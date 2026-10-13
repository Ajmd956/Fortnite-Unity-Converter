const { app, BrowserWindow, ipcMain, dialog, shell, clipboard, nativeTheme, screen, nativeImage, session, safeStorage } = require('electron')
const meshThumb = require('./meshthumb')
const { glbToFbx } = require('./glb2fbx')
const crypto = require('crypto')
const path = require('path')
const fs = require('fs')
const net = require('net')
const { spawn, spawnSync } = require('child_process')
const store = require('./store')
const { createUpdater, wasUpdated } = require('./updater')
const { createAi } = require('./ai')

const appDir = app.isPackaged ? (process.env.PORTABLE_EXECUTABLE_DIR || path.dirname(process.execPath)) : __dirname
const root = path.join(app.getPath('documents'), 'Fortnite Unity Converter')
const conversionsDir = path.join(root, 'Shader Conversions')
const oldConversionsDir = path.join(root, 'Fortnite Conversions')
const particlesDir = path.join(root, 'Particle Conversions')
const cacheDir = path.join(root, 'Cache')
const exportDefault = path.join(root, 'Exports')
const settingsFile = path.join(root, 'settings.json')

function exportDirNow() {
  const p = settings && settings.useExportsPath ? String(settings.exportsPath || '').trim() : ''
  return p ? path.resolve(p) : exportDefault
}

let win = null
let quitting = false
let settings = store.defaults
let current = null
let busy = false
let stopped = false
let buildingFiles = false
let fileIndex = null
let fileDirs = null
let fileContainers = []
let fileBuiltAt = 0

function migrateOldData() {
  const marker = path.join(root, '.migrated')
  if (path.resolve(appDir) === path.resolve(root) || fs.existsSync(marker)) return
  try {
    fs.mkdirSync(root, { recursive: true })
    for (const name of ['Fortnite Conversions', 'Cache', 'settings.json']) {
      const from = path.join(appDir, name)
      if (fs.existsSync(from)) {
        fs.cpSync(from, path.join(root, name === 'Fortnite Conversions' ? 'Shader Conversions' : name), { recursive: true, force: false, errorOnExist: false })
      }
    }
    fs.writeFileSync(marker, '', 'utf8')
  } catch (e) {}
}

function ensureFolders() {
  try {
    if (fs.existsSync(oldConversionsDir) && !fs.existsSync(conversionsDir)) fs.renameSync(oldConversionsDir, conversionsDir)
  } catch (e) {}
  fs.mkdirSync(conversionsDir, { recursive: true })
  fs.mkdirSync(particlesDir, { recursive: true })
  fs.mkdirSync(cacheDir, { recursive: true })
}

function send(channel, data) {
  if (win && !win.isDestroyed()) win.webContents.send(channel, data)
}

const gameDir = path.join(root, 'Game Files')
const pathsFile = path.join(gameDir, 'paths2.txt')

function helperPath() {
  if (app.isPackaged) return path.join(process.resourcesPath, 'engine', 'FortniteFileLister.exe')
  return path.join(__dirname, 'engine', 'bin', 'FortniteFileLister.exe')
}

let archiveMemo = { key: null, at: 0, val: '' }
function findArchive() {
  const key = settings.fortniteArchive || ''
  if (archiveMemo.key === key && archiveMemo.val && Date.now() - archiveMemo.at < 30000) return archiveMemo.val
  const v = findArchiveNow()
  archiveMemo = { key, at: Date.now(), val: v }
  return v
}

function findArchiveNow() {
  const chosen = settings.fortniteArchive
  if (chosen && fs.existsSync(chosen)) return chosen
  try {
    const dat = path.join(process.env.ProgramData || 'C:\\ProgramData', 'Epic', 'UnrealEngineLauncher', 'LauncherInstalled.dat')
    const list = JSON.parse(fs.readFileSync(dat, 'utf8')).InstallationList || []
    const game = list.find(x => String(x.AppName).toLowerCase() === 'fortnite')
    if (game) {
      const paks = path.join(game.InstallLocation, 'FortniteGame', 'Content', 'Paks')
      if (fs.existsSync(paks)) return paks
    }
  } catch (e) {}
  for (const root of programRoots()) {
    for (const rel of [['Epic Games', 'Fortnite'], ['Fortnite']]) {
      const paks = path.join(root, ...rel, 'FortniteGame', 'Content', 'Paks')
      if (fs.existsSync(paks)) return paks
    }
  }
  return ''
}

// Program Files folders on the system drive first, then the same folders on the other drives
function programRoots() {
  const out = []
  const add = p => { if (p && !out.includes(p)) out.push(p) }
  add(process.env.ProgramFiles)
  add(process.env['ProgramFiles(x86)'])
  add('C:\\Program Files')
  for (let c = 68; c <= 90; c++) {
    const d = String.fromCharCode(c) + ':\\'
    add(d + 'Program Files')
    add(d + 'Epic Games')
    add(d + 'Games')
  }
  return out.filter(p => { try { return fs.existsSync(p) } catch (e) { return false } })
}

const PREFERRED_UNITY = '2022.3.22f1'

function versionKey(v) {
  const m = String(v).match(/^(\d+)\.(\d+)\.(\d+)([a-z])(\d+)/i)
  return m ? [+m[1], +m[2], +m[3], m[4].toLowerCase().charCodeAt(0), +m[5]] : [0, 0, 0, 0, 0]
}

function compareVersions(a, b) {
  const x = versionKey(a), y = versionKey(b)
  for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return x[i] - y[i]
  return 0
}

function findUnityNow() {
  const chosen = settings.unity
  if (chosen && fs.existsSync(chosen)) return chosen
  const found = []
  const hubDirs = []
  for (const root of programRoots()) {
    hubDirs.push(path.join(root, 'Unity', 'Hub', 'Editor'))
    hubDirs.push(path.join(root, 'Unity Hub', 'Editor'))
    const single = path.join(root, 'Unity', 'Editor', 'Unity.exe')
    if (fs.existsSync(single)) found.push({ v: '0', exe: single })
  }
  for (const dir of hubDirs) {
    let names = []
    try { names = fs.readdirSync(dir) } catch (e) { continue }
    for (const n of names) {
      const exe = path.join(dir, n, 'Editor', 'Unity.exe')
      if (fs.existsSync(exe)) found.push({ v: n, exe })
    }
  }
  if (!found.length) return ''
  const want = found.find(f => f.v === PREFERRED_UNITY)
  if (want) return want.exe
  found.sort((a, b) => compareVersions(b.v, a.v))
  return found[0].exe
}

// Fill in the Fortnite and Unity locations on its own when they are empty or no longer exist
function autoDetectPaths() {
  let changed = false
  try {
    if (!settings.fortniteArchive || !fs.existsSync(settings.fortniteArchive)) {
      const a = findArchiveNow()
      if (a && a !== settings.fortniteArchive) { settings.fortniteArchive = a; changed = true }
    }
    if (!settings.unity || !fs.existsSync(settings.unity)) {
      const u = findUnityNow()
      if (u && u !== settings.unity) { settings.unity = u; changed = true }
    }
  } catch (e) {}
  if (changed) { try { store.save(settingsFile, settings) } catch (e) {} }
  return changed
}

const collator =new Intl.Collator('en', { numeric: true, sensitivity: 'base' })

function typeOf(p) {
  const n = p.slice(p.lastIndexOf('/') + 1).split('.')[0].toLowerCase()
  if (/skeleton|^skel_/.test(n)) return 'Skeleton'
  if (/^(sm|sk|skm)_/.test(n)) return 'Mesh'
  if (/^mi_/.test(n)) return 'MaterialInstance'
  if (/^mf_/.test(n)) return 'MaterialFunction'
  if (/^m_/.test(n)) return 'Material'
  if (/^mpc_/.test(n)) return 'MaterialParameterCollection'
  if (/^t_/.test(n)) return 'Texture'
  if (/^(pa|pose)_|poseasset/.test(n)) return 'PoseAsset'
  if (/^(sw|sc|snd|sound)_|soundwave|soundcue/.test(n)) return 'Sound'
  if (/font/.test(n)) return 'Font'
  if (/^(anim|am|as)_|montage|animsequence/.test(n)) return 'Animation'
  if (/\/maps?\//i.test(p)) return 'Map'
  return 'Other'
}

const typesFile = path.join(gameDir, 'types.json')
const typeCache = new Map()
const typeMiss = new Set()
const typeInflight = new Map()
const typeWaiters = new Map()
let typeCacheDirty = false
let typeServer = null
let typeServerReady = null
let typeServerDead = false
let apiState = { state: 'off', text: '' }
const exportWaiters = new Map()

function catOf(c) {
  if (!c) return ''
  if (c === 'Material') return 'Material'
  if (/^MaterialFunction/.test(c)) return 'MaterialFunction'
  if (/MaterialInstance/.test(c)) return 'MaterialInstance'
  if (c === 'MaterialParameterCollection') return 'MaterialParameterCollection'
  if (/Texture/.test(c)) return 'Texture'
  if (c === 'StaticMesh' || c === 'SkeletalMesh') return 'Mesh'
  if (c === 'Skeleton') return 'Skeleton'
  if (c === 'PoseAsset') return 'PoseAsset'
  if (/^(Anim|BlendSpace)/.test(c) && !/Blueprint/.test(c)) return 'Animation'
  if (/^Sound/.test(c)) return 'Sound'
  if (/^Font/.test(c)) return 'Font'
  if (c === 'World') return 'Map'
  return 'Other'
}

let typeCacheLoading = null
function loadTypeCache() {
  if (!typeCacheLoading) {
    typeCacheLoading = fs.promises.readFile(typesFile, 'utf8').then(t => {
      const obj = JSON.parse(t)
      for (const k in obj) if (!typeCache.has(k)) typeCache.set(k, obj[k])
    }).catch(() => {})
  }
  return typeCacheLoading
}

function saveTypeCache() {
  if (!typeCacheDirty) return
  typeCacheDirty = false
  try {
    fs.mkdirSync(gameDir, { recursive: true })
    const data = JSON.stringify(Object.fromEntries(typeCache))
    if (quitting) fs.writeFileSync(typesFile, data, 'utf8')
    else fs.promises.writeFile(typesFile, data, 'utf8').catch(() => {})
  } catch (e) {}
}

function stopTypeServer() {
  if (typeServer) {
    try {
      typeServer.kill()
    } catch (e) {}
  }
  typeServer = null
  typeServerReady = null
}

function setApi(state, text) {
  apiState = { state, text: text || '' }
  send('api:state', apiState)
}

function bridgePort() {
  const t = parseBridge(settings.bridge)
  return t && t.port ? t.port : 24320
}

function startTypeServer() {
  if (typeServerReady) return typeServerReady
  const helper = helperPath()
  const archive = findArchive()
  if (!fs.existsSync(helper) || !archive) return Promise.resolve(false)
  fs.mkdirSync(gameDir, { recursive: true })
  typeServerReady = new Promise(resolve => {
    let settled = false
    let buf = ''
    let child
    const finish = ok => {
      if (settled) return
      settled = true
      resolve(ok)
    }
    setApi('starting', 'Starting')
    try {
      child = spawn(helper, ['--serve', '--archive', archive, '--cache', gameDir, '--port', String(bridgePort())], { windowsHide: true })
    } catch (e) {
      setApi('off', '')
      finish(false)
      return
    }
    typeServer = child
    child.stdout.on('data', d => {
      buf += d.toString('utf8')
      const lines = buf.split(/\r?\n/)
      buf = lines.pop()
      for (const line of lines) {
        if (line === 'ready') finish(true)
        else if (line.startsWith('status: ')) setApi('starting', line.slice(8))
        else if (line.startsWith('api: ready')) setApi('ready', '')
        else if (line.startsWith('api: unavailable')) {
          pingBridge().then(ok => setApi(ok ? 'external' : 'failed', line.slice(17)))
        } else if (line.startsWith('error: ')) {
          setApi('failed', line.slice(7))
          finish(false)
        } else if (line.startsWith('type\t')) {
          const parts = line.split('\t')
          const w = typeWaiters.get(parts[1])
          if (w) {
            typeWaiters.delete(parts[1])
            w(parts[2] || '?')
          }
        } else if (line.startsWith('export\t')) {
          const parts = line.split('\t')
          const key = parts[1] + '\t' + parts[2]
          const w = exportWaiters.get(key)
          if (w) {
            exportWaiters.delete(key)
            w(parts[3] === 'ok' ? { ok: true, file: parts.slice(4).join('\t') } : { ok: false, error: parts.slice(4).join('\t') })
          }
        }
      }
    })
    child.stderr.on('data', () => {})
    child.on('error', () => {
      setApi('failed', 'The helper could not start')
      finish(false)
    })
    child.on('close', () => {
      finish(false)
      typeWaiters.forEach(w => w('?'))
      typeWaiters.clear()
      exportWaiters.forEach(w => w({ ok: false, error: 'the helper stopped' }))
      exportWaiters.clear()
      typeInflight.clear()
      if (typeServer === child) {
        typeServer = null
        typeServerReady = null
        if (apiState.state !== 'external') setApi('off', '')
      }
    })
  })
  return typeServerReady
}

function askType(p) {
  if (typeInflight.has(p)) return typeInflight.get(p)
  const key = p.slice(1)
  const pr = new Promise(resolve => {
    if (!typeServer) {
      resolve('?')
      return
    }
    typeWaiters.set(key, resolve)
    try {
      typeServer.stdin.write(key + '\n')
    } catch (e) {
      typeWaiters.delete(key)
      resolve('?')
    }
  }).then(c => {
    typeInflight.delete(p)
    return c
  })
  typeInflight.set(p, pr)
  return pr
}

function kindOf(p) {
  const c = typeCache.get(p)
  return c ? catOf(c) : typeOf(p)
}


function makeMatcher(search, regex) {
  const text = String(search || '').trim()
  if (!text) return null
  if (regex) {
    let re
    try {
      re = new RegExp(text, 'i')
    } catch (e) {
      return () => false
    }
    return s => re.test(s)
  }
  const tokens = text.toLowerCase().split(/\s+/)
  return (s, lower) => {
    const l = lower || s.toLowerCase()
    return tokens.every(t => l.includes(t))
  }
}

const yieldLoop = () => new Promise(r => setImmediate(r))
const CHUNK = 8000

async function buildDirs(lines, vfs) {
  const dirs = new Map()
  const node = key => {
    let n = dirs.get(key)
    if (!n) {
      n = { folders: new Set(), files: [], vfs: vfs ? new Set() : null, fileVfs: vfs ? [] : null }
      dirs.set(key, n)
    }
    return n
  }
  node('')
  for (let i = 0; i < lines.length; i++) {
    if (i % CHUNK === CHUNK - 1) await yieldLoop()
    const parts = lines[i].split('/')
    const v = vfs ? vfs[i] : 0
    let key = ''
    for (let j = 1; j < parts.length - 1; j++) {
      const n = node(key)
      n.folders.add(parts[j])
      if (vfs) n.vfs.add(v)
      key = key + '/' + parts[j]
    }
    const leaf = node(key)
    if (vfs) leaf.vfs.add(v)
    leaf.files.push(parts[parts.length - 1])
    if (vfs) leaf.fileVfs.push(v)
  }
  return dirs
}

let indexLoading = null
let indexGen = 0

function ensureIndex(force) {
  if (force) { indexGen++; indexLoading = null; fileIndex = null; fileDirs = null }
  if (fileIndex && fileDirs) return Promise.resolve()
  if (!indexLoading) indexLoading = loadIndex().finally(() => { indexLoading = null })
  return indexLoading
}

async function loadIndex() {
  const gen = indexGen
  try {
    const [text] = await Promise.all([fs.promises.readFile(pathsFile, 'utf8'), loadTypeCache()])
    await yieldLoop()
    const raw = []
    let pos = 0
    while (pos < text.length) {
      let nl = text.indexOf('\n', pos)
      if (nl < 0) nl = text.length
      let line = text.slice(pos, nl)
      if (line.charCodeAt(line.length - 1) === 13) line = line.slice(0, -1)
      if (line) {
        raw.push(line)
        if (raw.length % CHUNK === 0) await yieldLoop()
      }
      pos = nl + 1
    }
    if (!raw.length || raw[0].startsWith('/')) throw new Error('old list')
    let sorted = true
    for (let i = 1; i < raw.length; i++) {
      if (i % (CHUNK * 4) === 0) await yieldLoop()
      if (raw[i - 1] > raw[i]) { sorted = false; break }
    }
    if (!sorted) { raw.sort(); await yieldLoop() }
    const hasVfs = raw[0].includes('\t')
    const lines = new Array(raw.length)
    let vfs = null
    let containers = []
    if (hasVfs) {
      vfs = new Uint16Array(raw.length)
      const ids = new Map()
      for (let i = 0; i < raw.length; i++) {
        if (i % CHUNK === CHUNK - 1) await yieldLoop()
        const cut = raw[i].indexOf('\t')
        lines[i] = '/' + (cut < 0 ? raw[i] : raw[i].slice(0, cut))
        const name = cut < 0 ? '' : raw[i].slice(cut + 1)
        let id = ids.get(name)
        if (id === undefined) {
          id = containers.length
          ids.set(name, id)
          containers.push({ id, name, count: 0 })
        }
        containers[id].count++
        vfs[i] = id
      }
      containers.sort((a, b) => collator.compare(a.name, b.name))
    } else {
      for (let i = 0; i < raw.length; i++) {
        if (i % (CHUNK * 4) === 0) await yieldLoop()
        lines[i] = '/' + raw[i]
      }
    }
    const index = new Array(lines.length)
    for (let i = 0; i < lines.length; i++) {
      if (i % CHUNK === CHUNK - 1) await yieldLoop()
      const p = lines[i]
      index[i] = { p, l: p.toLowerCase(), k: typeOf(p), v: vfs ? vfs[i] : -1 }
    }
    const dirs = await buildDirs(lines, vfs)
    const stamp = (await fs.promises.stat(pathsFile)).mtimeMs
    if (gen !== indexGen) return
    fileContainers = containers
    fileIndex = index
    fileDirs = dirs
    fileBuiltAt = stamp
  } catch (e) {
    if (gen !== indexGen) return
    fileIndex = null
    fileDirs = null
    fileContainers = []
    fileBuiltAt = 0
  }
}

function engineCommand() {
  if (app.isPackaged) {
    return { cmd: path.join(process.resourcesPath, 'engine', 'FortniteUnityPorter.exe'), pre: [] }
  }
  return {
    cmd: process.platform === 'win32' ? 'python' : 'python3',
    pre: [path.join(__dirname, 'engine', 'FortniteUnityPorter.py')]
  }
}

function runEngine(args) {
  return new Promise(resolve => {
    const { cmd, pre } = engineCommand()
    const env = Object.assign({}, process.env, { PYTHONUNBUFFERED: '1', PYTHONIOENCODING: 'utf-8' })
    if (settings.unity) env.UNITY_EDITOR = settings.unity
    let output = ''
    let child
    if (app.isPackaged && !fs.existsSync(cmd)) {
      send('engine:log', 'The conversion engine (FortniteUnityPorter.exe) is missing from the app. Windows Security may have removed it. Open Windows Security, Virus & threat protection, Protection history, restore FortniteUnityPorter.exe, add an exclusion for this app, then start it again.\n')
      resolve({ code: -1, output })
      return
    }
    try {
      child = spawn(cmd, pre.concat(args), { env, windowsHide: true })
    } catch (e) {
      send('engine:log', 'could not start the engine: ' + e.message + '\n')
      resolve({ code: -1, output })
      return
    }
    current = child
    const onData = d => {
      const t = d.toString('utf8')
      output += t
      send('engine:log', t)
    }
    child.stdout.on('data', onData)
    child.stderr.on('data', onData)
    child.on('error', e => {
      send('engine:log', 'could not start the engine: ' + e.message + '\n')
      current = null
      resolve({ code: -1, output })
    })
    child.on('close', code => {
      current = null
      resolve({ code, output })
    })
  })
}

function pingBridge() {
  return new Promise(resolve => {
    const target = parseBridge(settings.bridge)
    if (!target) return resolve(false)
    const sock = net.createConnection(target)
    const done = ok => {
      sock.destroy()
      resolve(ok)
    }
    sock.setTimeout(800)
    sock.on('connect', () => done(true))
    sock.on('timeout', () => done(false))
    sock.on('error', () => done(false))
  })
}

function findFnp() {
  if (settings.fnpPath && fs.existsSync(settings.fnpPath)) return settings.fnpPath
  const names = ['FortnitePorting.exe', 'FortnitePorting.Avalonia.exe']
  const bases = [
    process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, 'FortnitePorting'),
    process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, 'Programs', 'FortnitePorting'),
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'FortnitePorting'),
    process.env.USERPROFILE && path.join(process.env.USERPROFILE, 'Desktop', 'FortnitePorting'),
    process.env.USERPROFILE && path.join(process.env.USERPROFILE, 'Downloads', 'FortnitePorting')
  ].filter(Boolean)
  for (const b of bases) {
    for (const n of names) {
      const p = path.join(b, n)
      if (fs.existsSync(p)) return p
    }
  }
  return ''
}

function launchFnp(exe) {
  const dir = path.dirname(exe)
  try {
    if (process.platform === 'win32' && settings.minimizeFnp) {
      const env = Object.assign({}, process.env, { FNP_EXE: exe, FNP_DIR: dir })
      const ps = "Start-Process -FilePath $env:FNP_EXE -WorkingDirectory $env:FNP_DIR -WindowStyle Minimized"
      const c = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', ps], { env, windowsHide: true, stdio: 'ignore' })
      c.on('error', () => {})
      c.unref()
    } else {
      const c = spawn(exe, [], { cwd: dir, detached: true, stdio: 'ignore' })
      c.on('error', () => {})
      c.unref()
    }
    return true
  } catch (e) {
    return false
  }
}

async function ensureFnp() {
  if (await pingBridge()) return { state: 'running' }
  if (!settings.autoStartFnp) return { state: 'off' }
  const exe = findFnp()
  if (!exe) return { state: 'nopath' }
  if (!launchFnp(exe)) return { state: 'failed', exe }
  for (let i = 0; i < 40; i++) {
    await new Promise(r => setTimeout(r, 750))
    if (await pingBridge()) return { state: 'started', exe }
  }
  return { state: 'timeout', exe }
}

function classifyFailure(output) {
  if (/HTTP Error 5\d\d/i.test(output)) return 'error'
  if (/Connection refused|actively refused|urlopen error|timed out|Remote end closed|Connection reset|Connection aborted|getaddrinfo/i.test(output)) return 'unreachable'
  if (/HTTP Error 4\d\d/i.test(output)) return 'missing'
  return ''
}

function noticeFor(kinds) {
  if (kinds.includes('unreachable')) return {
    title: 'The API stopped responding',
    body: 'The connection to the API dropped while converting. Try again in a moment.'
  }
  if (kinds.includes('error')) return {
    title: "Looks like Fortnite's down",
    body: "The API couldn't load that material, which usually means Fortnite's data isn't available right now. Shaders can't be converted at this time. Please try again later."
  }
  if (kinds.includes('missing')) return {
    title: "Couldn't find that material",
    body: "The API is running but doesn't know that path. Check the path, and make sure the API has finished loading, then try again."
  }
  return null
}

function parseBridge(url) {
  try {
    const u = new URL(url)
    return { host: u.hostname, port: Number(u.port) || 80 }
  } catch (e) {
    return null
  }
}

async function listShaders() {
  ensureFolders()
  const ents = (await fs.promises.readdir(conversionsDir, { withFileTypes: true })).filter(e => e.isDirectory())
  const rows = await Promise.all(ents.map(async ent => {
    const dir = path.join(conversionsDir, ent.name)
    const shader = path.join(dir, ent.name + '.shader')
    const cache = path.join(cacheDir, ent.name)
    const [st, tex, hasCache] = await Promise.all([
      fs.promises.stat(shader).catch(() => null),
      fs.promises.readdir(path.join(dir, 'Textures')).catch(() => []),
      fs.promises.access(cache).then(() => true, () => false)
    ])
    if (!st) return null
    return { name: ent.name, dir, shader, textures: tex.length, hasCache, cache, modified: st.mtimeMs }
  }))
  const out = rows.filter(Boolean)
  out.sort((a, b) => b.modified - a.modified)
  return out
}

function createWindow() {
  const wa = screen.getPrimaryDisplay().workAreaSize
  win = new BrowserWindow({
    width: Math.min(1600, wa.width),
    height: Math.min(900, wa.height),
    minWidth: 1000,
    minHeight: 660,
    frame: false,
    show: false,
    backgroundColor: '#0D0C12',
    autoHideMenuBar: true,
    title: 'Fortnite Unity Converter',
    icon: path.join(__dirname, 'assets', 'icon.ico'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: false
    }
  })
  // The window only ever shows the app's own page: no pop-ups, and no navigating away from it (a dropped file would otherwise open in it)
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  win.webContents.on('will-navigate', e => e.preventDefault())
  win.once('ready-to-show', () => {
    win.show()
    loadTypeCache().then(() => { if (fs.existsSync(pathsFile)) ensureIndex().catch(() => {}) })
    startUpdateChecks()
  })
  win.on('closed', () => { previewWins.forEach(w => { if (!w.isDestroyed()) w.destroy() }) })
  win.on('maximize', () => send('win:state', true))
  win.on('unmaximize', () => send('win:state', false))
  win.loadFile(path.join(__dirname, 'src', 'index.html'))
}

/* ---------- updates ---------- */
let updater = null
let updateOffered = null
let updateTimer = null

function updateExePath() {
  if (!app.isPackaged) return ''
  return process.env.PORTABLE_EXECUTABLE_FILE || process.execPath
}

function initUpdater() {
  if (updater) return updater
  let repo = ''
  try { repo = String(JSON.parse(fs.readFileSync(path.join(__dirname, 'update.json'), 'utf8')).repo || '').trim() } catch (e) {}
  updater = createUpdater({ repo, currentVersion: app.getVersion(), exePath: updateExePath(), tempDir: app.getPath('temp'), getToken: () => settings.updateToken })
  return updater
}

function offerPayload(o) {
  return { version: o.version, name: o.name, size: o.size, notes: o.notes, published: o.published, current: app.getVersion() }
}

let updateRetries = 0

async function runUpdateCheck(manual) {
  if (!updateExePath()) return { status: 'unsupported' }
  if (!manual && settings.checkUpdates === false) return { status: 'off' }
  try {
    const found = await initUpdater().check()
    updateRetries = 0
    if (!found) return { status: 'latest', current: app.getVersion() }
    const isNew = !updateOffered || updateOffered.version !== found.version
    updateOffered = found
    if (isNew || manual) send('update:available', Object.assign({ manual: !!manual }, offerPayload(found)))
    return { status: 'available', version: found.version, current: app.getVersion() }
  } catch (e) {
    if (!manual && updateRetries < 3) {
      updateRetries++
      setTimeout(() => runUpdateCheck(false), 5 * 60 * 1000)
    }
    return { status: 'error', current: app.getVersion() }
  }
}

function startUpdateChecks() {
  const exe = updateExePath()
  if (exe) fs.promises.rm(path.join(path.dirname(exe), path.basename(exe, '.exe') + '.update.exe'), { force: true }).catch(() => {})
  setTimeout(() => runUpdateCheck(false), 6000)
  if (!updateTimer) updateTimer = setInterval(() => runUpdateCheck(false), 6 * 60 * 60 * 1000)
}

// Set at startup when this is the first run after an update; the page asks for it once and then it is gone
let justUpdated = null
let updatedMeta = null

ipcMain.handle('app:updated', () => {
  const j = justUpdated
  justUpdated = null
  if (j) updatedMeta = j
  return j ? { version: j.version, from: j.from } : null
})

ipcMain.handle('app:releasenotes', async () => {
  const j = updatedMeta
  if (!j) return { ok: false }
  try {
    const list = await initUpdater().notesSince(j.from, j.version)
    if (list.length) return { ok: true, list }
  } catch (e) {}
  if (j.notes) return { ok: true, list: [{ version: 'v' + j.version, notes: j.notes }] }
  return { ok: false }
})

ipcMain.handle('update:pending', () => (updateOffered ? offerPayload(updateOffered) : null))
ipcMain.handle('update:check', () => runUpdateCheck(true))
ipcMain.handle('app:version', () => ({ version: app.getVersion(), canUpdate: !!updateExePath() }))

ipcMain.handle('update:download', async () => {
  const u = initUpdater()
  try {
    const file = await u.download(p => send('update:progress', p))
    send('update:progress', { done: true })
    // keep the notes of this update so the new version can show them even when it starts offline
    if (updateOffered && updateOffered.notes) {
      settings.pendingNotes = String(updateOffered.notes).slice(0, 1500)
      settings.pendingFor = String(updateOffered.version || '').replace(/^v/i, '')
      try { store.save(settingsFile, settings) } catch (e) {}
    }
    const script = u.applyScript(file, updateExePath())
    spawn('cmd.exe', ['/c', script], { detached: true, stdio: 'ignore', windowsHide: true }).unref()
    setTimeout(() => app.quit(), 600)
    return { ok: true }
  } catch (e) {
    return { ok: false, error: e && e.message ? e.message : 'The update failed.' }
  }
})

ipcMain.handle('win:minimize', e => { const w = BrowserWindow.fromWebContents(e.sender); if (w) w.minimize() })
ipcMain.handle('win:toggle-maximize', e => {
  const w = BrowserWindow.fromWebContents(e.sender)
  if (!w) return false
  if (w.isMaximized()) w.unmaximize(); else w.maximize()
  return w.isMaximized()
})
ipcMain.handle('win:close', e => { const w = BrowserWindow.fromWebContents(e.sender); if (w) w.close() })

const gotLock = app.requestSingleInstanceLock()

if (!gotLock) {
  app.whenReady().then(() => {
    const w = new BrowserWindow({
      width: 460,
      height: 400,
      resizable: false,
      maximizable: false,
      minimizable: false,
      autoHideMenuBar: true,
      backgroundColor: '#0D0C12',
      title: 'Fortnite Unity Converter',
      icon: path.join(__dirname, 'assets', 'icon.ico'),
      webPreferences: { contextIsolation: true, nodeIntegration: false }
    })
    w.loadFile(path.join(__dirname, 'src', 'running.html'))
  })
} else {
  app.on('second-instance', () => {
    if (win && !win.isDestroyed()) {
      if (win.isMinimized()) win.restore()
      win.show()
      win.focus()
    }
  })
  app.whenReady().then(() => {
    migrateOldData()
    ensureFolders()
    const hadSettings = fs.existsSync(settingsFile)
    settings = store.load(settingsFile)
    {
      const cur = app.getVersion()
      const prev = String(settings.lastVersion || '')
      if (wasUpdated({ packaged: app.isPackaged, prev, cur, hadSettings })) {
        justUpdated = { version: cur, from: prev, notes: settings.pendingFor === cur ? String(settings.pendingNotes || '') : '' }
      }
      if (prev !== cur || settings.pendingNotes || settings.pendingFor) {
        settings.lastVersion = cur
        settings.pendingNotes = ''
        settings.pendingFor = ''
        try { store.save(settingsFile, settings) } catch (e) {}
      }
    }
    autoDetectPaths()
    // The app never needs camera, microphone, location or similar access, so refuse any request for it
    session.defaultSession.setPermissionRequestHandler((_wc, _permission, done) => done(false))
    createWindow()
  })
}

app.on('before-quit', () => {
  quitting = true
  saveTypeCache()
  stopTypeServer()
})

app.on('window-all-closed', () => {
  if (current) current.kill()
  app.quit()
})

ipcMain.handle('app:info', () => ({
  root,
  conversions: conversionsDir,
  particles: particlesDir,
  cache: cacheDir,
  exports: exportDirNow(),
  packaged: app.isPackaged
}))

ipcMain.handle('startup:check', () => {
  ensureFolders()
  const { cmd, pre } = engineCommand()
  let engine = false
  if (app.isPackaged) {
    engine = fs.existsSync(cmd)
  } else {
    const r = spawnSync(cmd, ['--version'], { windowsHide: true })
    engine = !r.error && fs.existsSync(pre[0])
  }
  return { engine, helper: fs.existsSync(helperPath()), archive: !!findArchive() }
})

ipcMain.handle('app:relaunch', () => { app.relaunch(); app.exit(0) })

ipcMain.handle('app:reset', () => {
  settings = Object.assign({}, store.defaults, { lastVersion: app.getVersion() })
  store.save(settingsFile, settings)
  app.relaunch()
  app.exit(0)
})

function publicSettings() {
  const flags = {}
  Object.keys(settings.aiKeys || {}).forEach(k => { flags[k] = !!settings.aiKeys[k] })
  if (settings.aiKeyStored && !flags.openai) flags.openai = true
  const out = Object.assign({}, settings, { aiHasKey: !!flags[settings.aiProvider || 'openai'], aiKeyFlags: flags })
  delete out.aiKeyStored
  delete out.aiKeys
  return out
}

const ai = createAi({
  safeStorage,
  getSettings: () => settings,
  saveSettings: patch => {
    settings = Object.assign({}, settings, patch)
    store.save(settingsFile, settings)
  },
  runEngine: args => runEngine(args),
  send: (channel, data) => send(channel, data),
  isStopped: () => stopped
})

ipcMain.handle('settings:get', () => publicSettings())
ipcMain.handle('ai:status', () => ai.status())
ipcMain.handle('ai:setKey', (_e, key, providerId) => {
  ai.setKey(key, providerId)
  return publicSettings()
})
ipcMain.handle('ai:chat', (_e, payload) => ai.chat(payload))

ipcMain.handle('settings:set', (_e, next) => {
  if (!next || typeof next !== 'object' || Array.isArray(next)) return publicSettings()
  next = Object.assign({}, next)
  delete next.aiKeyStored
  delete next.aiKeys
  delete next.aiHasKey
  delete next.aiKeyFlags
  const before = (settings.fortniteArchive || '') + '|' + (settings.bridge || '')
  settings = Object.assign({}, settings, next)
  if (typeServer && before !== (settings.fortniteArchive || '') + '|' + (settings.bridge || '')) {
    stopTypeServer()
    setTimeout(() => ensureApi().catch(() => {}), 2000)
  }
  store.save(settingsFile, settings)
  return publicSettings()
})

ipcMain.handle('files:info', async () => {
  if (!fileIndex && fs.existsSync(pathsFile)) await ensureIndex()
  return {
    helper: fs.existsSync(helperPath()),
    archive: findArchive(),
    count: fileIndex ? fileIndex.length : 0,
    containers: fileContainers.length,
    builtAt: fileBuiltAt,
    building: buildingFiles
  }
})

function vfsFilter(q) {
  const ids = Array.isArray(q.containers) ? q.containers.filter(n => Number.isInteger(n)) : []
  return ids.length && fileContainers.length ? new Set(ids) : null
}

function overlaps(a, set) {
  if (!a) return true
  for (const v of a) if (set.has(v)) return true
  return false
}

ipcMain.handle('files:containers', async () => {
  if (!fileIndex) await ensureIndex()
  return fileContainers.map(c => ({ id: c.id, name: c.name, count: c.count }))
})

ipcMain.handle('files:list', async (_e, q) => {
  if (!fileDirs) await ensureIndex()
  const dir = q.dir || ''
  const node = fileDirs && fileDirs.get(dir)
  if (!node) return { folders: [], files: [], total: 0 }
  const only = vfsFilter(q)
  const info = name => {
    const child = fileDirs.get(dir + '/' + name)
    return { name, folders: child.folders.size, files: child.files.length }
  }
  if (q.foldersOnly) return { folders: Array.from(node.folders).sort(collator.compare).map(info) }
  const match = makeMatcher(q.search, q.regex)
  const type = q.type || 'All'
  let folders = []
  if (type === 'All') {
    folders = Array.from(node.folders)
      .filter(n => (!match || match(dir + '/' + n)) && (!only || overlaps(fileDirs.get(dir + '/' + n).vfs, only)))
      .sort(collator.compare)
      .map(info)
  }
  const files = []
  for (let i = 0; i < node.files.length; i++) {
    const n = node.files[i]
    if (only && node.fileVfs && !only.has(node.fileVfs[i])) continue
    const p = dir + '/' + n
    if (match && !match(p)) continue
    const t = kindOf(p)
    if (type !== 'All' && t !== type) continue
    files.push({ n, t, c: typeCache.get(p) || '' })
  }
  files.sort((x, y) => collator.compare(x.n, y.n))
  return { folders, files: files.slice(0, 2000), total: files.length }
})

ipcMain.handle('files:search', async (_e, q) => {
  if (!fileIndex) await ensureIndex()
  const only = vfsFilter(q)
  const match = fileIndex && (makeMatcher(q.query, q.regex) || (only ? () => true : null))
  if (!match) return { results: [], total: 0 }
  const limit = Math.min(Number(q.limit) || 1000, 2000)
  const results = []
  let total = 0
  for (const f of fileIndex) {
    if (only && !only.has(f.v)) continue
    if (!match(f.p, f.l)) continue
    total++
    if (results.length < limit) {
      const c = typeCache.get(f.p) || ''
      results.push({ p: f.p, t: c ? catOf(c) : f.k, c })
    }
  }
  return { results, total }
})

ipcMain.handle('files:types', async (_e, paths) => {
  const list = Array.isArray(paths) ? paths.filter(p => typeof p === 'string' && p.startsWith('/')).slice(0, 200) : []
  const need = list.filter(p => !typeCache.has(p) && !typeMiss.has(p))
  if (need.length && !typeServerDead) {
    const ok = await startTypeServer()
    if (!ok) typeServerDead = true
    else {
      const work = Promise.all(need.map(p => askType(p).then(c => {
        if (c && c !== '?') {
          typeCache.set(p, c)
          typeCacheDirty = true
        } else typeMiss.add(p)
      })))
      await Promise.race([work, new Promise(r => setTimeout(r, 60000))])
      saveTypeCache()
    }
  }
  return list.map(p => {
    const c = typeCache.get(p) || ''
    return { p, c, t: c ? catOf(c) : typeOf(p) }
  })
})

ipcMain.handle('clipboard:write', (_e, text) => {
  clipboard.writeText(String(text || ''))
  return true
})

ipcMain.handle('files:build', async () => {
  if (buildingFiles) return { ok: false, error: 'busy' }
  const helper = helperPath()
  if (!fs.existsSync(helper)) return { ok: false, error: 'no-helper' }
  const archive = findArchive()
  if (!archive) return { ok: false, error: 'no-archive' }
  buildingFiles = true
  fs.mkdirSync(gameDir, { recursive: true })
  const args = ['--archive', archive, '--out', pathsFile, '--cache', gameDir]
  const result = await new Promise(resolve => {
    let error = ''
    let done = 0
    let buf = ''
    const handle = line => {
      if (line.startsWith('status: ')) send('files:status', line.slice(8))
      else if (line.startsWith('error: ')) error = line.slice(7)
      else if (line.startsWith('done: ')) done = parseInt(line.slice(6), 10) || 0
    }
    let child
    try {
      child = spawn(helper, args, { windowsHide: true })
    } catch (e) {
      resolve({ ok: false, error: 'spawn' })
      return
    }
    child.stdout.on('data', d => {
      buf += d.toString('utf8')
      const lines = buf.split(/\r?\n/)
      buf = lines.pop()
      lines.forEach(handle)
    })
    child.stderr.on('data', () => {})
    child.on('error', () => resolve({ ok: false, error: 'spawn' }))
    child.on('close', code => {
      if (buf) handle(buf)
      resolve(code === 0 && done ? { ok: true, count: done } : { ok: false, error: error || 'failed' })
    })
  })
  buildingFiles = false
  if (result.ok) {
    typeServerDead = false
    typeCache.clear()
    typeMiss.clear()
    typeCacheDirty = false
    try {
      fs.unlinkSync(typesFile)
    } catch (e) {}
    await ensureIndex(true)
  }
  return result
})

let apiStarting = null

function apiHelperReady() {
  return fs.existsSync(helperPath()) && !!findArchive()
}

async function ensureApi() {
  if (await pingBridge()) {
    if (apiState.state === 'off' || apiState.state === 'failed') setApi('external', '')
    return { state: 'running' }
  }
  if (apiStarting) return apiStarting
  apiStarting = (async () => {
    if (settings.autoStartApi !== false && apiHelperReady()) {
      const ready = startTypeServer()
      const outcome = await Promise.race([
        ready.then(ok => (ok ? 'mounted' : 'dead')),
        new Promise(r => setTimeout(() => r('wait'), 20 * 60 * 1000))
      ])
      if (outcome === 'mounted') {
        for (let i = 0; i < 40; i++) {
          if (await pingBridge()) return { state: 'started' }
          await new Promise(r => setTimeout(r, 250))
        }
        return { state: 'timeout' }
      }
      if (outcome === 'wait') return { state: 'timeout' }
    }
    if (settings.autoStartFnp) {
      const r = await ensureFnp()
      if (r.state === 'running' || r.state === 'started') {
        setApi('external', '')
        return r
      }
      return r
    }
    return { state: apiHelperReady() ? 'failed' : 'nohelper' }
  })()
  try {
    return await apiStarting
  } finally {
    apiStarting = null
  }
}

ipcMain.handle('bridge:ping', () => pingBridge())
ipcMain.handle('api:state', () => apiState)
ipcMain.handle('api:start', () => {
  ensureApi().then(async r => {
    if (await pingBridge()) {
      if (apiState.state !== 'ready') setApi(apiState.state === 'starting' ? 'ready' : apiState.state, '')
      return
    }
    const text = {
      nohelper: fs.existsSync(helperPath()) ? 'Fortnite was not found. Set the game archive folder in Settings.' : 'The built-in API is not included in this build.',
      failed: apiState.text || 'The API could not start.',
      timeout: 'Still loading the game files.',
      off: 'The API is turned off in Settings.'
    }[r.state]
    setApi('failed', text || apiState.text || '')
  })
  return true
})
ipcMain.handle('api:ensure', () => ensureApi())

ipcMain.handle('fnp:ensure', () => ensureFnp())

function isNiagara(p) {
  const t = String(p || '').trim()
  const base = t.slice(t.lastIndexOf('/') + 1).split('.')[0]
  if (/^NS_/i.test(base)) return true
  return typeCache.get(t) === 'NiagaraSystem' || typeCache.get(packagePath(t)) === 'NiagaraSystem'
}

ipcMain.handle('engine:convert', async (_e, payload) => {
  if (busy) return { ok: false, error: 'busy' }
  const materials = (payload.materials || []).map(m => m.trim()).filter(Boolean)
  if (!materials.length) return { ok: false, error: 'no materials' }
  busy = true
  stopped = false
  ensureFolders()
  send('engine:state', { running: true, label: 'Converting' })
  if (!(await pingBridge())) {
    send('engine:log', 'The API is not up yet, starting it. The first start reads the game files and can take a minute...\n')
    const started = await ensureApi()
    if (!(await pingBridge())) {
      busy = false
      send('engine:state', { running: false, label: '' })
      const hints = {
        nohelper: "The built-in API isn't installed and the game files couldn't be found. Set the game archive folder under Settings, then Game API, and make sure the app was built with the .NET SDK installed.",
        failed: apiState.text === 'no-mappings' ? "The mappings file (.usmap) couldn't be downloaded, and the game files can't be read without it. Check your internet connection and try again. If it keeps failing, put a .usmap file named custom.usmap in Documents\\Fortnite Unity Converter\\Game Files." : apiState.text ? 'The API could not start: ' + apiState.text : 'The API could not start. Check the game archive folder under Settings, then try again.',
        timeout: 'The API is still loading the game files, or Fortnite looks down. Wait a moment and try again.',
        nopath: "The API couldn't start and the fallback program wasn't found. Set the game archive folder under Settings.",
        off: 'The API is turned off. Turn on Start the API under Settings, then try again.'
      }
      send('app:notice', {
        title: "Can't reach the API",
        body: hints[started.state] || 'The API is not responding. Please try again later.'
      })
      return { ok: false, error: 'no-bridge' }
    }
  }
  let converted = 0
  let attempted = materials.length
  const made = []
  const kinds = []
  const convertMaterial = async (p, name, outDir) => {
    const args = ['--bridge', settings.bridge, '--material', p, '--out', outDir, '--cache', cacheDir]
    if (name) args.push('--name', name)
    const res = await runEngine(args)
    const m = res.output.match(/wrote (.+?)\.shader/)
    if (!m) {
      const kind = classifyFailure(res.output)
      if (kind) kinds.push(kind)
      return null
    }
    const shader = path.join(outDir, m[1], m[1] + '.shader')
    if (!fs.existsSync(shader)) return null
    converted++
    const row = { name: m[1], shader, dir: path.dirname(shader) }
    made.push(row)
    return row
  }
  for (let i = 0; i < materials.length; i++) {
    if (stopped) break
    send('engine:log', '\n[' + (i + 1) + '/' + materials.length + '] ' + materials[i] + '\n')
    if (!isNiagara(materials[i])) {
      await convertMaterial(materials[i], payload.name && materials.length === 1 ? payload.name : '', conversionsDir)
      continue
    }
    const nargs = ['--bridge', settings.bridge, '--niagara', materials[i], '--out', particlesDir]
    if (payload.name && materials.length === 1) nargs.push('--name', payload.name)
    const nres = await runEngine(nargs)
    const nm = nres.output.match(/wrote (.+?)\.particles\.json/)
    if (!nm) {
      const kind = classifyFailure(nres.output)
      if (kind) kinds.push(kind)
      continue
    }
    const pdir = path.join(particlesDir, nm[1])
    const manifest = path.join(pdir, nm[1] + '.particles.json')
    if (!fs.existsSync(manifest)) continue
    converted++
    made.push({ name: nm[1], shader: manifest, dir: pdir, kind: 'particles' })
    const refs = []
    for (const mm of nres.output.matchAll(/^material: (.+)$/gm)) {
      const ref = mm[1].trim()
      if (ref && !refs.includes(ref) && !materials.includes(ref)) refs.push(ref)
    }
    for (const ref of refs) {
      if (stopped) break
      const short = ref.slice(ref.lastIndexOf('/') + 1).split('.')[0]
      if (fs.existsSync(path.join(pdir, short, short + '.shader'))) continue
      attempted++
      send('engine:log', '\nmaterial used by ' + nm[1] + ': ' + ref + '\n')
      await convertMaterial(ref, '', pdir)
    }
    const meshes = []
    for (const mm of nres.output.matchAll(/^mesh: (.+)$/gm)) {
      const ref = mm[1].trim()
      if (ref && !meshes.includes(ref)) meshes.push(ref)
    }
    if (meshes.length && !stopped) {
      if (await startTypeServer()) {
        for (const ref of meshes) {
          if (stopped) break
          send('engine:log', '\nexporting mesh used by ' + nm[1] + ': ' + ref + '\n')
          const r = await askExport('modelexport', packagePath(ref), path.join(pdir, 'Meshes'))
          if (r && r.ok) {
            let made = r.file
            if (/\.glb$/i.test(made)) {
              try {
                const fbx = made.replace(/\.glb$/i, '.fbx')
                glbToFbx(made, fbx)
                fs.rmSync(made, { force: true })
                made = fbx
              } catch (err) {
                send('engine:log', 'fbx conversion failed, keeping the glb: ' + (err && err.message ? err.message : err) + '\n')
              }
            }
            send('engine:log', 'mesh exported: ' + made + '\n')
          } else {
            send('engine:log', 'mesh not exported: ' + ((r && r.error) || 'unknown error') + '\n')
          }
        }
      } else {
        send('engine:log', '\nmeshes were not exported because the file helper is not running\n')
      }
    }
    fs.rmSync(path.join(pdir, 'Editor'), { recursive: true, force: true })
    fs.rmSync(path.join(pdir, 'PorterObjectBounds.cs'), { force: true })
  }
  busy = false
  send('engine:state', { running: false, label: '' })
  send('engine:log', '\nfinished: ' + converted + ' of ' + attempted + ' converted\n')
  if (made.length) send('convert:done', { shaders: made, total: attempted })
  if (!made.length && !stopped) {
    const notice = noticeFor(kinds)
    if (notice) send('app:notice', notice)
  }
  return { ok: true, converted, total: attempted }
})

ipcMain.handle('engine:check', async (_e, payload) => {
  if (busy) return { ok: false, error: 'busy' }
  const paths = payload.paths || []
  if (!paths.length) return { ok: false, error: 'no files' }
  busy = true
  stopped = false
  send('engine:state', { running: true, label: 'Checking' })
  const args = ['--check-shader'].concat(paths)
  if (!payload.fix) args.push('--no-fix')
  if (!payload.useUnity) args.push('--no-unity')
  const checked = await runEngine(args)
  if (payload.ai && !stopped) {
    send('engine:state', { running: true, label: 'AI fixing' })
    try {
      await ai.fixShaders({ paths, output: checked.output, useUnity: !!payload.useUnity })
    } catch (e) {
      send('engine:log', 'AI fix failed: ' + e.message + '\n')
    }
  }
  busy = false
  send('engine:state', { running: false, label: '' })
  send('engine:log', '\ncheck finished\n')
  return { ok: true }
})

ipcMain.handle('engine:stop', () => {
  stopped = true
  if (current) current.kill()
  return true
})

ipcMain.handle('shaders:list', () => listShaders())


/* ---------- previews ---------- */
const previewDir = path.join(cacheDir, 'Preview')
const previewJobs = new Map()
const previewWins = new Set()
const PREVIEW_MAX = 8

function previewClass(c, p) {
  if (c) {
    if (/Texture/.test(c)) return 'texture'
    if (c === 'StaticMesh' || c === 'SkeletalMesh') return 'model'
    if (c === 'SoundWave') return 'sound'
    if (c === 'Material' || /^MaterialFunction/.test(c)) return 'material'
    return 'json'
  }
  const t = typeOf(p)
  if (t === 'Texture') return 'texture'
  if (t === 'Mesh') return 'model'
  if (t === 'Sound') return 'sound'
  if (t === 'Material' || t === 'MaterialFunction') return 'material'
  return 'json'
}

async function previewKindFor(p) {
  let c = typeCache.get(p) || ''
  if (!c && !typeServerDead) {
    try {
      const got = await askType(p)
      if (got && got !== '?') {
        c = got
        typeCache.set(p, c)
        typeCacheDirty = true
      }
    } catch (e) {}
  }
  return previewClass(c, p)
}

function walkFiles(dir, depth, out) {
  let list = []
  try { list = fs.readdirSync(dir, { withFileTypes: true }) } catch (e) { return out }
  for (const d of list) {
    const full = path.join(dir, d.name)
    if (d.isDirectory()) { if (depth < 4) walkFiles(full, depth + 1, out) } else out.push(full)
  }
  return out
}

async function walkFilesAsync(dir, depth, out) {
  let list = []
  try { list = await fs.promises.readdir(dir, { withFileTypes: true }) } catch (e) { return out }
  await Promise.all(list.map(async d => {
    const full = path.join(dir, d.name)
    if (d.isDirectory()) { if (depth < 4) await walkFilesAsync(full, depth + 1, out) } else out.push(full)
  }))
  return out
}

function pickPreviewFile(dir, kind) {
  const files = walkFiles(dir, 0, [])
  const order = {
    texture: ['.png'],
    thumb: ['.png'],
    banner: ['.png'],
    meshthumb: ['.mesh'],
    model: ['.glb', '.gltf'],
    json: ['.json'],
    material: ['.json'],
    sound: ['.ogg', '.wav', '.mp3', '.opus', '.flac']
  }[kind] || []
  for (const ext of order) {
    const hit = files.find(f => f.toLowerCase().endsWith(ext))
    if (hit) return hit
  }
  if (kind === 'sound' && files.length) return files[0]
  return ''
}

const pendingExports = new Map()
let interactiveExports = 0
const isThumbKind = k => k === 'thumb' || k === 'meshthumb' || k === 'banner'

async function previewExport(kind, p) {
  if (isThumbKind(kind)) {
    // icons wait while a viewer or an export is loading, so they never delay it
    while (interactiveExports > 0) await new Promise(r => setTimeout(r, 120))
    return previewExportInner(kind, p)
  }
  interactiveExports++
  try { return await previewExportInner(kind, p) } finally { interactiveExports-- }
}

async function previewExportInner(kind, p) {
  const stem = p.slice(p.lastIndexOf('/') + 1).split('.')[0].replace(/[^A-Za-z0-9_\-]/g, '_').slice(0, 60)
  const id = crypto.createHash('sha1').update(kind + (kind === 'banner' ? '#2048' : '') + '\t' + p).digest('hex').slice(0, 10)
  const dir = path.join(previewDir, kind, id + '_' + stem)
  if (settings.previewCache !== false) {
    const hit = fs.existsSync(dir) ? pickPreviewFile(dir, kind) : ''
    if (hit) return { ok: true, kind, file: hit, cached: true }
  }
  // The same export may already be running (for example the Files tab asked again after you left a
  // folder and came back). Share that run: a second one would delete its folder and replace its
  // waiter, so the first request would never finish.
  const running = pendingExports.get(dir)
  if (running) return running
  const job = (async () => {
    try { fs.rmSync(dir, { recursive: true, force: true }) } catch (e) {}
    fs.mkdirSync(dir, { recursive: true })
    const r = await askExport(kind === 'material' ? 'matgraph' : kind, p, dir)
    if (!r.ok) return { ok: false, kind, error: r.error || 'export failed' }
    let file = pickPreviewFile(dir, kind)
    if (!file && r.file && fs.existsSync(r.file)) file = r.file
    if (!file) return { ok: false, kind, error: 'the export finished but produced no ' + kind + ' file' }
    return { ok: true, kind, file }
  })()
  pendingExports.set(dir, job)
  try { return await job } finally { pendingExports.delete(dir) }
}

async function runPreviewJob(p, force) {
  const up = await startTypeServer()
  if (!up) return { ok: false, error: 'The built-in API is not running, so the game files cannot be read. Check the game archive folder under App Settings, then Installation.' }
  const kind = force || await previewKindFor(p)
  const first = await previewExport(kind, p)
  if (first.ok || kind === 'json') return first
  const fallback = await previewExport('json', p)
  if (fallback.ok) return Object.assign(fallback, { note: 'This asset could not be shown as a ' + kind + ' (' + first.error + '), so its properties are shown instead.' })
  return first
}

function openPreviewWindow(p, force) {
  nativeTheme.themeSource = 'dark'
  const name = p.slice(p.lastIndexOf('/') + 1).split('.')[0]
  const w = new BrowserWindow({
    width: 1100,
    height: 760,
    minWidth: 560,
    minHeight: 380,
    backgroundColor: '#0D0C12',
    autoHideMenuBar: true,
    title: 'Preview - ' + name,
    icon: path.join(__dirname, 'assets', 'icon.ico'),
    webPreferences: {
      preload: path.join(__dirname, 'preview-preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  })
  w.setMenu(null)
  const id = w.webContents.id
  previewWins.add(w)
  previewJobs.set(id, { name, path: p, promise: runPreviewJob(p, force).catch(e => ({ ok: false, error: String(e && e.message || e) })) })
  w.on('closed', () => { previewWins.delete(w); previewJobs.delete(id) })
  w.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  w.webContents.on('will-navigate', e => e.preventDefault())
  w.loadFile(path.join(__dirname, 'src', 'preview.html'))
}

const multi = { texture: null, sound: null, json: null, model: null, material: null }
let multiSeq = 0
const MULTI_SPEC = {
  texture: { page: 'texture-viewer.html', title: 'Texture Viewer', size: { width: 1280, height: 720, minWidth: 560, minHeight: 380 } },
  sound: { page: 'sound-viewer.html', title: 'Sound Viewer', size: { width: 540, height: 96, minWidth: 360, minHeight: 96, maxHeight: 96, useContentSize: true } },
  model: { page: 'model-viewer.html', title: 'Model Viewer', size: { width: 1280, height: 720, minWidth: 700, minHeight: 420 } },
  json: { page: 'properties-viewer.html', title: 'Properties Viewer', size: { width: 1280, height: 720, minWidth: 560, minHeight: 380 } },
  material: { page: 'material-viewer.html', title: 'Material Viewer', size: { width: 1280, height: 720, minWidth: 700, minHeight: 420 } }
}

function linkCandidates(stem) {
  const base = packagePath(stem)
  const out = [base]
  const m = /^\/([^\/]+)\/(.+)$/.exec(base)
  if (m && !['Game', 'Engine', 'FortniteGame', 'Script'].includes(m[1])) {
    out.push('/FortniteGame/Plugins/GameFeatures/' + m[1] + '/Content/' + m[2])
    out.push('/FortniteGame/Plugins/' + m[1] + '/Content/' + m[2])
    out.push('/Engine/Plugins/' + m[1] + '/Content/' + m[2])
  }
  return out
}

function openMulti(kind, p, opts) {
  opts = opts || {}
  const name = (opts.name || p.slice(p.lastIndexOf('/') + 1)).split('.')[0]
  const id = 'j' + (++multiSeq)
  const index = opts.index != null ? opts.index : -1
  const run = opts.cands
    ? (async () => {
        let last = { ok: false, error: 'that file is not in the game archives' }
        for (const c of opts.cands) {
          const r = await runPreviewJob(c, kind)
          if (r && r.ok) return r
          last = r || last
        }
        return last
      })()
    : runPreviewJob(p, kind)
  const job = { id, name, path: p, index, promise: run.catch(e => ({ ok: false, error: String(e && e.message || e) })) }
  let m = multi[kind]
  if (m && m.win && !m.win.isDestroyed()) {
    if (opts.reset) {
      m.jobs.clear()
      m.order = []
      if (m.ready) m.win.webContents.send('pvm:reset')
    }
    m.jobs.set(id, job)
    m.order.push(id)
    if (m.ready) m.win.webContents.send('pvm:add', { id, name, path: p, index })
    if (m.win.isMinimized()) m.win.restore()
    m.win.show()
    m.win.focus()
    return
  }
  const spec = MULTI_SPEC[kind]
  nativeTheme.themeSource = 'dark'
  const w = new BrowserWindow(Object.assign({
    frame: false,
    show: false,
    backgroundColor: '#0D0C12',
    autoHideMenuBar: true,
    title: spec.title,
    icon: path.join(__dirname, 'assets', 'icon.ico'),
    webPreferences: {
      preload: path.join(__dirname, 'preview-preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  }, spec.size))
  w.setMenu(null)
  m = { win: w, jobs: new Map([[id, job]]), order: [id], ready: false }
  multi[kind] = m
  previewWins.add(w)
  w.once('ready-to-show', () => w.show())
  w.on('maximize', () => { if (!w.isDestroyed()) w.webContents.send('win:state', true) })
  w.on('unmaximize', () => { if (!w.isDestroyed()) w.webContents.send('win:state', false) })
  w.on('closed', () => { previewWins.delete(w); if (multi[kind] === m) multi[kind] = null })
  w.webContents.setWindowOpenHandler(() => ({ action: 'deny' }))
  w.webContents.on('will-navigate', e => e.preventDefault())
  w.loadFile(path.join(__dirname, 'src', spec.page))
}

function multiFor(e) {
  return Object.values(multi).find(m => m && m.win && !m.win.isDestroyed() && m.win.webContents.id === e.sender.id) || null
}

ipcMain.handle('pvm:init', e => {
  const m = multiFor(e)
  if (!m) return []
  m.ready = true
  return m.order.map(id => { const j = m.jobs.get(id); return { id, name: j.name, path: j.path, index: j.index } })
})

ipcMain.handle('pvm:open-props', (_e, raw) => {
  const t = String(raw || '').trim()
  if (!t) return false
  const cut = t.lastIndexOf('.')
  const tail = cut >= 0 ? t.slice(cut + 1) : ''
  const stem = cut >= 0 ? t.slice(0, cut) : t
  const index = /^\d+$/.test(tail) ? parseInt(tail, 10) : -1
  const cands = linkCandidates(stem)
  openMulti('json', cands[0], { name: stem.slice(stem.lastIndexOf('/') + 1), index, cands })
  return true
})

// A material function call node opens the function's own graph in a new tab of the material viewer
ipcMain.handle('pvm:open-material', (e, raw) => {
  if (!multiFor(e)) return false
  let t = String(raw || '').trim().replace(/\\/g, '/')
  if (!t || t.length > 600) return false
  const cut = t.lastIndexOf('.')
  if (cut > t.lastIndexOf('/')) t = t.slice(0, cut)
  if (!t.startsWith('/')) t = '/' + t
  const cands = linkCandidates(t)
  openMulti('material', cands[0], { name: t.slice(t.lastIndexOf('/') + 1), cands })
  return true
})

const FP_DIFFUSE = [
  ['Diffuse', 'D', 'Base Color', 'BaseColor', 'Concrete', 'Trunk_BaseColor', 'Diffuse Top', 'BaseColor_Trunk', 'CliffTexture', 'PM_Diffuse', '___Diffuse', 'Background Diffuse', 'BG Diffuse Texture'],
  ['Diffuse_Texture_2'], ['Diffuse_Texture_3'], ['Diffuse_Texture_4']
]
const FP_NORMAL = [
  ['Normals', 'N', 'Normal', 'NormalMap', 'ConcreteTextureNormal', 'Trunk_Normal', 'Normals Top', 'Normal_Trunk', 'CliffNormal', 'PM_Normals', '_Normal'],
  ['Normals_Texture_2'], ['Normals_Texture_3'], ['Normals_Texture_4']
]
const FP_SPECULAR = [
  ['SpecularMasks', 'S', 'SRM', 'S Mask', 'Specular Mask', 'SpecularMask', 'Concrete_SpecMask', 'Trunk_Specular', 'Specular Top', 'SMR_Trunk', 'Cliff Spec Texture', 'PM_SpecularMasks', '__PBR Masks'],
  ['SpecularMasks_2'], ['SpecularMasks_3'], ['SpecularMasks_4']
]
const FP_LAYER_SWITCHES = ['use 2 layers', 'use 3 layers', 'use 4 layers', 'use 5 layers', 'use 6 layers', 'use 7 layers', 'use 2 materials', 'use 3 materials', 'use 4 materials', 'use 5 materials', 'use 6 materials', 'use 7 materials', 'use_multiple_material_textures']

function objectPathOf(ref) {
  if (!ref) return ''
  let t = typeof ref === 'string' ? ref : (ref.ObjectPath || '')
  if (!t) return ''
  t = String(t).replace(/\.\d+$/, '').replace(/\\/g, '/')
  if (!t.startsWith('/')) t = '/' + t
  return packagePath(t)
}

function paramName(e) {
  return (e && e.ParameterInfo && e.ParameterInfo.Name) || (e && e.ParameterName) || ''
}

function sliceArray(text, key) {
  const k = text.indexOf('"' + key + '"')
  if (k < 0) return null
  const open = text.indexOf('[', k)
  if (open < 0) return null
  let depth = 0, inStr = false
  for (let i = open; i < text.length; i++) {
    const c = text[i]
    if (inStr) {
      if (c === '\\') i++
      else if (c === '"') inStr = false
      continue
    }
    if (c === '"') inStr = true
    else if (c === '[') depth++
    else if (c === ']') { depth--; if (depth === 0) { try { return JSON.parse(text.slice(open, i + 1)) } catch (e) { return null } } }
  }
  return null
}

async function jsonOf(pkgPath) {
  const r = await previewExport('json', pkgPath)
  if (!r.ok) return null
  try { return fs.readFileSync(r.file, 'utf8') } catch (e) { return null }
}

function mainExport(list, pkgPath) {
  const stem = pkgPath.slice(pkgPath.lastIndexOf('/') + 1).split('.')[0]
  const arr = Array.isArray(list) ? list : []
  return arr.find(x => x && x.Name === stem && x.Properties) || arr.find(x => x && /^Material/.test(x.Type || '') && x.Properties) || arr.find(x => x && x.Properties) || null
}

async function meshMaterialList(meshPath) {
  const text = await jsonOf(meshPath)
  if (!text) return []
  for (const key of ['StaticMaterials', 'SkeletalMaterials', 'Materials']) {
    const arr = sliceArray(text, key)
    if (arr && arr.length) {
      return arr.map(e => {
        const ref = e && (e.MaterialInterface || e.Material || e)
        return { slot: (e && (e.MaterialSlotName || e.ImportedMaterialSlotName)) || '', path: objectPathOf(ref) }
      })
    }
  }
  return []
}

async function describeMaterial(matPath) {
  const out = { textures: {}, useLayers: false, maxLayer: 0 }
  let cur = matPath
  const seen = new Set()
  for (let depth = 0; depth < 16 && cur && !seen.has(cur); depth++) {
    seen.add(cur)
    const text = await jsonOf(cur)
    if (!text) break
    let list
    try { list = JSON.parse(text) } catch (e) { break }
    const ex = mainExport(list, cur)
    if (!ex) break
    const props = ex.Properties || {}
    if (ex.Type === 'Material') {
      const ced = props.CachedExpressionData
      if (ced) {
        try {
          const infoSet = t => {
            const entry = t === 0 ? ced.RuntimeEntries : ced['RuntimeEntries[' + t + ']']
            return (entry && entry.ParameterInfoSet) || []
          }
          const vals = ced.TextureValues || []
          const n = infoSet(3)
          for (let i = 0; i < n.length && i < vals.length; i++) {
            const nm = n[i] && n[i].Name
            const tp = objectPathOf(vals[i])
            if (nm && tp && !(nm in out.textures)) out.textures[nm] = tp
          }
        } catch (e) {}
      }
      break
    }
    for (const e of props.TextureParameterValues || []) {
      const nm = paramName(e)
      const tp = objectPathOf(e.ParameterValue)
      if (nm && tp && !(nm in out.textures)) out.textures[nm] = tp
    }
    let level = null
    for (const sp of [props.StaticParametersRuntime, props.StaticParameters]) {
      if (!sp) continue
      const sw = sp.StaticSwitchParameters || []
      const on = n => sw.some(x => paramName(x).toLowerCase() === n && x.Value)
      if (sw.some(x => FP_LAYER_SWITCHES.includes(paramName(x).toLowerCase()) && x.Value)) out.useLayers = true
      let ml = 4
      if (on('use 2 materials') || on('use 2 layers')) ml = 2
      if (on('use 3 materials') || on('use 3 layers')) ml = 3
      if (on('use 4 materials') || on('use 4 layers')) ml = 4
      level = ml
    }
    if (level !== null) out.maxLayer = level
    cur = objectPathOf(props.Parent)
  }
  return out
}

function mapTextures(textures, table) {
  const slots = [null, null, null, null]
  for (const [name, tp] of Object.entries(textures)) {
    for (let l = 0; l < 4; l++) {
      if (!slots[l] && table[l].includes(name)) { slots[l] = tp; break }
    }
  }
  return slots
}

const matTexCache = new Map()
async function textureFile(tp) {
  if (!tp) return null
  if (matTexCache.has(tp)) return matTexCache.get(tp)
  const pr = previewExport('texture', tp).then(r => (r.ok ? r.file : null)).catch(() => null)
  matTexCache.set(tp, pr)
  return pr
}

async function resolveMeshMaterials(meshPath) {
  const slots = await meshMaterialList(meshPath)
  const out = []
  for (const s of slots) {
    const entry = { slot: s.slot, name: s.path ? s.path.slice(s.path.lastIndexOf('/') + 1).split('.')[0] : '', diffuse: [null, null, null, null], normal: [null, null, null, null], specular: [null, null, null, null], useLayers: false, maxLayer: 0 }
    if (s.path) {
      try {
        const d = await describeMaterial(s.path)
        entry.useLayers = d.useLayers
        entry.maxLayer = d.maxLayer
        const maps = { diffuse: mapTextures(d.textures, FP_DIFFUSE), normal: mapTextures(d.textures, FP_NORMAL), specular: mapTextures(d.textures, FP_SPECULAR) }
        for (const k of Object.keys(maps)) {
          for (let l = 0; l < 4; l++) entry[k][l] = await textureFile(maps[k][l])
        }
      } catch (e) {}
    }
    out.push(entry)
  }
  return out
}

ipcMain.handle('pvm:materials', async (e, id) => {
  const m = multiFor(e)
  const j = m && m.jobs.get(id)
  if (!j) return []
  try { return await resolveMeshMaterials(packagePath(j.path)) } catch (err) { return [] }
})

ipcMain.handle('pvm:job', async (e, id) => {
  const m = multiFor(e)
  const j = m && m.jobs.get(id)
  if (!j) return { ok: false, error: 'This preview has no job.' }
  const r = await j.promise
  const out = Object.assign({ name: j.name, path: j.path, cls: typeCache.get(j.path) || '' }, r)
  if (r && r.note) out.note = String(r.note).replace(/, so its properties are shown instead\.$/, '.')
  return out
})

function previewAllowed(file) {
  if (typeof file !== 'string' || !file) return ''
  const abs = path.resolve(file)
  const base = path.resolve(previewDir) + path.sep
  const ok = process.platform === 'win32' ? abs.toLowerCase().startsWith(base.toLowerCase()) : abs.startsWith(base)
  return ok ? abs : ''
}

const thumbFailed = new Map()
const thumbFails = new Map()
function thumbFail(pp, error) {
  // a timeout or a stopped helper says nothing about the asset: try it again later
  if (error && /timed out|helper|cancel/i.test(error)) return
  const n = (thumbFails.get(pp) || 0) + 1
  thumbFails.set(pp, n)
  if (n >= 4) { thumbFailed.set(pp, Date.now() + 60000); thumbFails.delete(pp) }
}
function thumbBlocked(pp) {
  const t = thumbFailed.get(pp)
  if (!t) return false
  if (Date.now() < t) return true
  thumbFailed.delete(pp)
  return false
}
ipcMain.handle('files:thumb', async (_e, p) => {
  const pp = packagePath(p)
  if (!pp || thumbBlocked(pp)) return null
  try {
    const up = await startTypeServer()
    if (!up) return null
    const cls = typeCache.get(p) || typeCache.get(pp) || ''
    if (cls === 'StaticMesh' || cls === 'SkeletalMesh') {
      const m = await previewExport('meshthumb', pp)
      if (!m.ok) { thumbFail(pp, m.error); return null }
      const rgba = meshThumb.render(await fs.promises.readFile(m.file))
      if (!rgba) { thumbFail(pp); return null }
      const bgra = Buffer.alloc(rgba.length)
      for (let i = 0; i < rgba.length; i += 4) { bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3] }
      return nativeImage.createFromBitmap(bgra, { width: meshThumb.SIZE, height: meshThumb.SIZE }).toDataURL()
    }
    const r = await previewExport('thumb', pp)
    if (!r.ok) { thumbFail(pp, r.error); return null }
    return 'data:image/png;base64,' + (await fs.promises.readFile(r.file)).toString('base64')
  } catch (e) {
    thumbFail(pp)
    return null
  }
})

// Home cards: the app no longer fetches pictures from another service, so the cards keep their plain look
ipcMain.handle('news:images', async () => [])

let screenNotes = 0
function screenNote(msg) {
  if (screenNotes >= 40) return
  screenNotes++
  send('engine:log', '[loading screens] ' + msg + '\n')
}

async function gameScreens() {
  if (!fileIndex && fs.existsSync(pathsFile)) await ensureIndex()
  if (!fileIndex) { screenNote('the game file list is not built yet; open the Files tab once'); return [] }
  const items = []
  const loose = []
  for (const f of fileIndex) {
    if (!/\.uasset$/i.test(f.p)) continue
    const name = f.p.slice(f.p.lastIndexOf('/') + 1)
    if (/^LSID_/i.test(name)) { items.push(f.p); continue }
    if (!/\/loading[_ ]?screens?\//i.test(f.p)) continue
    if (!/^T_/i.test(name) || /icon|thumb|small|mask|logo|_n\.|normal|button|tip|hud|tutorial|map|poi|lobby/i.test(name)) continue
    loose.push(f.p)
  }
  screenNote(items.length + ' loading screen items and ' + loose.length + ' loose pictures found')
  const mix = a => {
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [a[i], a[j]] = [a[j], a[i]]
    }
    return a
  }
  return mix(items).slice(0, 500).concat(mix(loose).slice(0, 200))
}

const lsidTextures = new Map()

async function quietExport(kind, p) {
  while (interactiveExports > 0) await new Promise(r => setTimeout(r, 120))
  return previewExportInner(kind, p)
}

function texturePathFromRef(v) {
  let s = v
  if (v && typeof v === 'object') {
    const n = v.AssetPathName
    s = v.ObjectPath || (n && typeof n === 'object' ? n.Text || n.Name || '' : n) || v.ObjectName || ''
  }
  s = String(s || '').replace(/\\/g, '/').trim()
  const q = s.match(/'([^']+)'/)
  if (q && !s.includes('/')) s = q[1]
  if (!s || s === 'None') return ''
  const dot = s.lastIndexOf('.')
  if (dot > s.lastIndexOf('/')) s = s.slice(0, dot)
  if (!s.startsWith('/')) s = '/' + s
  if (s.startsWith('/Game/')) return '/FortniteGame/Content/' + s.slice(6)
  if (/^\/(FortniteGame|Engine)\//.test(s)) return s
  const m = s.match(/^\/([^/]+)\/(.+)$/)
  return m ? '/FortniteGame/Plugins/GameFeatures/' + m[1] + '/Content/' + m[2] : ''
}

function findTextureRef(props) {
  if (!props || typeof props !== 'object') return ''
  const direct = props.BackgroundImage
  if (direct) { const t = texturePathFromRef(direct); if (t) return t }
  let best = ''
  for (const k of Object.keys(props)) {
    if (!/background|loading|image|texture/i.test(k) || /preview|icon|small|thumb|mask/i.test(k)) continue
    const v = props[k]
    if (!v || typeof v !== 'object') continue
    const t = texturePathFromRef(v)
    if (t && /\/T_[^/]+$/i.test(t)) { best = t; break }
  }
  return best
}

async function loadingScreenTexture(p) {
  if (lsidTextures.has(p)) return lsidTextures.get(p)
  let tex = ''
  let why = ''
  try {
    const r = await quietExport('json', p)
    if (!r.ok) why = 'could not read it (' + r.error + ')'
    else {
      const data = JSON.parse(await fs.promises.readFile(r.file, 'utf8'))
      for (const e of Array.isArray(data) ? data : [data]) {
        tex = findTextureRef(e && e.Properties)
        if (tex) break
      }
      if (!tex) why = 'no picture reference found; its properties are: ' + Object.keys(((Array.isArray(data) ? data : [data]).find(e => e && e.Properties) || { Properties: {} }).Properties).slice(0, 12).join(', ')
    }
  } catch (e) { why = String(e && e.message || e) }
  if (!tex) screenNote(p.slice(p.lastIndexOf('/') + 1) + ': ' + why)
  lsidTextures.set(p, tex)
  return tex
}

// Real loading screens from a public Fortnite cosmetics API. Each item has a background picture and two smaller ones;
// the page checks the size and shape of every picture it downloads and skips the ones that are not wide loading screens.
const SCREEN_API = 'https://fortnite-api.com/v2/cosmetics/br/search/all?type=loadingscreen'
function mixed(a) {
  a = a.slice()
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]]
  }
  return a
}
async function apiScreens() {
  const dir = path.join(root, 'cache', 'screens')
  const file = path.join(dir, 'loadingscreens-api.json')
  let saved = null
  let fresh = false
  try {
    saved = JSON.parse(await fs.promises.readFile(file, 'utf8'))
    fresh = Date.now() - (await fs.promises.stat(file)).mtimeMs < 24 * 3600 * 1000
  } catch (e) {}
  const usable = o => o && Array.isArray(o.bg) && Array.isArray(o.ft) && Array.isArray(o.ic) && o.bg.length + o.ft.length + o.ic.length > 0
  let lists = usable(saved) && fresh ? saved : null
  if (!lists) {
    try {
      const r = await fetch(SCREEN_API, { signal: AbortSignal.timeout(30000), headers: { 'User-Agent': 'FortniteUnityConverter' } })
      if (!r.ok) throw new Error('HTTP ' + r.status)
      const j = await r.json()
      const o = { bg: [], ft: [], ic: [] }
      const web = u => typeof u === 'string' && /^https:\/\//.test(u)
      for (const it of (j && j.data) || []) {
        const im = (it && it.images) || {}
        if (web(im.other && im.other.background)) o.bg.push(im.other.background)
        if (web(im.featured)) o.ft.push(im.featured)
        if (web(im.icon)) o.ic.push(im.icon)
      }
      if (!usable(o)) throw new Error('the answer had no pictures')
      await fs.promises.mkdir(dir, { recursive: true })
      await fs.promises.writeFile(file, JSON.stringify(o))
      lists = o
      screenNote('the picture list came from the API: ' + o.bg.length + ' loading screens')
    } catch (e) {
      lists = usable(saved) ? saved : null
      screenNote('could not get the picture list from the API (' + (e && e.message || e) + ')' + (lists ? '; using the saved list' : ''))
    }
  }
  if (!lists) return []
  return mixed(lists.bg).concat(mixed(lists.ft), mixed(lists.ic))
}

ipcMain.handle('home:screens', async () => {
  const web = await apiScreens().catch(() => [])
  if (web.length) return web.concat(fileIndex ? await gameScreens().catch(() => []) : [])
  return gameScreens()
})
// Only public web addresses: never this computer or anything on the local network
function publicHttps(u) {
  try {
    const x = new URL(u)
    if (x.protocol !== 'https:' || x.username || x.password) return false
    const h = x.hostname.toLowerCase().replace(/^\[|\]$/g, '')
    if (!h || h === 'localhost' || h.endsWith('.localhost') || h.endsWith('.local') || h.endsWith('.internal')) return false
    const ip = net.isIP(h)
    if (ip === 6) return false
    if (ip === 4) {
      const [a, b] = h.split('.').map(Number)
      if (a === 0 || a === 10 || a === 127 || (a === 169 && b === 254) || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 100 && b >= 64 && b <= 127)) return false
    }
    return true
  } catch (e) { return false }
}
ipcMain.handle('home:fetchimg', async (_e, u) => {
  if (typeof u !== 'string' || u.length > 2000 || !publicHttps(u)) return null
  try {
    const dir = path.join(root, 'cache', 'screens')
    const key = require('crypto').createHash('md5').update(u).digest('hex')
    const file = path.join(dir, key + '.img')
    const meta = file + '.type'
    try {
      const [mt, data] = await Promise.all([fs.promises.readFile(meta, 'utf8'), fs.promises.readFile(file)])
      return 'data:' + mt + ';base64,' + data.toString('base64')
    } catch (e) {}
    const r = await fetch(u, { signal: AbortSignal.timeout(30000) })
    if (!r.ok) return null
    const type = (r.headers.get('content-type') || 'image/png').split(';')[0]
    if (!/^image\//.test(type)) return null
    const buf = Buffer.from(await r.arrayBuffer())
    if (!buf.length || buf.length > 25 * 1024 * 1024) return null
    await fs.promises.mkdir(dir, { recursive: true })
    await Promise.all([fs.promises.writeFile(file, buf), fs.promises.writeFile(meta, type)])
    return 'data:' + type + ';base64,' + buf.toString('base64')
  } catch (e) { return null }
})
ipcMain.handle('home:screenimg', async (_e, p) => {
  let pp = packagePath(p)
  if (!pp) return null
  try {
    if (!(await startTypeServer())) return null
    if (/\/LSID_[^/]+$/i.test(pp)) {
      pp = await loadingScreenTexture(pp)
      if (!pp) return null
    }
    const r = await previewExport('banner', pp)
    if (!r.ok) { screenNote(pp.slice(pp.lastIndexOf('/') + 1) + ': picture export failed (' + r.error + ')'); return null }
    return 'data:image/png;base64,' + (await fs.promises.readFile(r.file)).toString('base64')
  } catch (e) { return null }
})

ipcMain.handle('preview:open', async (_e, payload) => {
  const paths = Array.from(new Set(((payload && payload.paths) || []).map(packagePath).filter(Boolean)))
  if (!paths.length) return { ok: false, error: 'no paths' }
  const force = payload && ['json', 'texture', 'model', 'sound', 'material'].includes(payload.kind) ? payload.kind : ''
  let firstModel = true
  for (const p of paths.slice(0, PREVIEW_MAX)) {
    const kind = force || await previewKindFor(p)
    if (kind === 'model') { openMulti(kind, p, { reset: firstModel }); firstModel = false }
    else if (kind === 'texture' || kind === 'sound' || kind === 'json' || kind === 'material') openMulti(kind, p)
    else openPreviewWindow(p, kind)
  }
  return { ok: true, opened: Math.min(paths.length, PREVIEW_MAX), total: paths.length }
})

async function baseMaterialOf(p) {
  let cur = p
  const seen = new Set()
  for (let depth = 0; depth < 16 && cur && !seen.has(cur); depth++) {
    seen.add(cur)
    const text = await jsonOf(cur)
    if (!text) return ''
    let list
    try { list = JSON.parse(text) } catch (e) { return '' }
    const ex = mainExport(list, cur)
    if (!ex) return ''
    if (ex.Type === 'Material') return cur
    cur = objectPathOf((ex.Properties || {}).Parent)
  }
  return ''
}

ipcMain.handle('preview:material-tree', async (_e, raw) => {
  const p = packagePath(String(raw || ''))
  if (!p) return { ok: false, error: 'No file selected' }
  const up = await startTypeServer()
  if (!up) return { ok: false, error: 'The built-in API is not running, so the game files cannot be read' }
  const base = await baseMaterialOf(p)
  if (!base) return { ok: false, error: 'Could not find the parent material of this instance' }
  openMulti('material', base, { name: base.slice(base.lastIndexOf('/') + 1).split('.')[0] })
  return { ok: true, base }
})

// The material viewer's texture nodes ask for a small picture of their texture
ipcMain.handle('pvm:thumb', async (e, p) => {
  if (!multiFor(e)) return null
  const pp = packagePath(String(p || '').replace(/\.\d+$/, ''))
  if (!pp || thumbBlocked(pp)) return null
  try {
    const r = await previewExport('thumb', pp)
    if (!r.ok) { thumbFail(pp, r.error); return null }
    return 'data:image/png;base64,' + (await fs.promises.readFile(r.file)).toString('base64')
  } catch (err) { thumbFail(pp); return null }
})

ipcMain.handle('pv:job', async e => {
  const j = previewJobs.get(e.sender.id)
  if (!j) return { ok: false, error: 'This preview has no job.' }
  const r = await j.promise
  return Object.assign({ name: j.name, path: j.path }, r)
})

ipcMain.handle('pv:read', async (_e, file) => {
  const abs = previewAllowed(file)
  if (!abs) return null
  return fs.promises.readFile(abs).catch(() => null)
})

ipcMain.handle('pv:save', async (e, file) => {
  const abs = previewAllowed(file)
  if (!abs) return { ok: false }
  const owner = BrowserWindow.fromWebContents(e.sender)
  const r = await dialog.showSaveDialog(owner, { defaultPath: path.join(exportDirNow(), path.basename(abs)) })
  if (r.canceled || !r.filePath) return { ok: false }
  try { fs.copyFileSync(abs, r.filePath); return { ok: true, file: r.filePath } } catch (err) { return { ok: false } }
})

ipcMain.handle('pv:reveal', (_e, file) => {
  const abs = previewAllowed(file)
  if (abs) shell.showItemInFolder(abs)
  return !!abs
})

async function dirSize(dir) {
  const files = await walkFilesAsync(dir, 0, [])
  const sizes = await Promise.all(files.map(f => fs.promises.stat(f).then(x => x.size, () => 0)))
  return sizes.reduce((a, b) => a + b, 0)
}

ipcMain.handle('preview:cacheSize', () => dirSize(previewDir))

ipcMain.handle('preview:clearCache', () => {
  try { fs.rmSync(previewDir, { recursive: true, force: true }) } catch (e) { return { ok: false } }
  return { ok: true }
})

const EXPORT_DIRS = { raw: 'Raw', json: 'JSON', texture: 'Textures', sound: 'Sounds', model: 'Models' }
let exporting = false
let exportStop = false

function packagePath(p) {
  let t = String(p || '').trim().replace(/\\/g, '/').replace(/[\t\r\n]/g, ' ')
  if (t.startsWith('/Game/')) t = '/FortniteGame/Content/' + t.slice(6)
  else if (t.startsWith('/Engine/')) t = '/Engine/Content/' + t.slice(8)
  return t
}

function askExport(kind, p, out) {
  const key = kind + '\t' + p
  return new Promise(resolve => {
    if (!typeServer) {
      resolve({ ok: false, error: 'the helper is not running' })
      return
    }
    const timer = setTimeout(() => {
      exportWaiters.delete(key)
      resolve({ ok: false, error: 'timed out' })
    }, 5 * 60 * 1000)
    exportWaiters.set(key, r => {
      clearTimeout(timer)
      resolve(r)
    })
    try {
      typeServer.stdin.write('export\t' + kind + '\t' + p + '\t' + out + '\n')
    } catch (e) {
      exportWaiters.delete(key)
      clearTimeout(timer)
      resolve({ ok: false, error: 'the helper is not running' })
    }
  })
}

ipcMain.handle('export:run', async (_e, payload) => {
  const kind = payload && payload.kind
  if (!EXPORT_DIRS[kind]) return { ok: false, error: 'kind' }
  if (exporting) return { ok: false, error: 'busy' }
  const paths = Array.from(new Set((payload.paths || []).map(packagePath).filter(Boolean)))
  if (!paths.length) return { ok: false, error: 'no paths' }
  exporting = true
  exportStop = false
  try {
    const up = await startTypeServer()
    if (!up) return { ok: false, error: 'no-helper' }
    const base = payload.dir && typeof payload.dir === 'string' && path.isAbsolute(payload.dir) ? payload.dir : exportDirNow()
    // Custom folder: every file goes straight into the chosen folder.
    // Regular export: each file goes into the same folder path it has in the game files.
    const outFor = p => {
      if (payload.direct) return base
      const segs = p.split('/').filter(s => s && s !== '.' && s !== '..' && !/[<>:"|?*]/.test(s))
      segs.pop()
      return path.join(base, ...segs)
    }
    const dirs = []
    fs.mkdirSync(base, { recursive: true })
    const results = []
    for (let i = 0; i < paths.length; i++) {
      if (exportStop) break
      const out = outFor(paths[i])
      fs.mkdirSync(out, { recursive: true })
      send('export:progress', { kind, i: i + 1, n: paths.length, path: paths[i] })
      const r = await askExport(kind === 'model' ? 'modelexport' : kind, paths[i], out)
      if (r && r.ok) dirs.push(out)
      results.push(Object.assign({ path: paths[i] }, r))
      send('export:item', Object.assign({ kind, path: paths[i] }, r))
    }
    // Report the deepest folder that holds everything exported
    let shown = dirs.length ? dirs[0] : base
    for (const d of dirs) {
      const a = shown.split(path.sep), b = d.split(path.sep)
      let n = 0
      while (n < a.length && n < b.length && a[n].toLowerCase() === b[n].toLowerCase()) n++
      shown = a.slice(0, n).join(path.sep) || base
    }
    send('export:done', { kind, results, dir: shown })
    return { ok: true, results, dir: shown }
  } finally {
    exporting = false
  }
})

ipcMain.handle('export:stop', () => {
  exportStop = true
  const pending = Array.from(exportWaiters.values())
  exportWaiters.clear()
  pending.forEach(w => w({ ok: false, error: 'cancelled' }))
  return true
})

ipcMain.handle('export:dir', () => {
  fs.mkdirSync(exportDirNow(), { recursive: true })
  return exportDirNow()
})

ipcMain.handle('clipboard:read', () => clipboard.readText())

// Opening is for folders and ordinary files only; programs and scripts are never launched from the page
const NEVER_OPEN = /\.(exe|bat|cmd|com|msi|msix|msp|scr|pif|ps1|psm1|psd1|vbs|vbe|js|jse|wsf|wsh|hta|jar|lnk|url|reg|cpl|dll|appx|appinstaller|application|gadget|msc|inf|scf|sh)$/i
ipcMain.handle('shell:open', async (_e, target) => {
  if (typeof target !== 'string' || !target || target.length > 1000 || !path.isAbsolute(target)) return 'Invalid path'
  try {
    const st = await fs.promises.stat(target)
    if (!st.isDirectory() && NEVER_OPEN.test(target)) return 'That kind of file cannot be opened from here'
  } catch (e) { return 'Not found' }
  return shell.openPath(target)
})
const EXTERNAL_OK = ['https://github.com/halfuwu', 'https://halfheart.dev', 'https://discord.gg/FortnitePorting']
ipcMain.handle('shell:external', (_e, url) => {
  if (typeof url !== 'string' || !EXTERNAL_OK.some(ok => url === ok || url.startsWith(ok + '/'))) return false
  shell.openExternal(url)
  return true
})

ipcMain.handle('shell:show', (_e, target) => {
  if (typeof target !== 'string' || !target || target.length > 1000 || !path.isAbsolute(target)) return false
  shell.showItemInFolder(target)
  return true
})

ipcMain.handle('dialog:files', async () => {
  const r = await dialog.showOpenDialog(win, {
    properties: ['openFile', 'multiSelections'],
    filters: [{ name: 'Shaders', extensions: ['shader'] }],
    defaultPath: conversionsDir
  })
  return r.canceled ? [] : r.filePaths
})

ipcMain.handle('dialog:folder', async () => {
  const r = await dialog.showOpenDialog(win, {
    properties: ['openDirectory', 'multiSelections'],
    defaultPath: conversionsDir
  })
  return r.canceled ? [] : r.filePaths
})

ipcMain.handle('dialog:exportdir', async () => {
  const r = await dialog.showOpenDialog(win, { properties: ['openDirectory', 'createDirectory'], defaultPath: exportDirNow() })
  return r.canceled || !r.filePaths.length ? '' : r.filePaths[0]
})

ipcMain.handle('dialog:fnp', async () => {
  const r = await dialog.showOpenDialog(win, {
    properties: ['openFile'],
    filters: [{ name: 'FortnitePorting', extensions: ['exe'] }]
  })
  return r.canceled ? '' : r.filePaths[0]
})

ipcMain.handle('dialog:unity', async () => {
  const r = await dialog.showOpenDialog(win, {
    properties: ['openFile'],
    filters: [{ name: 'Unity editor', extensions: ['exe'] }]
  })
  return r.canceled ? '' : r.filePaths[0]
})
