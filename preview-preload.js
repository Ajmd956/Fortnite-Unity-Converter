const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('pv', {
  job: () => ipcRenderer.invoke('pv:job'),
  read: file => ipcRenderer.invoke('pv:read', file),
  saveAs: file => ipcRenderer.invoke('pv:save', file),
  reveal: file => ipcRenderer.invoke('pv:reveal', file),
  gotoFile: p => ipcRenderer.invoke('pv:goto', p),
  openJson: (name, json) => ipcRenderer.invoke('pvm:open-json', name, json),
  settings: () => ipcRenderer.invoke('settings:get'),
  copy: text => ipcRenderer.invoke('clipboard:write', text),
  multiInit: () => ipcRenderer.invoke('pvm:init'),
  multiJob: id => ipcRenderer.invoke('pvm:job', id),
  materials: id => ipcRenderer.invoke('pvm:materials', id),
  openProps: p => ipcRenderer.invoke('pvm:open-props', p),
  openMaterial: p => ipcRenderer.invoke('pvm:open-material', p),
  thumb: p => ipcRenderer.invoke('pvm:thumb', p),
  onAdd: cb => ipcRenderer.on('pvm:add', (_e, p) => cb(p)),
  onReset: cb => ipcRenderer.on('pvm:reset', () => cb()),
  win: {
    minimize: () => ipcRenderer.invoke('win:minimize'),
    toggleMaximize: () => ipcRenderer.invoke('win:toggle-maximize'),
    close: () => ipcRenderer.invoke('win:close'),
    onState: cb => ipcRenderer.on('win:state', (_e, m) => cb(m))
  }
})
