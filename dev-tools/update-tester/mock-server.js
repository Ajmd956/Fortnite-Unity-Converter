const http = require('http')
const crypto = require('crypto')

const ASSET_NAME = 'Fortnite-Unity-Converter.exe'

function notesBody(n) {
  return [['Added', n.added], ['Changed', n.changed], ['Fixed', n.fixed]]
    .filter(s => s[1] && s[1].length)
    .map(s => '## ' + s[0] + '\n' + s[1].map(x => '- ' + x).join('\n'))
    .join('\n\n')
}

function createMock() {
  const state = {
    available: true,
    baseVersion: '2.0.0',
    sizeMb: 3,
    behavior: 'normal',
    history: []
  }
  const log = []
  const listeners = new Set()
  const bufs = new Map()
  let base = ''

  function note(entry) {
    const e = Object.assign({ t: Date.now() }, entry)
    log.push(e)
    if (log.length > 500) log.shift()
    listeners.forEach(f => { try { f(e) } catch (x) {} })
  }

  function assetBuf(mb) {
    if (!bufs.has(mb)) {
      const buf = Buffer.alloc(Math.round(mb * 1024 * 1024))
      for (let i = 0; i < buf.length; i += 4096) buf[i] = (i / 4096) & 255
      bufs.set(mb, { buf, digest: crypto.createHash('sha256').update(buf).digest('hex') })
    }
    return bufs.get(mb)
  }

  function releaseJson(h) {
    const a = assetBuf(h.sizeMb)
    const url = base + '/asset.exe'
    return {
      tag_name: 'v' + h.version,
      name: 'v' + h.version,
      draft: false,
      prerelease: false,
      body: notesBody(h.notes),
      html_url: base + '/releases/tag/v' + h.version,
      published_at: new Date(h.published).toISOString(),
      assets: h.noAsset ? [] : [{ name: ASSET_NAME, size: a.buf.length, digest: 'sha256:' + a.digest, browser_download_url: url, url }]
    }
  }

  function currentLatest() {
    if (state.available && state.history.length) {
      const h = state.history[0]
      return Object.assign({}, h, { noAsset: state.behavior === 'noasset' })
    }
    return { version: state.baseVersion, sizeMb: 1, published: Date.now() - 86400000, notes: { added: [], changed: ['Base version'], fixed: [] } }
  }

  function json(res, code, data) {
    const text = JSON.stringify(data)
    res.writeHead(code, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(text) })
    res.end(text)
  }

  function serveAsset(req, res) {
    if (state.behavior === 'http500') {
      res.writeHead(500)
      return res.end()
    }
    const top = state.history.length ? state.history[0] : { sizeMb: state.sizeMb }
    const a = assetBuf(top.sizeMb)
    let buf = a.buf
    if (state.behavior === 'badhash') {
      buf = Buffer.from(a.buf)
      buf[0] = buf[0] ^ 255
    }
    const m = /^bytes=(\d+)-(\d+)?$/.exec(req.headers.range || '')
    let from = 0
    let to = buf.length - 1
    let code = 200
    const head = { 'Content-Type': 'application/octet-stream', 'Accept-Ranges': 'bytes' }
    if (m) {
      from = Number(m[1])
      to = Math.min(buf.length - 1, m[2] === undefined ? buf.length - 1 : Number(m[2]))
      code = 206
      head['Content-Range'] = 'bytes ' + from + '-' + to + '/' + buf.length
    }
    head['Content-Length'] = to - from + 1
    res.writeHead(code, head)
    if (state.behavior !== 'slow') return res.end(buf.subarray(from, to + 1))
    let pos = from
    const step = () => {
      if (res.destroyed) return
      if (pos > to) return res.end()
      const end = Math.min(to + 1, pos + 65536)
      res.write(buf.subarray(pos, end))
      pos = end
      setTimeout(step, 100)
    }
    step()
  }

  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x')
    const p = url.pathname
    res.on('finish', () => note({ kind: 'request', method: req.method, path: req.url, status: res.statusCode, range: req.headers.range || '' }))
    let m
    if ((m = /^\/repos\/[^/]+\/[^/]+\/releases\/latest$/.exec(p))) return json(res, 200, releaseJson(currentLatest()))
    if (/^\/repos\/[^/]+\/[^/]+\/releases$/.test(p)) {
      const list = state.history.map(h => releaseJson(h))
      list.push(releaseJson(currentLatest()))
      const seen = new Set()
      return json(res, 200, list.filter(r => (seen.has(r.tag_name) ? false : seen.add(r.tag_name))))
    }
    if ((m = /^\/([^/]+)\/([^/]+)\/releases\/latest$/.exec(p))) {
      res.writeHead(302, { Location: '/' + m[1] + '/' + m[2] + '/releases/tag/v' + currentLatest().version })
      return res.end()
    }
    if (p === '/asset.exe') return serveAsset(req, res)
    res.writeHead(404)
    res.end()
  })

  function listen(port) {
    return new Promise((resolve, reject) => {
      let tries = 0
      const attempt = p => {
        server.once('error', e => {
          if (e.code === 'EADDRINUSE' && tries++ < 20) attempt(p + 1)
          else reject(e)
        })
        server.listen(p, '127.0.0.1', () => {
          base = 'http://127.0.0.1:' + server.address().port
          resolve(base)
        })
      }
      attempt(port)
    })
  }

  function publish(o) {
    const version = String(o.version || '').trim().replace(/^v/i, '')
    if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('The version must look like 2.0.5')
    const sizeMb = Math.min(60, Math.max(0.5, Number(o.sizeMb) || 3))
    const split = t => String(t || '').split('\n').map(x => x.trim()).filter(Boolean)
    const entry = {
      version,
      sizeMb,
      published: Date.now(),
      notes: { added: split(o.added), changed: split(o.changed), fixed: split(o.fixed) }
    }
    state.history = state.history.filter(h => h.version !== version)
    state.history.unshift(entry)
    state.history.sort((a, b) => cmp(b.version, a.version))
    state.available = true
    state.sizeMb = sizeMb
    if (o.behavior) state.behavior = o.behavior
    note({ kind: 'event', text: 'Published test release v' + version })
    return entry
  }

  function cmp(a, b) {
    const x = a.split('.').map(Number)
    const y = b.split('.').map(Number)
    for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] < y[i] ? -1 : 1
    return 0
  }

  return {
    state,
    log,
    listen,
    publish,
    close: () => server.close(),
    base: () => base,
    onLog: f => { listeners.add(f); return () => listeners.delete(f) },
    note,
    withdraw() {
      state.available = false
      note({ kind: 'event', text: 'Withdrew the test update, the latest release is v' + state.baseVersion })
    },
    reset() {
      state.history = []
      state.available = true
      state.behavior = 'normal'
      log.length = 0
      note({ kind: 'event', text: 'Cleared all test releases' })
    }
  }
}

module.exports = { createMock, notesBody }
