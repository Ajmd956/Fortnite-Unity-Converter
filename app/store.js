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
  gameVersion: '',
  ueVersion: '',
  localTextures: false,
  profiles: [],
  activeProfile: '',
  tabTransitions: true,
  volume: 1,
  audioDevice: '',
  fix: true,
  useUnity: true,
  checkUpdates: true,
  updateToken: '',
  showExperimental: false,
  expParticle: false,
  expBlueprint: false,
  lastVersion: '',
  pendingNotes: '',
  pendingFor: ''
}

// Installation settings live in profiles, with the same fields Fortnite Porting's InstallationProfile has.
// fortniteVersion: 'LatestInstalled' | 'LatestOnDemand' | 'Custom'
const ZERO_KEY = '0x' + '0'.repeat(64)

function newProfile(o) {
  return Object.assign({
    id: 'p' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
    profileName: 'Unnamed',
    fortniteVersion: 'LatestInstalled',
    archiveDirectory: '',
    unrealVersion: 'GAME_UE6_0',
    mainKey: ZERO_KEY,
    extraKeys: [],
    useMappingsFile: false,
    mappingsFile: '',
    gameLanguage: 'English',
    useTextureStreaming: true,
    loadInstalledBundles: true,
    loadNaniteData: true
  }, o || {})
}

// Turns the first-run flat settings, or an earlier profile layout, into the current profile layout
function fixProfile(p) {
  const q = newProfile(p)
  if (p.name && !p.profileName) q.profileName = p.name
  if (p.mode && !p.fortniteVersion) q.fortniteVersion = p.mode === 'custom' ? 'Custom' : 'LatestInstalled'
  if (p.archive && !p.archiveDirectory) q.archiveDirectory = p.archive
  if (p.ueVersion && !p.unrealVersion) q.unrealVersion = p.ueVersion
  if (p.unrealVersion === '') q.unrealVersion = 'GAME_UE6_0'
  if (typeof p.onDemand === 'boolean' && p.useTextureStreaming === undefined) q.useTextureStreaming = p.onDemand
  if (typeof p.bundles === 'boolean' && p.loadInstalledBundles === undefined) q.loadInstalledBundles = p.bundles
  if (typeof p.nanite === 'boolean' && p.loadNaniteData === undefined) q.loadNaniteData = p.nanite
  if (Array.isArray(p.keys) && !Array.isArray(p.extraKeys) || (Array.isArray(p.keys) && p.keys.length && !(p.extraKeys || []).length)) {
    const keys = p.keys.map(k => String(k.key || '').trim()).filter(Boolean)
    if (keys.length) { q.mainKey = keys[0]; q.extraKeys = keys.slice(1) }
  }
  if (!Array.isArray(q.extraKeys)) q.extraKeys = []
  q.extraKeys = q.extraKeys.map(k => (typeof k === 'string' ? k : String((k && k.key) || '')))
  if (!q.mainKey) q.mainKey = ZERO_KEY
  for (const k of ['name', 'mode', 'archive', 'ueVersion', 'onDemand', 'bundles', 'nanite', 'keys', 'gameVersion']) delete q[k]
  return q
}

function migrateProfiles(data) {
  if (Array.isArray(data.profiles) && data.profiles.length) {
    data.profiles = data.profiles.map(fixProfile)
    if (!data.profiles.some(p => p.id === data.activeProfile)) data.activeProfile = data.profiles[0].id
    return
  }
  const custom = !!String(data.gameVersion || '').trim()
  const p = newProfile({
    id: 'default',
    profileName: custom ? String(data.gameVersion).trim() : 'Fortnite',
    fortniteVersion: custom ? 'Custom' : 'LatestInstalled',
    archiveDirectory: data.fortniteArchive || '',
    unrealVersion: String(data.ueVersion || '').trim() || 'GAME_UE6_0',
    useTextureStreaming: data.localTextures !== true
  })
  data.profiles = [p]
  data.activeProfile = p.id
}

function load(file) {
  try {
    const data = Object.assign({}, defaults, JSON.parse(fs.readFileSync(file, 'utf8')))
    migrateProfiles(data)
    if (['#00d4ff', '#8a45fa'].includes(String(data.accent).toLowerCase())) data.accent = defaults.accent
    return data
  } catch (e) {
    const data = Object.assign({}, defaults)
    migrateProfiles(data)
    return data
  }
}

function save(file, data) {
  fs.writeFileSync(file, JSON.stringify(data, null, 2), 'utf8')
}

module.exports = { load, save, defaults, newProfile, ZERO_KEY }
