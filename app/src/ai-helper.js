(() => {
  const el = id => document.getElementById(id)
  const wait = ms => new Promise(r => setTimeout(r, ms))
  const TEXT_LIMIT = 6000

  const targets = []
  const add = (id, label, sel, desc, tab) => targets.push({ id, label, sel, desc: desc || '', tab: tab || '' })

  add('tab-home', 'Home tab', '.tab.nav[data-tab="home"]', 'status and news')
  add('tab-updates', 'News & Updates tab', '.tab.nav[data-tab="updates"]', 'release notes')
  add('tab-files', 'Files tab', '.tab.nav[data-tab="files"]', 'browse, preview and export game files')
  add('tab-convert', 'Shader Converter tab', '.tab.nav[data-tab="convert"]', 'turn materials into Unity shaders')
  add('tab-niagara', 'Niagara Converter tab', '.tab.nav[data-tab="niagara"]', 'turn Niagara particle systems into Unity particles')
  add('tab-shaders', 'My Shaders tab', '.tab.nav[data-tab="shaders"]', 'list of converted shaders')
  add('tab-check', 'Check Shaders tab', '.tab.nav[data-tab="check"]', 'scan and fix shader problems')
  add('tab-export', 'Export Options tab', '.tab.nav[data-tab="export"]', 'auto fix and compile defaults')
  add('tab-settings', 'App Settings tab', '.tab.nav[data-tab="settings"]', 'app settings')
  add('files-search', 'Files search box', '#fileQuery', 'search game files', 'files')
  add('files-type', 'Files type filter', '#fileKindBtn', 'filter by asset type', 'files')
  add('files-export', 'Files export button', '#fileExpBtn', 'export the selected files', 'files')
  add('files-unity', 'Files Unity button', '#fileUnity', 'send selection to Unity or convert', 'files')
  add('files-cancel', 'Files cancel button', '#fileCancel', 'stop a running export', 'files')
  add('convert-paths', 'Material paths box', '#materials', 'where material paths go', 'convert')
  add('convert-paste', 'Paste button', '#pasteMaterials', 'paste paths from the clipboard', 'convert')
  add('convert-name', 'Shader name box', '#shaderName', 'optional name for one material', 'convert')
  add('convert-run', 'Convert button', '#convertBtn', 'start converting', 'convert')
  add('niagara-paths', 'Niagara paths box', '#niagaraPaths', 'where Niagara system paths go', 'niagara')
  add('niagara-run', 'Niagara Convert button', '#niagaraBtn', 'start converting Niagara systems', 'niagara')
  add('shaders-refresh', 'Refresh button on My Shaders', '#refreshShaders', 'reload the converted shaders list', 'shaders')
  add('check-drop', 'Shader drop area', '#drop', 'drop .shader files or folders', 'check')
  add('check-add-files', 'Add files button', '#addFiles', 'pick .shader files', 'check')
  add('check-add-folder', 'Add folder button', '#addFolder', 'pick a folder of shaders', 'check')
  add('check-fix', 'Fix problems option', '#optFix', 'apply safe automatic fixes', 'check')
  add('check-unity', 'Compile in Unity option', '#optUnity', 'compile each shader in the Unity Editor', 'check')
  add('check-ai', 'AI fix option', '#optAi', 'let the AI repair remaining errors', 'check')
  add('check-run', 'Check button', '#checkBtn', 'start the check', 'check')
  add('output-log', 'Output window', '.logbox', 'shows what the app is doing')
  add('api-status', 'API status indicator', '#bridgeBtn', 'shows whether the game-file API is running')
  add('settings-ai-key', 'AI API key box', '#setAiKey', 'where the AI key is entered', 'settings')
  add('settings-ai-model', 'Helper model box', '#setAiModel', 'model used by the helper', 'settings')
  add('settings-ai-fixmodel', 'Fix model box', '#setAiFixModel', 'model used for shader fixes', 'settings')
  add('settings-updates', 'Check for updates switch', '#setCheckUpd', 'automatic update checks', 'settings')
  add('settings-unity', 'Unity Editor path box', '#setUnity', 'path to Unity.exe', 'settings')
  add('settings-archive', 'Game Archive Folder box', '#setArchive', 'Fortnite Paks folder', 'settings')
  add('settings-api', 'API Address box', '#setBridge', 'address of the game-file API', 'settings')

  const byId = {}
  targets.forEach(t => { byId[t.id] = t })

  const history = []
  const ctx = { selection: '', region: '', log: false }
  let lastSel = ''
  let hasKey = false
  let busy = false
  let ringTimer = null

  const ICON = {
    spark: 'M12,2L14.4,8.6L21,11L14.4,13.4L12,20L9.6,13.4L3,11L9.6,8.6L12,2M19,16L19.9,18.1L22,19L19.9,19.9L19,22L18.1,19.9L16,19L18.1,18.1L19,16Z',
    trash: 'M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z',
    close: 'M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z',
    send: 'M2,21L23,12L2,3V10L17,12L2,14V21Z',
    pick: 'M12,8A4,4 0 0,0 8,12A4,4 0 0,0 12,16A4,4 0 0,0 16,12A4,4 0 0,0 12,8M3.05,13H1V11H3.05C3.5,6.83 6.83,3.5 11,3.05V1H13V3.05C17.17,3.5 20.5,6.83 20.95,11H23V13H20.95C20.5,17.17 17.17,20.5 13,20.95V23H11V20.95C6.83,20.5 3.5,17.17 3.05,13M12,5A7,7 0 0,0 5,12A7,7 0 0,0 12,19A7,7 0 0,0 19,12A7,7 0 0,0 12,5Z',
    log: 'M20,19V7H4V19H20M20,3A2,2 0 0,1 22,5V19A2,2 0 0,1 20,21H4A2,2 0 0,1 2,19V5C2,3.89 2.9,3 4,3H20M13,17V15H18V17H13M9.58,13L5.57,9H8.4L11.7,12.3C12.09,12.69 12.09,13.33 11.7,13.72L8.4,17H5.57L9.58,13Z',
    paste: 'M19,20H5V4H7V7H17V4H19M12,2A1,1 0 0,1 13,3A1,1 0 0,1 12,4A1,1 0 0,1 11,3A1,1 0 0,1 12,2M19,2H14.82C14.4,0.84 13.3,0 12,0C10.7,0 9.6,0.84 9.18,2H5A2,2 0 0,0 3,4V20A2,2 0 0,0 5,22H19A2,2 0 0,0 21,20V4A2,2 0 0,0 19,2Z',
    chat: 'M20,2H4A2,2 0 0,0 2,4V22L6,18H20A2,2 0 0,0 22,16V4A2,2 0 0,0 20,2Z',
    grip: 'M9,3A2,2 0 1,0 9,7A2,2 0 1,0 9,3M15,3A2,2 0 1,0 15,7A2,2 0 1,0 15,3M9,10A2,2 0 1,0 9,14A2,2 0 1,0 9,10M15,10A2,2 0 1,0 15,14A2,2 0 1,0 15,10M9,17A2,2 0 1,0 9,21A2,2 0 1,0 9,17M15,17A2,2 0 1,0 15,21A2,2 0 1,0 15,17Z'
  }
  const icon = name => '<svg viewBox="0 0 24 24" fill="currentColor"><path d="' + ICON[name] + '"/></svg>'

  let prov = { id: 'openai', short: 'ChatGPT', name: 'ChatGPT (OpenAI)' }
  let shownProv = ''

  function sparkNode() {
    const s = document.createElement('span')
    s.className = 'ai-spark'
    s.innerHTML = icon('spark')
    return s
  }

  function logoNode(pid) {
    const lid = pid || prov.id
    if (!lid || lid === 'custom') return sparkNode()
    const wrap = document.createElement('span')
    wrap.className = 'ai-lg'
    const img = document.createElement('img')
    img.alt = ''
    img.draggable = false
    const tries = ['svg', 'png']
    let i = 0
    const next = () => {
      if (i < tries.length) img.src = '../assets/ai/' + lid + '.' + tries[i++]
      else wrap.replaceWith(sparkNode())
    }
    img.addEventListener('error', next)
    next()
    wrap.appendChild(img)
    return wrap
  }

  const SUGGESTIONS = [
    'How do I convert a Fortnite material to a Unity shader?',
    'What does Check Shaders do?',
    'Where do I set my Unity Editor path?',
    'I have a shader error. What should I do?'
  ]

  const FAB_H = 44
  const TOP_GAP = 50
  const BOTTOM_GAP = 44
  const SIDE_GAP = 16
  let pos = { side: 'r', fy: 1 }
  try {
    const sp = JSON.parse(localStorage.getItem('aiPos') || 'null')
    if (sp && (sp.side === 'l' || sp.side === 'r') && isFinite(sp.fy)) pos = { side: sp.side, fy: Math.min(1, Math.max(0, sp.fy)) }
    else {
      const c = localStorage.getItem('aiCorner')
      if (c && c.length === 2) pos = { side: c[1] === 'l' ? 'l' : 'r', fy: c[0] === 't' ? 0 : 1 }
    }
  } catch (e) {}

  const panelH = () => Math.min(600, Math.max(200, window.innerHeight - 150))
  const fabRange = () => {
    const lo = TOP_GAP
    return [lo, Math.max(lo, window.innerHeight - BOTTOM_GAP - FAB_H)]
  }
  const fabTopOf = fy => {
    const [lo, hi] = fabRange()
    return lo + fy * (hi - lo)
  }
  function panelTopFor(fabTop) {
    const ph = panelH()
    const lo = TOP_GAP
    const hi = Math.max(lo, window.innerHeight - BOTTOM_GAP - ph)
    return Math.min(hi, Math.max(lo, fabTop + FAB_H - ph))
  }
  function place(node, side, top) {
    node.style.top = top + 'px'
    node.style.bottom = 'auto'
    if (side === 'l') {
      node.style.left = SIDE_GAP + 'px'
      node.style.right = 'auto'
    } else {
      node.style.right = SIDE_GAP + 'px'
      node.style.left = 'auto'
    }
  }
  function applyPos() {
    const ft = fabTopOf(pos.fy)
    place(fab, pos.side, ft)
    place(panel, pos.side, panelTopFor(ft))
    document.body.classList.toggle('ai-side-l', pos.side === 'l')
    document.body.classList.toggle('ai-side-r', pos.side === 'r')
  }
  function targetFor(node, left, top, w, h) {
    const side = left + w / 2 < window.innerWidth / 2 ? 'l' : 'r'
    const [lo, hi] = fabRange()
    const ft = node === fab ? top : top + h - FAB_H
    const fy = hi > lo ? Math.min(1, Math.max(0, (ft - lo) / (hi - lo))) : 1
    return { side, fy }
  }
  function savePos() {
    try {
      localStorage.setItem('aiPos', JSON.stringify(pos))
    } catch (e) {}
  }

  const fab = document.createElement('button')
  fab.id = 'aiFab'
  const askSpan = document.createElement('span')
  askSpan.textContent = 'Ask'
  fab.append(sparkNode(), askSpan)

  const panel = document.createElement('div')
  panel.id = 'aiPanel'
  panel.innerHTML =
    '<div class="ai-head" id="aiHead">' +
    '<div class="ai-badge" id="aiBadge">' + icon('spark') + '</div>' +
    '<div class="ai-ttl"><b id="aiName">Helper</b><i>Drag this bar to move me</i></div>' +
    '<button class="ai-ib" id="aiClear" title="Clear chat">' + icon('trash') + '</button>' +
    '<button class="ai-ib" id="aiClose" title="Close">' + icon('close') + '</button>' +
    '</div>' +
    '<div class="ai-msgs" id="aiMsgs"></div>' +
    '<div class="ai-foot">' +
    '<div class="ai-chips" id="aiChips" style="padding:0"></div>' +
    '<div class="ai-tools">' +
    '<button class="ai-tool" id="aiPickBtn" title="Click any part of the app to ask about it">' + icon('pick') + 'Pick area</button>' +
    '<button class="ai-tool" id="aiLogBtn" title="Include the latest output">' + icon('log') + 'Add output</button>' +
    '<button class="ai-tool" id="aiPasteBtn" title="Paste the clipboard into the message">' + icon('paste') + 'Paste</button>' +
    '</div>' +
    '<div class="ai-compose"><textarea id="aiInput" rows="1" spellcheck="false" placeholder="Ask anything about the app"></textarea><button id="aiSend" title="Send">' + icon('send') + '</button></div>' +
    '</div>'

  const snap = document.createElement('div')
  snap.id = 'aiSnap'
  const ring = document.createElement('div')
  ring.id = 'aiRing'
  const ringTag = document.createElement('div')
  ringTag.className = 'ai-ring-tag'
  ring.appendChild(ringTag)
  const pickBox = document.createElement('div')
  pickBox.id = 'aiPick'
  const pickBar = document.createElement('div')
  pickBar.className = 'ai-pickbar'
  pickBar.textContent = 'Click the part you want to ask about. Esc to cancel.'

  document.body.append(fab, panel, snap, ring, pickBox, pickBar)

  const syncOn = () => {
    const v = getComputedStyle(document.documentElement).getPropertyValue('--on-primary').trim().toLowerCase()
    document.body.classList.toggle('ai-dk', v === '#0b0a10')
  }
  new MutationObserver(syncOn).observe(document.documentElement, { attributes: true, attributeFilter: ['style'] })
  syncOn()
  document.body.classList.add('ai-off')
  applyPos()

  const msgs = el('aiMsgs')
  const input = el('aiInput')

  function avatar() {
    const a = document.createElement('div')
    a.className = 'ai-av'
    a.appendChild(logoNode())
    return a
  }

  function applyProvider() {
    if (shownProv === prov.id) return
    shownProv = prov.id
    fab.replaceChildren(logoNode(), askSpan)
    fab.title = 'Ask ' + prov.short + ' (Ctrl+Shift+H). Drag to move it along the left or right edge.'
    el('aiBadge').replaceChildren(logoNode())
    el('aiName').textContent = prov.short + ' Helper'
    input.placeholder = 'Ask ' + prov.short + ' anything about the app'
    if (document.body.classList.contains('ai-open')) renderSetup()
  }

  function say(kind, text, points, tag) {
    const row = document.createElement('div')
    row.className = 'ai-row ' + (kind === 'user' ? 'user' : 'bot')
    if (kind !== 'user') row.appendChild(avatar())
    const d = document.createElement('div')
    d.className = 'ai-msg ' + kind
    d.textContent = text
    if (tag) {
      const t = document.createElement('span')
      t.className = 'ai-tag'
      t.textContent = tag
      d.appendChild(t)
    }
    if (points && points.length) {
      const pts = document.createElement('div')
      pts.className = 'ai-pts'
      points.forEach(id => {
        const b = document.createElement('button')
        b.className = 'ai-pt'
        b.innerHTML = icon('pick')
        b.appendChild(document.createTextNode('Show me: ' + byId[id].label))
        b.onclick = () => pointAt(id)
        pts.appendChild(b)
      })
      d.appendChild(pts)
    }
    row.appendChild(d)
    msgs.appendChild(row)
    msgs.scrollTop = msgs.scrollHeight
    return row
  }

  function thinking() {
    const row = document.createElement('div')
    row.className = 'ai-row bot'
    row.appendChild(avatar())
    const d = document.createElement('div')
    d.className = 'ai-msg bot'
    d.innerHTML = '<div class="ai-dots"><i></i><i></i><i></i></div>'
    row.appendChild(d)
    msgs.appendChild(row)
    msgs.scrollTop = msgs.scrollHeight
    return row
  }

  function renderChips() {
    const box = el('aiChips')
    box.innerHTML = ''
    const mk = (label, clear) => {
      const c = document.createElement('div')
      c.className = 'ai-chip'
      const s = document.createElement('span')
      s.textContent = label
      const x = document.createElement('button')
      x.textContent = '×'
      x.onclick = () => { clear(); renderChips() }
      c.append(s, x)
      box.appendChild(c)
    }
    if (ctx.selection) mk('Selected: ' + ctx.selection.replace(/\s+/g, ' ').slice(0, 60), () => { ctx.selection = '' })
    if (ctx.region) mk('Picked ' + ctx.region.split('\n')[0].slice(0, 60), () => { ctx.region = '' })
    if (ctx.log) mk('Latest output', () => { ctx.log = false })
  }

  function renderSetup() {
    msgs.innerHTML = ''
    if (!hasKey) {
      const box = document.createElement('div')
      box.className = 'ai-setup'
      const badge = document.createElement('div')
      badge.className = 'ai-badge'
      badge.appendChild(logoNode())
      const t = document.createElement('div')
      t.textContent = 'Add your own ' + prov.short + ' API key in App Settings to use the helper (or pick a different AI there). It is stored encrypted on this computer only.'
      const b = document.createElement('button')
      b.className = 'btn primary'
      b.textContent = 'Open settings'
      b.onclick = async () => {
        showTab('settings')
        await pointAt('settings-ai-key')
      }
      box.append(badge, t, b)
      msgs.appendChild(box)
      return
    }
    if (!history.length) {
      const w = document.createElement('div')
      w.className = 'ai-welcome'
      w.innerHTML = '<div class="ai-badge"></div><b>How can I help?</b><p>Ask how something works, paste an error, select text or pick an area of the app. I will answer and point at the right button.</p>'
      w.querySelector('.ai-badge').appendChild(logoNode())
      msgs.appendChild(w)
      const list = document.createElement('div')
      list.className = 'ai-sugs'
      SUGGESTIONS.forEach(q => {
        const b = document.createElement('button')
        b.className = 'ai-sug'
        b.innerHTML = icon('chat')
        b.appendChild(document.createTextNode(q))
        b.onclick = () => { input.value = q; send() }
        list.appendChild(b)
      })
      msgs.appendChild(list)
      return
    }
    history.forEach(m => {
      if (m.role === 'user') say('user', m.shown, null, m.tag)
      else say('bot', m.shown || m.content, m.points)
    })
  }

  async function refreshKey() {
    const s = await window.api.aiStatus()
    const had = hasKey
    hasKey = !!s.hasKey
    const found = (s.providers || []).find(x => x.id === s.provider)
    if (found) prov = found
    applyProvider()
    if (window.aiPaintProvider) window.aiPaintProvider(s)
    const opt = el('optAi')
    if (opt) {
      opt.disabled = !hasKey
      if (!hasKey) opt.checked = false
      else if (!had) opt.checked = true
    }
    const note = el('aiOptNote')
    if (note) note.textContent = hasKey ? '' : ' (add a key in App Settings)'
    return hasKey
  }

  function openPanel() {
    document.body.classList.add('ai-open')
    if (lastSel) { ctx.selection = lastSel; lastSel = '' }
    refreshKey().then(() => { renderSetup(); renderChips(); input.focus() })
  }

  function closePanel() {
    document.body.classList.remove('ai-open')
  }

  function hideRing() {
    clearTimeout(ringTimer)
    ring.classList.remove('on')
    document.body.classList.remove('ai-peek')
  }

  function radiusOf(node, box) {
    const r = getComputedStyle(node).borderTopLeftRadius || '0'
    const lim = Math.min(box.width, box.height) / 2
    if (r.includes('%')) return Math.min(lim, (Math.min(box.width, box.height) * parseFloat(r)) / 100)
    return Math.min(lim, parseFloat(r) || 0)
  }

  function glowBox(g, node, box) {
    g.style.left = box.left + 'px'
    g.style.top = box.top + 'px'
    g.style.width = box.width + 'px'
    g.style.height = box.height + 'px'
    g.style.borderRadius = radiusOf(node, box) + 'px'
  }

  async function pointAt(id) {
    const t = byId[id]
    if (!t) return false
    const wrap = el('wrap')
    if (t.tab && wrap && wrap.dataset.tab !== t.tab) {
      showTab(t.tab)
      await wait(420)
    }
    const node = document.querySelector(t.sel)
    if (!node) return false
    const pane = node.closest('.subpane')
    if (pane && !pane.classList.contains('active')) {
      showSub(pane.id.replace(/^sub-/, ''))
      await wait(360)
    }
    node.scrollIntoView({ block: 'center', behavior: 'smooth' })
    const anchor = node.closest('.check-row, .tgl') || node
    let box = anchor.getBoundingClientRect()
    for (let i = 0; i < 30; i++) {
      await wait(60)
      const next = anchor.getBoundingClientRect()
      const still = Math.abs(next.top - box.top) < 0.5 && Math.abs(next.left - box.left) < 0.5
      box = next
      if (still && i > 2) break
    }
    if (!box.width || !box.height) return false
    const pr = panel.getBoundingClientRect()
    document.body.classList.toggle('ai-peek', document.body.classList.contains('ai-open') && box.right > pr.left && box.left < pr.right && box.bottom > pr.top && box.top < pr.bottom)
    const first = !ring.classList.contains('on')
    if (first) ring.classList.add('jump')
    glowBox(ring, anchor, box)
    ringTag.textContent = t.label
    ring.classList.toggle('below', box.top < 40)
    ring.classList.toggle('flip', box.left + 160 > window.innerWidth)
    ring.classList.add('on')
    if (first) {
      void ring.offsetWidth
      ring.classList.remove('jump')
    }
    clearTimeout(ringTimer)
    ringTimer = setTimeout(hideRing, 7000)
    return true
  }

  function parseReply(text) {
    const points = []
    const clean = text.replace(/\[\[point:([a-z0-9-]+)\]\]/gi, (_m, id) => {
      if (byId[id] && !points.includes(id) && points.length < 2) points.push(id)
      return ''
    }).replace(/[ \t]+\n/g, '\n').trim()
    return { clean, points }
  }

  function readLog() {
    const log = el('log')
    const t = log ? log.innerText : ''
    return t.length > TEXT_LIMIT ? t.slice(t.length - TEXT_LIMIT) : t
  }

  function currentWhere() {
    const tab = (el('wrap') || {}).dataset ? el('wrap').dataset.tab : ''
    const sub = tab === 'settings' ? document.querySelector('.sub-nav.active') : null
    return sub ? tab + ' (' + sub.dataset.sub + ' page)' : tab
  }

  async function send() {
    if (busy) return
    const text = input.value.trim()
    if (!text && !ctx.selection && !ctx.region && !ctx.log) return
    if (!(await refreshKey())) {
      renderSetup()
      return
    }
    const question = text || 'Explain this.'
    const extras = []
    if (ctx.selection) extras.push('selected text')
    if (ctx.region) extras.push('picked area')
    if (ctx.log) extras.push('output')
    history.push({ role: 'user', content: question, shown: question, tag: extras.length ? 'with ' + extras.join(', ') : '' })
    const payload = {
      messages: history.map(m => ({ role: m.role, content: m.content })),
      tab: currentWhere(),
      selection: ctx.selection,
      region: ctx.region,
      log: ctx.log ? readLog() : '',
      targets: targets.map(t => ({ id: t.id, label: t.label, desc: t.desc }))
    }
    ctx.selection = ''
    ctx.region = ''
    ctx.log = false
    input.value = ''
    input.style.height = ''
    renderChips()
    if (history.length === 1) msgs.innerHTML = ''
    say('user', question, null, history[history.length - 1].tag)
    busy = true
    el('aiSend').disabled = true
    const wait1 = thinking()
    const res = await window.api.aiChat(payload)
    wait1.remove()
    busy = false
    el('aiSend').disabled = false
    if (!res.ok) {
      const failed = history.pop()
      say('err', res.error || 'The helper could not answer.')
      if (failed && !input.value) {
        input.value = failed.content
        input.dispatchEvent(new Event('input'))
      }
      return
    }
    const { clean, points } = parseReply(res.text)
    history.push({ role: 'assistant', content: res.text, shown: clean, points })
    say('bot', clean || '...', points)
    if (points.length) pointAt(points[0])
  }

  function textOf(node) {
    if (node.value && (node.tagName === 'TEXTAREA' || node.tagName === 'INPUT')) return String(node.value)
    return String(node.innerText || node.textContent || '')
  }

  function describe(node) {
    const bits = [node.tagName.toLowerCase()]
    if (node.id) bits.push('#' + node.id)
    const t = node.getAttribute && (node.getAttribute('title') || node.getAttribute('placeholder'))
    if (t) bits.push('"' + t + '"')
    return bits.join(' ')
  }

  const PICKABLE = '.tab, .sub-nav, .fb-item, button, .btn, .check-row, .tgl, select, [role="button"]'

  function pickTarget(n) {
    const t = n && n.closest ? n.closest(PICKABLE) : null
    if (!t || t.closest('#aiPanel') || fab.contains(t)) return null
    const r = t.getBoundingClientRect()
    return r.width < 4 || r.height < 4 ? null : t
  }

  function pickInfo(n) {
    const kind = n.classList.contains('fb-item') ? 'file' : n.classList.contains('tab') || n.classList.contains('sub-nav') ? 'tab' : n.classList.contains('check-row') || n.classList.contains('tgl') ? 'option' : 'button'
    const t = targets.find(x => {
      try {
        return n.matches(x.sel) || !!n.querySelector(x.sel)
      } catch (e) {
        return false
      }
    })
    const text = String(n.innerText || n.getAttribute('title') || '').trim().replace(/\s+/g, ' ').slice(0, 120)
    const label = t ? t.label : text || n.id || n.tagName.toLowerCase()
    return { kind, label, desc: t ? t.desc : '', text }
  }

  function startPick() {
    const wasOpen = document.body.classList.contains('ai-open')
    document.body.classList.add('ai-picking')
    if (wasOpen) document.body.classList.add('ai-pick-hide')
    pickBar.textContent = 'Click a tab, button or file to ask about it. Esc to cancel.'
    let cur = null
    const paint = instant => {
      if (!cur) {
        pickBox.classList.remove('on')
        return
      }
      const r = cur.getBoundingClientRect()
      const jump = instant || !pickBox.classList.contains('on')
      if (jump) pickBox.classList.add('jump')
      glowBox(pickBox, cur, r)
      const info = pickInfo(cur)
      pickBox.dataset.name = info.kind + ': ' + info.label.slice(0, 40)
      pickBox.classList.toggle('below', r.top < 28)
      pickBox.classList.toggle('flip', r.left + 220 > window.innerWidth)
      pickBox.classList.add('on')
      if (jump) {
        void pickBox.offsetWidth
        pickBox.classList.remove('jump')
      }
    }
    const over = e => {
      const t = pickTarget(e.target)
      if (t !== cur) {
        cur = t
        paint()
      }
    }
    const rescroll = () => paint(true)
    const stop = () => {
      document.body.classList.remove('ai-picking', 'ai-pick-hide')
      pickBox.classList.remove('on')
      document.removeEventListener('mousemove', over, true)
      document.removeEventListener('click', pick, true)
      document.removeEventListener('mousedown', swallow, true)
      document.removeEventListener('keydown', key, true)
      document.removeEventListener('scroll', rescroll, true)
    }
    const swallow = e => {
      e.preventDefault()
      e.stopPropagation()
    }
    const pick = e => {
      e.preventDefault()
      e.stopPropagation()
      if (!cur) return
      const info = pickInfo(cur)
      const where = currentWhere()
      ctx.region = info.kind + ': ' + info.label + '\n' +
        (info.desc ? 'What it does: ' + info.desc + '\n' : '') +
        (info.text && info.text !== info.label ? 'Text: ' + info.text + '\n' : '') +
        (where ? 'Shown on the ' + where + ' tab' : '')
      stop()
      renderChips()
      input.focus()
    }
    const key = e => {
      if (e.key === 'Escape') {
        e.preventDefault()
        e.stopPropagation()
        stop()
      }
    }
    document.addEventListener('mousemove', over, true)
    document.addEventListener('click', pick, true)
    document.addEventListener('mousedown', swallow, true)
    document.addEventListener('keydown', key, true)
    document.addEventListener('scroll', rescroll, true)
  }

  function enableDrag(handle, node, opts) {
    let st = null
    handle.addEventListener('pointerdown', e => {
      if (e.button !== 0 || (opts.skip && e.target.closest(opts.skip))) return
      const r = node.getBoundingClientRect()
      st = { x: e.clientX, y: e.clientY, left: r.left, top: r.top, w: r.width, h: r.height, moved: false, id: e.pointerId }
      try {
        handle.setPointerCapture(e.pointerId)
      } catch (err) {}
    })
    handle.addEventListener('pointermove', e => {
      if (!st) return
      const dx = e.clientX - st.x
      const dy = e.clientY - st.y
      if (!st.moved) {
        if (Math.hypot(dx, dy) < 6) return
        st.moved = true
        node.classList.add('ai-dragging')
        node.style.right = 'auto'
        node.style.bottom = 'auto'
        snap.style.right = 'auto'
        snap.style.borderRadius = node === fab ? '22px' : '20px'
        snap.style.width = st.w + 'px'
        snap.style.height = st.h + 'px'
      }
      const left = Math.min(Math.max(0, st.left + dx), window.innerWidth - st.w)
      const top = Math.min(Math.max(0, st.top + dy), window.innerHeight - st.h)
      node.style.left = left + 'px'
      node.style.top = top + 'px'
      const tg = targetFor(node, left, top, st.w, st.h)
      const tt = fabTopOf(tg.fy)
      place(snap, tg.side, node === fab ? tt : panelTopFor(tt))
      snap.className = 'jump on'
    })
    const end = () => {
      if (!st) return
      const s0 = st
      st = null
      try {
        handle.releasePointerCapture(s0.id)
      } catch (err) {}
      if (!s0.moved) {
        if (opts.onTap) opts.onTap()
        return
      }
      const from = node.getBoundingClientRect()
      pos = targetFor(node, from.left, from.top, from.width, from.height)
      snap.className = ''
      node.classList.remove('ai-dragging')
      savePos()
      applyPos()
      const to = node.getBoundingClientRect()
      node.style.transition = 'none'
      node.style.transform = 'translate(' + (from.left - to.left) + 'px,' + (from.top - to.top) + 'px)'
      void node.offsetWidth
      node.style.transition = 'transform .3s cubic-bezier(.2,.9,.3,1)'
      node.style.transform = ''
      setTimeout(() => { node.style.transition = '' }, 340)
    }
    handle.addEventListener('pointerup', end)
    handle.addEventListener('pointercancel', end)
  }

  enableDrag(fab, fab, { onTap: openPanel })
  enableDrag(el('aiHead'), panel, { skip: '.ai-ib' })
  window.addEventListener('resize', applyPos)

  el('aiClose').onclick = closePanel
  el('aiClear').onclick = () => {
    history.length = 0
    ctx.selection = ''
    ctx.region = ''
    ctx.log = false
    renderChips()
    renderSetup()
  }
  el('aiSend').onclick = send
  el('aiPickBtn').onclick = startPick
  el('aiLogBtn').onclick = () => { ctx.log = true; renderChips() }
  el('aiPasteBtn').onclick = async () => {
    const t = await window.api.readClipboard()
    if (t) {
      input.value += (input.value ? '\n' : '') + t
      input.dispatchEvent(new Event('input'))
      input.focus()
    }
  }
  input.addEventListener('input', () => {
    input.style.height = 'auto'
    input.style.height = Math.min(input.scrollHeight, 120) + 'px'
  })
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      e.preventDefault()
      send()
    }
  })

  document.addEventListener('selectionchange', () => {
    const s = window.getSelection()
    const t = s ? s.toString().trim() : ''
    if (!t) {
      if (!panel.contains(document.activeElement)) lastSel = ''
      return
    }
    const a = s.anchorNode && (s.anchorNode.nodeType === 1 ? s.anchorNode : s.anchorNode.parentElement)
    if (a && panel.contains(a)) return
    lastSel = t.slice(0, TEXT_LIMIT)
    if (document.body.classList.contains('ai-open')) {
      ctx.selection = lastSel
      renderChips()
    }
  })

  document.addEventListener('pointerdown', e => {
    if (ring.classList.contains('on') && !(e.target.closest && e.target.closest('#aiPanel .ai-pt'))) hideRing()
  }, true)
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') {
      hideRing()
      if (!document.body.classList.contains('ai-picking')) closePanel()
    }
    if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'h' && !document.body.classList.contains('ai-off')) {
      e.preventDefault()
      document.body.classList.contains('ai-open') ? closePanel() : openPanel()
    }
  })

  const saveKeyBtn = el('aiKeySave')
  const clearKeyBtn = el('aiKeyClear')
  const keyBox = el('setAiKey')
  const keyNote = el('aiKeyNote')
  const selProv = el('setAiProvider')

  let shownSelLogo = ''
  window.aiPaintProvider = s => {
    if (!selProv || !s || !s.providers || !s.providers.length) return
    if (selProv.options.length !== s.providers.length) {
      selProv.innerHTML = ''
      s.providers.forEach(p => {
        const o = document.createElement('option')
        o.value = p.id
        selProv.appendChild(o)
      })
    }
    Array.from(selProv.options).forEach((o, i) => {
      const p = s.providers[i]
      o.textContent = p.name + (p.hasKey ? '  (key saved)' : '')
    })
    selProv.value = s.provider
    if (shownSelLogo !== s.provider) {
      shownSelLogo = s.provider
      el('aiSelLogo').replaceChildren(logoNode(s.provider))
      el('aiKeyLogo').replaceChildren(logoNode(s.provider))
    }
    const p = s.providers.find(x => x.id === s.provider) || s.providers[0]
    el('aiKeyTitle').textContent = p.id === 'custom' ? 'API Key' : p.short + ' API Key'
    keyBox.placeholder = p.hasKey ? 'Key saved (hidden)' : p.hint
    keyNote.textContent = !p.hasKey && p.site ? 'Get one at ' + p.site + '.' : ''
    el('setAiModel').placeholder = p.chat || 'model name'
    el('setAiFixModel').placeholder = p.fix || 'model name'
    el('setAiModel').value = s.model || ''
    el('setAiFixModel').value = s.fixModel || ''
    el('rowAiUrl').style.display = p.id === 'custom' ? '' : 'none'
    el('setAiUrl').value = s.customUrl || ''
  }

  async function afterKeyChange(msg) {
    settings = await window.api.getSettings()
    await refreshKey()
    keyBox.value = ''
    keyNote.textContent = msg
    if (document.body.classList.contains('ai-open')) renderSetup()
  }

  if (saveKeyBtn) {
    saveKeyBtn.onclick = async () => {
      const v = keyBox.value.trim()
      if (!v) return
      await window.api.aiSetKey(v, selProv.value)
      await afterKeyChange('Saved.')
    }
    keyBox.addEventListener('keydown', e => {
      if (e.key === 'Enter') saveKeyBtn.click()
    })
    clearKeyBtn.onclick = async () => {
      await window.api.aiSetKey('', selProv.value)
      await afterKeyChange('Removed.')
    }
    selProv.onchange = async () => {
      settings = await window.api.setSettings({ aiProvider: selProv.value })
      await refreshKey()
    }
    const saveModels = async () => {
      const cur = (settings && settings.aiModels) || {}
      const next = Object.assign({}, cur)
      next[selProv.value] = { chat: el('setAiModel').value.trim(), fix: el('setAiFixModel').value.trim() }
      settings = await window.api.setSettings({ aiModels: next })
    }
    el('setAiModel').onchange = saveModels
    el('setAiFixModel').onchange = saveModels
    el('setAiUrl').onchange = async () => {
      settings = await window.api.setSettings({ aiCustomUrl: el('setAiUrl').value.trim() })
      await refreshKey()
    }
  }

  let cfg = {}
  let wasOn = false

  function applyExperimental() {
    const exp = !!cfg.showExperimental
    const on = exp && !!cfg.aiHelperOn
    const nav = el('navExp')
    if (nav) nav.style.display = exp ? '' : 'none'
    const pane = el('sub-experimental')
    if (!exp && pane && pane.classList.contains('active') && window.showSub) window.showSub('application')
    document.body.classList.toggle('ai-off', !on)
    if (!on) {
      closePanel()
      hideRing()
    }
    const opt = el('optAi')
    if (opt) {
      const label = opt.closest('label')
      if (label) label.style.display = on ? '' : 'none'
      if (!on) opt.checked = false
      else if (!wasOn && hasKey) opt.checked = true
    }
    wasOn = on
  }

  async function saveCfg(patch) {
    cfg = await window.api.setSettings(patch)
    settings = cfg
    applyExperimental()
    syncToggleLabels()
  }

  el('setExperimental').onchange = () => saveCfg({ showExperimental: el('setExperimental').checked })
  el('setAiHelper').onchange = () => saveCfg({ aiHelperOn: el('setAiHelper').checked })

  window.aiFillSettings = () => {
    cfg = settings
    el('setExperimental').checked = !!settings.showExperimental
    el('setAiHelper').checked = !!settings.aiHelperOn
    applyExperimental()
    refreshKey()
  }

  refreshKey().then(() => window.api.getSettings()).then(c => {
    cfg = c
    el('setExperimental').checked = !!c.showExperimental
    el('setAiHelper').checked = !!c.aiHelperOn
    applyExperimental()
    syncToggleLabels()
  })
})()
