const fs = require('fs')
const path = require('path')
const crypto = require('crypto')

function parseVersion(v) {
  const m = String(v || '').trim().replace(/^v/i, '').match(/^(\d+(?:\.\d+)*)(?:[-+.]?(.*))?$/)
  if (!m) return null
  return { nums: m[1].split('.').map(Number), pre: m[2] || '' }
}

function compareVersions(a, b) {
  const x = parseVersion(a)
  const y = parseVersion(b)
  if (!x || !y) return 0
  const n = Math.max(x.nums.length, y.nums.length)
  for (let i = 0; i < n; i++) {
    const d = (x.nums[i] || 0) - (y.nums[i] || 0)
    if (d) return d < 0 ? -1 : 1
  }
  if (x.pre === y.pre) return 0
  if (!x.pre) return 1
  if (!y.pre) return -1
  return x.pre.localeCompare(y.pre, 'en', { numeric: true })
}

// Did the app just start on a newer version than the one that ran last time?
// A copy that has never recorded a version but already has settings was updated from an older build.
function wasUpdated(o) {
  if (!o || !o.packaged) return false
  if (o.prev) return compareVersions(o.cur, o.prev) > 0
  return !!o.hadSettings
}

function codeError(code, text) {
  const e = new Error(text)
  e.code = code
  return e
}

function createUpdater(opts) {
  const api = (opts.apiBase || 'https://api.github.com').replace(/\/$/, '')
  const web = (opts.webBase || 'https://github.com').replace(/\/$/, '')
  const state = { offered: null, downloading: false, ready: null }
  const auth = () => {
    const t = typeof opts.getToken === 'function' ? opts.getToken() : opts.token
    return t ? { Authorization: 'Bearer ' + String(t).trim() } : {}
  }

  async function check() {
    if (!opts.repo || !/^[\w.-]+\/[\w.-]+$/.test(opts.repo)) return null
    const res = await fetch(api + '/repos/' + opts.repo + '/releases/latest', {
      headers: Object.assign({ Accept: 'application/vnd.github+json', 'User-Agent': 'FortniteUnityConverter-Updater' }, auth()),
      signal: AbortSignal.timeout(15000)
    })
    if (res.status === 404) throw codeError('notfound', 'No release was found (the repository is private, renamed, or has no published release).')
    if (res.status === 403 || res.status === 429) throw codeError('ratelimit', 'GitHub is limiting update checks right now.')
    if (!res.ok) throw new Error('Update check failed (' + res.status + ').')
    const rel = await res.json()
    if (!rel || rel.draft || rel.prerelease || !rel.tag_name) return null
    if (compareVersions(rel.tag_name, opts.currentVersion) <= 0) return null
    const asset = (rel.assets || []).find(a => /\.exe$/i.test(a.name) && !/setup|installer/i.test(a.name))
    if (!asset) throw codeError('noasset', 'The latest release has no EXE file attached.')
    state.offered = {
      version: rel.tag_name,
      name: asset.name,
      size: asset.size || 0,
      url: auth().Authorization ? asset.url || asset.browser_download_url : asset.browser_download_url,
      digest: /^sha256:([0-9a-f]{64})$/i.test(asset.digest || '') ? asset.digest.slice(7).toLowerCase() : '',
      page: rel.html_url || '',
      notes: String(rel.body || '').replace(/\r/g, '').trim().slice(0, 4000),
      published: rel.published_at || ''
    }
    return state.offered
  }

  // Cheap check: follows no redirect and reads the latest release tag from the Location header.
  // This is not counted against the GitHub API limit, so it is safe to call every 30 seconds.
  async function peekLatest() {
    if (!opts.repo || !/^[\w.-]+\/[\w.-]+$/.test(opts.repo)) return null
    const res = await fetch(web + '/' + opts.repo + '/releases/latest', {
      redirect: 'manual',
      headers: { 'User-Agent': 'FortniteUnityConverter-Updater' },
      signal: AbortSignal.timeout(10000)
    })
    if (res.status >= 300 && res.status < 400) {
      const m = /\/releases\/tag\/([^/?#]+)/.exec(res.headers.get('location') || '')
      return m ? decodeURIComponent(m[1]) : null
    }
    if (res.status === 404) return null
    if (!res.ok) throw new Error('Update check failed (' + res.status + ').')
    return null
  }

  // What changed: the notes of every release after "from", up to and including "to" (newest first)
  async function notesSince(from, to) {
    if (!opts.repo || !/^[\w.-]+\/[\w.-]+$/.test(opts.repo)) return []
    const res = await fetch(api + '/repos/' + opts.repo + '/releases?per_page=30', {
      headers: Object.assign({ Accept: 'application/vnd.github+json', 'User-Agent': 'FortniteUnityConverter-Updater' }, auth()),
      signal: AbortSignal.timeout(10000)
    })
    if (!res.ok) throw new Error('Could not load the release notes (' + res.status + ').')
    const list = await res.json()
    const out = []
    for (const r of Array.isArray(list) ? list : []) {
      if (!r || r.draft || r.prerelease || !r.tag_name) continue
      if (compareVersions(r.tag_name, to) > 0) continue
      if (from ? compareVersions(r.tag_name, from) <= 0 : compareVersions(r.tag_name, to) !== 0) continue
      const notes = String(r.body || '').replace(/\r/g, '').trim()
      // older releases only said their own name; there is nothing to show for those
      if (!notes || /^Fortnite Unity Converter v?[\d.]+$/i.test(notes)) continue
      out.push({ version: r.tag_name, notes: notes.slice(0, 4000) })
      if (out.length >= 15) break
    }
    return out
  }

  const listeners = new Set()
  let lastProgress = null
  let inflight = null

  function emit(p) {
    lastProgress = p
    for (const f of listeners) {
      try { f(p) } catch (e) {}
    }
  }

  function targetFile() {
    return path.join(path.dirname(opts.exePath), path.basename(opts.exePath, '.exe') + '.update.exe')
  }

  function hashFile(file) {
    return new Promise((resolve, reject) => {
      const h = crypto.createHash('sha256')
      const s = fs.createReadStream(file)
      s.on('data', c => h.update(c))
      s.on('error', reject)
      s.on('end', () => resolve(h.digest('hex')))
    })
  }

  // A finished copy of this exact update from an earlier run or a background prefetch can be used as it is
  async function existingCopy(o, target) {
    try {
      const st = fs.statSync(target)
      if (!o.size || st.size !== o.size) return false
      if (o.digest && (await hashFile(target)) !== o.digest) return false
      return !!o.digest
    } catch (e) {
      return false
    }
  }

  function requestHeaders(extra) {
    return Object.assign({ 'User-Agent': 'FortniteUnityConverter-Updater', Accept: 'application/octet-stream' }, auth(), extra || {})
  }

  async function downloadStream(o, tmp) {
    const res = await fetch(o.url, { headers: requestHeaders(), redirect: 'follow' })
    if (!res.ok || !res.body) throw new Error('The download failed (' + res.status + ').')
    const total = Number(res.headers.get('content-length')) || o.size || 0
    const hash = crypto.createHash('sha256')
    const out = fs.createWriteStream(tmp)
    let got = 0
    let last = 0
    try {
      for await (const chunk of res.body) {
        hash.update(chunk)
        if (!out.write(chunk)) await new Promise(r => out.once('drain', r))
        got += chunk.length
        const now = Date.now()
        if (now - last > 100) {
          last = now
          emit({ got, total })
        }
      }
    } finally {
      await new Promise(r => out.end(r))
    }
    if (o.size && got !== o.size) throw new Error('The download was incomplete. Please try again.')
    if (o.digest && hash.digest('hex') !== o.digest) throw new Error('The download did not match its checksum. Please try again.')
    return got
  }

  // Several connections each fetch a slice of the file; this is usually much faster than one stream
  async function downloadRanged(o, tmp) {
    const total = o.size
    const slice = 6 * 1024 * 1024
    const count = Math.ceil(total / slice)
    const fh = await fs.promises.open(tmp, 'w')
    let next = 0
    let got = 0
    let last = 0
    let failure = null
    try {
      await fh.truncate(total)
      const worker = async () => {
        while (!failure) {
          const i = next++
          if (i >= count) return
          const from = i * slice
          const to = Math.min(total, from + slice) - 1
          let done = false
          for (let attempt = 0; attempt < 3 && !done && !failure; attempt++) {
            try {
              const res = await fetch(o.url, { headers: requestHeaders({ Range: 'bytes=' + from + '-' + to }), redirect: 'follow', signal: AbortSignal.timeout(120000) })
              if (res.status !== 206) throw codeError('norange', 'The server did not accept ranged downloads.')
              const buf = Buffer.from(await res.arrayBuffer())
              if (buf.length !== to - from + 1) continue
              await fh.write(buf, 0, buf.length, from)
              got += buf.length
              const now = Date.now()
              if (now - last > 100) {
                last = now
                emit({ got, total })
              }
              done = true
            } catch (e) {
              if (e && e.code === 'norange') {
                failure = e
                return
              }
            }
          }
          if (!done && !failure) failure = new Error('A part of the download failed.')
        }
      }
      await Promise.all(Array.from({ length: Math.min(6, count) }, worker))
    } finally {
      await fh.close()
    }
    if (failure) throw failure
    if (got !== total) throw new Error('The download was incomplete. Please try again.')
    if (o.digest && (await hashFile(tmp)) !== o.digest) throw new Error('The download did not match its checksum. Please try again.')
    return got
  }

  function download(onProgress) {
    const o = state.offered
    if (!o) return Promise.reject(new Error('No update to download.'))
    if (!opts.exePath) return Promise.reject(new Error('Updating only works from the built app.'))
    if (onProgress) {
      listeners.add(onProgress)
      if (lastProgress) { try { onProgress(lastProgress) } catch (e) {} }
    }
    if (inflight && inflight.version === o.version) {
      return inflight.promise.finally(() => listeners.delete(onProgress))
    }
    if (state.ready && state.readyVersion === o.version && fs.existsSync(state.ready)) {
      if (onProgress) { try { onProgress({ got: o.size, total: o.size }) } catch (e) {} listeners.delete(onProgress) }
      return Promise.resolve(state.ready)
    }
    // Updates only ever come from GitHub, over a secure connection
    try {
      const u = new URL(o.url)
      const host = u.hostname.toLowerCase()
      const home = new URL(api).hostname.toLowerCase()
      if (u.protocol !== 'https:' && host !== home) throw new Error('bad scheme')
      if (host !== home && !/(^|\.)(github\.com|githubusercontent\.com)$/.test(host)) throw new Error('bad host')
    } catch (e) {
      listeners.delete(onProgress)
      return Promise.reject(new Error('The update came from an unexpected place, so it was not downloaded.'))
    }
    const promise = (async () => {
      state.downloading = true
      lastProgress = null
      const target = targetFile()
      const tmp = target + '.part'
      try {
        if (await existingCopy(o, target)) {
          state.ready = target
          state.readyVersion = o.version
          emit({ got: o.size, total: o.size })
          return target
        }
        let got
        if (o.size >= 16 * 1024 * 1024) {
          try {
            got = await downloadRanged(o, tmp)
          } catch (e) {
            try { fs.rmSync(tmp, { force: true }) } catch (x) {}
            if (e && e.code !== 'norange' && /checksum/.test(String(e.message))) throw e
            got = await downloadStream(o, tmp)
          }
        } else {
          got = await downloadStream(o, tmp)
        }
        if (fs.existsSync(target)) fs.rmSync(target, { force: true })
        fs.renameSync(tmp, target)
        emit({ got, total: o.size || got })
        state.ready = target
        state.readyVersion = o.version
        return target
      } catch (e) {
        try { fs.rmSync(tmp, { force: true }) } catch (x) {}
        throw e
      } finally {
        state.downloading = false
      }
    })()
    inflight = { version: o.version, promise }
    const clear = () => {
      if (inflight && inflight.promise === promise) inflight = null
    }
    promise.then(clear, clear)
    return promise.finally(() => listeners.delete(onProgress))
  }

  function discardDownloaded() {
    state.ready = null
    state.readyVersion = null
    try { fs.rmSync(targetFile(), { force: true }) } catch (e) {}
    try { fs.rmSync(targetFile() + '.part', { force: true }) } catch (e) {}
  }

  function applyScript(newExe, targetExe) {
    const script = path.join(opts.tempDir, 'fuc-update-' + process.pid + '.cmd')
    const q = s => '"' + String(s).replace(/"/g, '') + '"'
    const text = [
      '@echo off',
      'setlocal',
      'set /a n=0',
      ':retry',
      'move /y ' + q(newExe) + ' ' + q(targetExe) + ' >nul 2>&1',
      'if not errorlevel 1 goto done',
      'set /a n+=1',
      'if %n% geq 90 goto fail',
      'ping -n 2 127.0.0.1 >nul',
      'goto retry',
      ':done',
      'start "" ' + q(targetExe),
      'goto end',
      ':fail',
      'del /f /q ' + q(newExe) + ' >nul 2>&1',
      ':end',
      '(goto) 2>nul & del /f /q "%~f0"',
      ''
    ].join('\r\n')
    fs.writeFileSync(script, text, 'utf8')
    return script
  }

  return { check, peekLatest, download, discardDownloaded, applyScript, notesSince, state }
}

module.exports = { createUpdater, compareVersions, wasUpdated }
