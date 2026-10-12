const fs = require('fs')

const defaults = {
  bridge: 'http://localhost:24320',
  fnpPath: '',
  autoStartFnp: false,
  autoStartApi: true,
  minimizeFnp: true,
  unity: '',
  accent: '#8e22eb',
  theme: {},
  themeName: '',
  useExportsPath: false,
  exportsPath: '',
  fortniteArchive: '',
  tabTransitions: true,
  volume: 1,
  audioDevice: '',
  fix: true,
  useUnity: true,
  checkUpdates: true,
  updateToken: '',
  lastVersion: '',
  pendingNotes: '',
  pendingFor: ''
}

function load(file) {
  try {
    const data = Object.assign({}, defaults, JSON.parse(fs.readFileSync(file, 'utf8')))
    if (['#00d4ff', '#8a45fa'].includes(String(data.accent).toLowerCase())) data.accent = defaults.accent
    return data
  } catch (e) {
    return Object.assign({}, defaults)
  }
}

function save(file, data) {
  fs.writeFileSync(file, JSON.stringify(data, null, 2), 'utf8')
}

module.exports = { load, save, defaults }
