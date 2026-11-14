const { contextBridge, ipcRenderer } = require('electron')

function listen(channel, cb) {
  const fn = (_e, data) => cb(data)
  ipcRenderer.on(channel, fn)
  return () => ipcRenderer.removeListener(channel, fn)
}

contextBridge.exposeInMainWorld('tester', {
  snapshot: () => ipcRenderer.invoke('t:snapshot'),
  publish: o => ipcRenderer.invoke('t:publish', o),
  withdraw: () => ipcRenderer.invoke('t:withdraw'),
  reset: () => ipcRenderer.invoke('t:reset'),
  behavior: b => ipcRenderer.invoke('t:behavior', b),
  launch: o => ipcRenderer.invoke('t:launch', o),
  stop: () => ipcRenderer.invoke('t:stop'),
  clearLog: () => ipcRenderer.invoke('t:clearlog'),
  minimize: () => ipcRenderer.invoke('win:min'),
  toggleMaximize: () => ipcRenderer.invoke('win:max'),
  close: () => ipcRenderer.invoke('win:close'),
  onLog: cb => listen('t:log', cb),
  onAppState: cb => listen('app:state', cb)
})
