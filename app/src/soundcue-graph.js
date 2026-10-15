;(function (root) {
  const MG = root.MaterialGraph
  const H = MG.H
  const COLOR_NODE = '#748394'
  const COLOR_OUT = '#786859'
  const SKIP_PROPS = new Set(['ChildNodes', 'GraphNode', 'EditorPos', 'NodePosX', 'NodePosY'])
  const QUALITY_INPUTS = ['Default', 'Low', 'iOS', 'Android']

  function refName(ref) {
    if (!ref || typeof ref !== 'object') return ''
    const t = String(ref.ObjectName || '')
    const m = /'([^']+)'$/.exec(t)
    return m ? m[1] : t
  }

  function refIndex(ref) {
    if (!ref || typeof ref !== 'object') return -1
    const m = /\.(\d+)$/.exec(String(ref.ObjectPath || ''))
    return m ? parseInt(m[1], 10) : -1
  }

  function resolve(list, byName, ref) {
    if (!ref || typeof ref !== 'object') return null
    const i = refIndex(ref)
    const name = refName(ref)
    if (i >= 0 && i < list.length && list[i] && (!name || list[i].Name === name)) return list[i]
    if (name && byName.has(name)) return byName.get(name)
    if (i >= 0 && i < list.length) return list[i] || null
    return null
  }

  function waveName(props) {
    const p = props.SoundWaveAssetPtr || props.SoundWaveAsset || props.SoundWave
    let t = ''
    if (typeof p === 'string') t = p
    else if (p && typeof p === 'object') t = p.AssetPathName || p.ObjectPath || p.ObjectName || ''
    t = String(t)
    const q = /'([^']+)'$/.exec(t)
    if (q) t = q[1]
    t = t.replace(/\.\d+$/, '')
    const dot = t.lastIndexOf('.')
    if (dot > t.lastIndexOf('/')) t = t.slice(dot + 1)
    return t.slice(t.lastIndexOf('/') + 1)
  }

  function typeLabel(type) {
    return MG.spaced(String(type || '').replace(/^SoundNode/, '')) || 'Node'
  }

  function propsOf(ex) {
    const out = []
    const p = (ex && ex.Properties) || {}
    Object.keys(p).forEach(k => { if (!SKIP_PROPS.has(k)) out.push([k, p[k]]) })
    return out
  }

  function build(list, opts) {
    const byName = new Map()
    list.forEach(ex => { if (ex && ex.Name) byName.set(ex.Name, ex) })
    const cue = list.find(ex => ex && ex.Type === 'SoundCue') || null
    const nodes = new Map()
    const order = []
    const conns = []
    const warnings = []
    if (!cue) return { nodes, order, comments: [], conns, root: null, isFunction: false, warnings: ['no SoundCue export'], name: (opts && opts.name) || '' }

    const rootNode = {
      key: '__output', name: 'Output', type: 'Output', label: 'Output', listLabel: 'Output', sortKey: 'Output', color: COLOR_OUT, isRoot: true,
      inputs: [{ name: '' }], outputs: [], content: { kind: 'speaker' }, footer: '', props: [],
      owner: null, x: 0, y: 0, w: 0, h: 0, depth: 0, children: []
    }
    nodes.set(rootNode.key, rootNode)
    order.push(rootNode)

    const made = new Map()
    function make(ex, depth) {
      if (!ex) return null
      const key = ex.Name || ('n' + order.length)
      if (made.has(key)) return made.get(key)
      const isWave = ex.Type === 'SoundNodeWavePlayer'
      const props = (ex.Properties) || {}
      const kids = Array.isArray(props.ChildNodes) ? props.ChildNodes : []
      const label = isWave ? ('Wave Player' + (waveName(props) ? ' : ' + waveName(props) : '')) : typeLabel(ex.Type)
      const isQuality = ex.Type === 'SoundNodeQualityLevel'
      const inputs = kids.map((_, i) => ({ name: isQuality && QUALITY_INPUTS[i] ? QUALITY_INPUTS[i] : '' }))
      if (isQuality) QUALITY_INPUTS.forEach((q, i) => { if (!inputs[i]) inputs[i] = { name: q } })
      const n = {
        key, name: ex.Name || label, type: ex.Type || 'SoundNode', label, listLabel: isWave ? label : (ex.Name || label), sortKey: ex.Name || label, rawLen: (isWave ? label : (ex.Name || label)).length, color: COLOR_NODE, isRoot: false,
        inputs, outputs: [{ name: 'Output' }], content: null, footer: '', props: propsOf(ex),
        owner: null, x: 0, y: 0, w: 0, h: 0, depth, children: []
      }
      made.set(key, n)
      nodes.set(key, n)
      order.push(n)
      kids.forEach((ref, i) => {
        const child = make(resolve(list, byName, ref), depth + 1)
        if (child) {
          conns.push({ from: child, to: n, fi: 0, ti: i })
          n.children.push(child)
        }
      })
      return n
    }

    const first = make(resolve(list, byName, (cue.Properties || {}).FirstNode), 1)
    if (first) {
      conns.push({ from: first, to: rootNode, fi: 0, ti: 0 })
      rootNode.children.push(first)
    } else warnings.push('the sound cue has no first node')
    return { nodes, order, comments: [], conns, root: rootNode, isFunction: false, warnings, name: (opts && opts.name) || '' }
  }

  function layout(graph, measure, measurePort) {
    const mh = measure || (s => String(s).length * 7.6)
    const mp = measurePort || (s => String(s).length * 6.6)
    graph.order.forEach(n => MG.sizeNode(n, mh, mp))
    const SPACING_Y = 75
    const SPACING_X = 250
    const heights = new Map()
    function subtreeH(n) {
      if (heights.has(n)) return heights.get(n)
      const v = n.children.length ? Math.max(SPACING_Y, n.children.reduce((t, c) => t + subtreeH(c), 0)) : SPACING_Y
      heights.set(n, v)
      return v
    }
    function place(n, level, cy) {
      const len = n.rawLen || n.label.length
      n.x = -(level * SPACING_X + len * 14)
      n.y = cy
      if (!n.children.length) return
      const hs = n.children.map(subtreeH)
      let y = cy - hs.reduce((t, v) => t + v, 0) / 2
      n.children.forEach((c, i) => {
        place(c, level + 1, y + hs[i] / 2)
        y += hs[i]
      })
    }
    if (graph.root) {
      graph.root.rawLen = graph.root.label.length
      place(graph.root, 0, 0)
    }
  }

  root.SoundCueGraph = { build, layout }
})(typeof window !== 'undefined' ? window : this)
