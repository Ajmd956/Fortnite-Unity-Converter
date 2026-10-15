const { contextBridge, ipcRenderer } = require('electron')

let pathOf = f => f.path
try {
  const { webUtils } = require('electron')
  if (webUtils && webUtils.getPathForFile) pathOf = f => webUtils.getPathForFile(f)
} catch (e) {}

function listen(channel, cb) {
  const fn = (_e, data) => cb(data)
  ipcRenderer.on(channel, fn)
  return () => ipcRenderer.removeListener(channel, fn)
}

contextBridge.exposeInMainWorld('api', {
  info: () => ipcRenderer.invoke('app:info'),
  ping: () => ipcRenderer.invoke('bridge:ping'),
  ensureFnp: () => ipcRenderer.invoke('fnp:ensure'),
  apiState: () => ipcRenderer.invoke('api:state'),
  startApi: () => ipcRenderer.invoke('api:start'),
  ensureApi: () => ipcRenderer.invoke('api:ensure'),
  onApi: cb => listen('api:state', cb),
  filesContainers: () => ipcRenderer.invoke('files:containers'),
  exportRun: payload => ipcRenderer.invoke('export:run', payload),
  exportStop: () => ipcRenderer.invoke('export:stop'),
  exportDir: () => ipcRenderer.invoke('export:dir'),
  onExportProgress: cb => listen('export:progress', cb),
  onExportItem: cb => listen('export:item', cb),
  onExportDone: cb => listen('export:done', cb),
  pickFnp: () => ipcRenderer.invoke('dialog:fnp'),
  startup: () => ipcRenderer.invoke('startup:check'),
  getSettings: () => ipcRenderer.invoke('settings:get'),
  setSettings: s => ipcRenderer.invoke('settings:set', s),
  convert: payload => ipcRenderer.invoke('engine:convert', payload),
  convertNiagara: payload => ipcRenderer.invoke('niagara:convert', payload),
  check: payload => ipcRenderer.invoke('engine:check', payload),
  stop: () => ipcRenderer.invoke('engine:stop'),
  shaders: () => ipcRenderer.invoke('shaders:list'),
  readClipboard: () => ipcRenderer.invoke('clipboard:read'),
  updatedInfo: () => ipcRenderer.invoke('app:updated'),
  releaseNotes: () => ipcRenderer.invoke('app:releasenotes'),
  open: p => ipcRenderer.invoke('shell:open', p),
  external: url => ipcRenderer.invoke('shell:external', url),
  relaunch: () => ipcRenderer.invoke('app:relaunch'),
  resetSettings: () => ipcRenderer.invoke('app:reset'),
  minimize: () => ipcRenderer.invoke('win:minimize'),
  toggleMaximize: () => ipcRenderer.invoke('win:toggle-maximize'),
  closeWindow: () => ipcRenderer.invoke('win:close'),
  onMaximized: cb => listen('win:state', cb),
  show: p => ipcRenderer.invoke('shell:show', p),
  pickFiles: () => ipcRenderer.invoke('dialog:files'),
  pickFolder: () => ipcRenderer.invoke('dialog:folder'),
  pickExportDir: () => ipcRenderer.invoke('dialog:exportdir'),
  pickUnity: () => ipcRenderer.invoke('dialog:unity'),
  pathOf: file => pathOf(file),
  onLog: cb => listen('engine:log', cb),
  onNotice: cb => listen('app:notice', cb),
  filesInfo: () => ipcRenderer.invoke('files:info'),
  filesBuild: () => ipcRenderer.invoke('files:build'),
  filesSearch: q => ipcRenderer.invoke('files:search', q),
  filesList: q => ipcRenderer.invoke('files:list', q),
  filesTypes: paths => ipcRenderer.invoke('files:types', paths),
  copyText: t => ipcRenderer.invoke('clipboard:write', t),
  newsImages: () => ipcRenderer.invoke('news:images'),
  homeScreens: () => ipcRenderer.invoke('home:screens'),
  homeFetchImg: u => ipcRenderer.invoke('home:fetchimg', u),
  homeScreenImg: p => ipcRenderer.invoke('home:screenimg', p),
  filesThumb: p => ipcRenderer.invoke('files:thumb', p),
  preview: payload => ipcRenderer.invoke('preview:open', payload),
  previewMaterialTree: path => ipcRenderer.invoke('preview:material-tree', path),
  previewCacheSize: () => ipcRenderer.invoke('preview:cacheSize'),
  previewClearCache: () => ipcRenderer.invoke('preview:clearCache'),
  onFilesStatus: cb => listen('files:status', cb),
  onState: cb => listen('engine:state', cb),
  onDone: cb => listen('convert:done', cb),
  updatePending: () => ipcRenderer.invoke('update:pending'),
  updateDownload: () => ipcRenderer.invoke('update:download'),
  updateCheck: () => ipcRenderer.invoke('update:check'),
  aiStatus: () => ipcRenderer.invoke('ai:status'),
  aiSetKey: (key, providerId) => ipcRenderer.invoke('ai:setKey', key, providerId),
  aiChat: payload => ipcRenderer.invoke('ai:chat', payload),
  appVersion: () => ipcRenderer.invoke('app:version'),
  onGoto: cb => listen('files:goto', cb),
  onUpdate: cb => listen('update:available', cb),
  onUpdateProgress: cb => listen('update:progress', cb)
})
