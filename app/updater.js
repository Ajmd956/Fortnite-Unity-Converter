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

function createUpdater(opts) {
  const api = (opts.apiBase || 'https://api.github.com').replace(/\/$/, '')
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
    if (res.status === 404) return null
    if (!res.ok) throw new Error('Update check failed (' + res.status + ').')
    const rel = await res.json()
    if (!rel || rel.draft || rel.prerelease || !rel.tag_name) return null
    if (compareVersions(rel.tag_name, opts.currentVersion) <= 0) return null
    const asset = (rel.assets || []).find(a => /\.exe$/i.test(a.name) && !/setup|installer/i.test(a.name))
    if (!asset) return null
    state.offered = {
      version: rel.tag_name,
      name: asset.name,
      size: asset.size || 0,
      url: auth().Authorization ? asset.url || asset.browser_download_url : asset.browser_download_url,
      digest: /^sha256:([0-9a-f]{64})$/i.test(asset.digest || '') ? asset.digest.slice(7).toLowerCase() : '',
      page: rel.html_url || '',
      notes: String(rel.body || '').replace(/\r/g, '').trim().slice(0, 1200),
      published: rel.published_at || ''
    }
    return state.offered
  }

  async function download(onProgress) {
    const o = state.offered
    if (!o) throw new Error('No update to download.')
    if (state.downloading) throw new Error('Already downloading.')
    if (!opts.exePath) throw new Error('Updating only works from the built app.')
    // Updates only ever come from GitHub, over a secure connection
    try {
      const u = new URL(o.url)
      const host = u.hostname.toLowerCase()
      const home = new URL(api).hostname.toLowerCase()
      if (u.protocol !== 'https:' && host !== home) throw new Error('bad scheme')
      if (host !== home && !/(^|\.)(github\.com|githubusercontent\.com)$/.test(host)) throw new Error('bad host')
    } catch (e) {
      throw new Error('The update came from an unexpected place, so it was not downloaded.')
    }
    state.downloading = true
    const target = path.join(path.dirname(opts.exePath), path.basename(opts.exePath, '.exe') + '.update.exe')
    const tmp = target + '.part'
    try {
      const res = await fetch(o.url, { headers: Object.assign({ 'User-Agent': 'FortniteUnityConverter-Updater', Accept: 'application/octet-stream' }, auth()), redirect: 'follow' })
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
          if (onProgress && now - last > 100) {
            last = now
            onProgress({ got, total })
          }
        }
      } finally {
        await new Promise(r => out.end(r))
      }
      if (o.size && got !== o.size) throw new Error('The download was incomplete. Please try again.')
      if (o.digest && hash.digest('hex') !== o.digest) throw new Error('The download did not match its checksum. Please try again.')
      if (fs.existsSync(target)) fs.rmSync(target, { force: true })
      fs.renameSync(tmp, target)
      if (onProgress) onProgress({ got, total: total || got })
      state.ready = target
      return target
    } catch (e) {
      try { fs.rmSync(tmp, { force: true }) } catch (x) {}
      throw e
    } finally {
      state.downloading = false
    }
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

  return { check, download, applyScript, state }
}

module.exports = { createUpdater, compareVersions }
