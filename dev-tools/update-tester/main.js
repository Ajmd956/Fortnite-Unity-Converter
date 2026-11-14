const { app, BrowserWindow, ipcMain } = require('electron')
const path = require('path')
const fs = require('fs')
const { spawn } = require('child_process')
const { createMock } = require('./mock-server')

const appDir = path.join(__dirname, '..', '..', 'app')
const mock = createMock()
let win = null
let child = null

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')) } catch (e) { return {} }
}

function snapshot() {
  return {
    url: mock.base(),
    state: mock.state,
    log: mock.log.slice(-200),
    running: !!child,
    appVersion: readJson(path.join(appDir, 'package.json')).version || '',
    repo: readJson(path.join(appDir, 'update.json')).repo || '',
    appFound: fs.existsSync(path.join(appDir, 'main.js'))
  }
}

function send(channel, data) {
  if (win && !win.isDestroyed()) win.webContents.send(channel, data)
}

function launch(mode, baseVersion) {
  if (child) return { ok: false, error: 'The test app is already running.' }
  const base = String(baseVersion || '').trim().replace(/^v/i, '')
  if (!/^\d+\.\d+\.\d+$/.test(base)) return { ok: false, error: 'The app version must look like 2.0.0' }
  if (!fs.existsSync(path.join(appDir, 'main.js'))) return { ok: false, error: 'The app folder was not found next to dev-tools.' }
  mock.state.baseVersion = base
  const env = Object.assign({}, process.env, { FUC_DEV_UPDATE_URL: mock.base() })
  delete env.ELECTRON_RUN_AS_NODE
  if (mode === 'updated') {
    const top = mock.state.history[0]
    if (!top) return { ok: false, error: 'Publish a test update first. Its notes are what the pop-up shows.' }
    env.FUC_DEV_VERSION = top.version
    env.FUC_DEV_UPDATED = base
  } else {
    env.FUC_DEV_VERSION = base
  }
  mock.note({ kind: 'event', text: 'Launching the app as v' + env.FUC_DEV_VERSION + (mode === 'updated' ? ' (first start after updating from v' + base + ')' : '') })
  child = spawn(process.execPath, [appDir], { env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: false })
  const out = d => String(d).split('\n').map(s => s.trim()).filter(Boolean).forEach(s => mock.note({ kind: 'app', text: s }))
  child.stdout.on('data', out)
  child.stderr.on('data', out)
  child.on('exit', code => {
    child = null
    mock.note({ kind: 'event', text: 'The test app closed' + (code ? ' (code ' + code + ')' : '') })
    send('app:state', false)
  })
  child.on('error', e => {
    child = null
    mock.note({ kind: 'event', text: 'The test app could not start: ' + e.message })
    send('app:state', false)
  })
  send('app:state', true)
  return { ok: true }
}

function stop() {
  if (child) child.kill()
  return { ok: true }
}

ipcMain.handle('t:snapshot', () => snapshot())
ipcMain.handle('t:publish', (_e, o) => {
  try {
    mock.publish(o)
    return { ok: true, snap: snapshot() }
  } catch (e) {
    return { ok: false, error: e.message }
  }
})
ipcMain.handle('t:withdraw', () => { mock.withdraw(); return snapshot() })
ipcMain.handle('t:reset', () => { mock.reset(); return snapshot() })
ipcMain.handle('t:behavior', (_e, b) => {
  mock.state.behavior = ['normal', 'slow', 'http500', 'badhash', 'noasset'].includes(b) ? b : 'normal'
  return snapshot()
})
ipcMain.handle('t:launch', (_e, o) => launch(o && o.mode, o && o.base))
ipcMain.handle('t:stop', () => stop())
ipcMain.handle('t:clearlog', () => { mock.log.length = 0; return true })
ipcMain.handle('win:min', () => win && win.minimize())
ipcMain.handle('win:max', () => { if (win) { if (win.isMaximized()) win.unmaximize(); else win.maximize() } })
ipcMain.handle('win:close', () => win && win.close())

app.whenReady().then(async () => {
  await mock.listen(24399)
  mock.onLog(e => send('t:log', e))
  win = new BrowserWindow({
    width: 1100,
    height: 760,
    minWidth: 900,
    minHeight: 600,
    frame: false,
    backgroundColor: '#0D0C12',
    title: 'Fortnite Unity Update Tester',
    icon: path.join(appDir, 'assets', 'icon.ico'),
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false }
  })
  win.setMenuBarVisibility(false)
  win.loadFile(path.join(__dirname, 'index.html'))
})

app.on('before-quit', () => {
  if (child) child.kill()
  mock.close()
})

app.on('window-all-closed', () => app.quit())
