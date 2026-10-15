/* Builds a node graph (nodes, pins, wires, comment boxes) from the editor-only data of a material or material function. */
(function (root) {
  'use strict'

  const HEADER_COLORS = [
    ['Vector', '#877020'],
    ['Scalar', '#547a2f'],
    ['TextureCoordinate', '#8c1313'],
    ['Texture', '#136384'],
    ['Bool', '#561a1a'],
    ['Switch', '#561a1a'],
    ['MaterialFunction', '#466e86'],
    ['Composite', '#272827']
  ]
  const DEFAULT_HEADER = '#60815c'
  const IGNORED = new Set(['MaterialExpressionEditorX', 'MaterialExpressionEditorY', 'Material', 'Function', 'Declaration', 'InputExpressions', 'OutputExpressions', 'Input', 'Output'])
  const PIN_COLORS = { R: '#ff0000', G: '#008000', B: '#0000ff', A: '#a9a9a9' }
  const NONE = new Set(['None', '', null, undefined])

  // measured from Fortnite Porting (nodify): 2px border outside the content, 25px header, 18px rows
  const H = { header: 25, top: 27, row: 18, pad: 2, footer: 16, min: 0 }

  const typeOf = x => String((x && x.Type) || '').replace(/^MaterialExpression/, '')
  const inner = s => {
    s = String(s || '')
    const i = s.indexOf("'")
    if (i < 0) return s
    const j = s.lastIndexOf("'")
    return s.slice(i + 1, j > i ? j : undefined)
  }
  const shortName = s => inner(s).split(':').pop().split('.').pop()
  const spaced = s => String(s).replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/_/g, ' ').trim()
  const isInput = v => !!v && typeof v === 'object' && !Array.isArray(v) && Object.prototype.hasOwnProperty.call(v, 'ExpressionName')
  const connected = v => isInput(v) && !NONE.has(v.ExpressionName)
  const num = (v, d) => (typeof v === 'number' && isFinite(v) ? v : d)

  function toSrgbHex(c, srgb) {
    const ch = k => {
      let v = Math.max(0, Math.min(1, num(c && c[k], 0)))
      if (srgb) v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.pow(v, 1 / 2.4) - 0.055
      return Math.round(Math.max(0, Math.min(1, v)) * 255)
    }
    return [ch('R'), ch('G'), ch('B')]
  }
  const hex = rgb => '#' + rgb.map(v => v.toString(16).padStart(2, '0')).join('')

  function build(exports, opts) {
    opts = opts || {}
    const all = Array.isArray(exports) ? exports : []
    const warnings = []
    const exprs = all.filter(x => x && typeof x === 'object' && /^MaterialExpression/.test(String(x.Type || '')))

    // a name used twice (a collapsed subgraph keeps its own numbering) is told apart by its outer object
    const seen = new Set()
    const dups = new Set()
    exprs.forEach(x => { (seen.has(x.Name) ? dups : seen).add(x.Name) })
    const outerOf = x => inner((x.Outer && x.Outer.ObjectName) || '')
    const keyOf = x => {
      if (!dups.has(x.Name)) return x.Name
      const outer = outerOf(x)
      return outer + (outer.indexOf(':') >= 0 ? '.' : ':') + x.Name
    }
    const keyOfRef = ref => {
      const n = ref.ExpressionName
      if (dups.has(n)) return inner((ref.Expression && ref.Expression.ObjectName) || '') || n
      return n
    }

    const byKey = new Map()
    const byName = new Map()
    const rawByKey = new Map()
    exprs.forEach(x => {
      rawByKey.set(keyOf(x), x)
      if (!byName.has(x.Name)) byName.set(x.Name, x)
    })

    const nodes = new Map()
    const order = []
    const comments = []
    const pend = []

    const addPin = (node, list, name, color) => { list.push({ name: name == null ? '' : String(name), color: color || PIN_COLORS[name] || '' }); return list.length - 1 }
    const addIn = (node, name) => node.inputs.push({ name: name == null ? '' : String(name) })
    const wire = (node, v, name) => {
      if (!connected(v)) return
      const fk = keyOfRef(v)
      if (!rawByKey.has(fk)) return
      const ti = addIn(node, name) - 1
      pend.push({ to: node, ti, from: fk, out: Math.max(0, num(v.OutputIndex, 0) | 0) })
    }
    const colorOutputs = (node, rgba) => {
      ;['RGB', 'R', 'G', 'B', 'A'].forEach(n => addPin(node, node.outputs, n))
      if (rgba) addPin(node, node.outputs, 'RGBA')
    }
    const nameOf = v => {
      if (v && typeof v === 'object') return String(v.Text || v.Name || '')
      return v == null ? '' : String(v)
    }

    exprs.forEach(x => {
      if (x.Type === 'MaterialExpressionComment') {
        const p = x.Properties || {}
        const c = p.CommentColor
        comments.push({
          key: keyOf(x),
          text: String(p.Text || ''),
          x: num(p.MaterialExpressionEditorX, 0),
          y: num(p.MaterialExpressionEditorY, 0),
          w: num(p.SizeX, 0),
          h: num(p.SizeY, 0),
          rgb: c && typeof c === 'object' ? toSrgbHex(c, false) : null
        })
        return
      }
      try {
        const n = makeNode(x)
        nodes.set(n.key, n)
        order.push(n)
      } catch (e) {
        warnings.push('skipped ' + x.Name + ': ' + (e && e.message))
      }
    })

    function makeNode(x) {
      const p = x.Properties || {}
      const t = typeOf(x)
      const n = {
        key: keyOf(x),
        name: x.Name,
        type: x.Type,
        t,
        x: num(p.MaterialExpressionEditorX, 0),
        y: num(p.MaterialExpressionEditorY, 0),
        label: null,
        color: null,
        inputs: [],
        outputs: [],
        content: null,
        footer: p.Desc ? String(p.Desc) : '',
        props: [],
        owner: null,
        composite: null,
        w: 0,
        h: 0
      }
      const handled = new Set()
      const take = (...keys) => keys.forEach(k => handled.add(k))

      switch (t) {
        case 'MaterialFunctionCall': {
          const mf = p.MaterialFunction || p.Function
          n.label = mf && typeof mf === 'object' ? (shortName(mf.ObjectName) || 'Material Function') : 'Material Function'
          if (mf && typeof mf === 'object' && mf.ObjectPath) n.package = String(mf.ObjectPath)
          ;(Array.isArray(p.FunctionInputs) ? p.FunctionInputs : []).forEach(fi => {
            const inp = fi && fi.Input
            wire(n, inp, inp && inp.InputName !== undefined ? nameOf(inp.InputName) : '')
          })
          ;(Array.isArray(p.FunctionOutputs) ? p.FunctionOutputs : []).forEach(fo => {
            const out = fo && (fo.Output || fo)
            addPin(n, n.outputs, nameOf(out && out.OutputName))
          })
          take('FunctionInputs', 'FunctionOutputs', 'MaterialFunction', 'Function')
          break
        }
        case 'Composite': {
          n.label = nameOf(p.SubgraphName) || 'Subgraph'
          n.composite = { input: p.InputExpressions || null, output: p.OutputExpressions || null }
          take('InputExpressions', 'OutputExpressions')
          break
        }
        case 'PinBase': {
          const dir = nameOf(p.PinDirection)
          if (dir === 'EEdGraphPinDirection::EGPD_Output') {
            n.label = 'Input'
            ;(Array.isArray(p.Outputs) ? p.Outputs : []).forEach(o => addPin(n, n.outputs, nameOf(o && o.OutputName)))
          } else {
            n.label = 'Output'
            ;(Array.isArray(p.ReroutePins) ? p.ReroutePins : []).forEach(r => addIn(n, nameOf(r && r.Name) || 'Pin'))
          }
          take('Outputs', 'ReroutePins', 'PinDirection')
          break
        }
        case 'FunctionInput':
          n.label = nameOf(p.InputName) || 'Input'
          n.color = '#7f0000'
          break
        case 'FunctionOutput':
          n.label = nameOf(p.OutputName) || 'Output'
          n.color = '#7f0000'
          break
        case 'DynamicParameter':
        case 'GetMaterialAttributes':
          ;(Array.isArray(p.Outputs) ? p.Outputs : []).forEach(o => addPin(n, n.outputs, nameOf(o && o.OutputName)))
          take('Outputs')
          break
        case 'SetMaterialAttributes':
          ;(Array.isArray(p.Inputs) ? p.Inputs : []).forEach(i => wire(n, i, NONE.has(i && i.InputName) ? 'MaterialAttributes' : nameOf(i.InputName)))
          take('Inputs')
          break
        case 'NamedRerouteDeclaration':
        case 'NamedRerouteUsage': {
          let decl = p
          if (t === 'NamedRerouteUsage' && p.Declaration && typeof p.Declaration === 'object') {
            const d = byName.get(shortName(p.Declaration.ObjectName))
            if (d && d.Properties) decl = d.Properties
          }
          n.label = nameOf(decl.Name) || 'Reroute'
          const c = decl.NodeColor
          if (c && typeof c === 'object') {
            const scaled = { R: num(c.R, 0) * 0.25, G: num(c.G, 0) * 0.25, B: num(c.B, 0) * 0.25 }
            n.color = hex(toSrgbHex(scaled, true))
          }
          take('Declaration', 'Name', 'NodeColor')
          break
        }
        case 'TextureSampleParameter2D':
        case 'TextureSample':
        case 'TextureObjectParameter': {
          colorOutputs(n, true)
          n.label = nameOf(p.ParameterName) || 'Texture'
          const tex = p.Texture
          if (tex && typeof tex === 'object') {
            if (n.label === 'Texture') n.label = shortName(tex.ObjectName) || n.label
            if (tex.ObjectPath) n.content = { kind: 'tex', path: String(tex.ObjectPath), name: shortName(tex.ObjectName) }
          }
          break
        }
        case 'RuntimeVirtualTextureSample':
          ;['BaseColor', 'Specular', 'Roughness', 'Normal', 'WorldHeight', 'Mask'].forEach(s => addPin(n, n.outputs, s))
          break
        case 'Constant2Vector':
        case 'Constant3Vector':
        case 'Constant4Vector':
        case 'VectorParameter': {
          let c
          if (t === 'VectorParameter') c = p.DefaultValue
          else if (t === 'Constant2Vector') c = { R: p.R, G: p.G, B: 0 }
          else c = p.Constant
          if (t === 'VectorParameter') n.label = nameOf(p.ParameterName) || x.Name
          colorOutputs(n, false)
          const rgb = toSrgbHex(c || {}, true)
          n.content = { kind: 'color', hex: hex(rgb), r: num(c && c.R, 0), g: num(c && c.G, 0), b: num(c && c.B, 0), a: t === 'Constant4Vector' || t === 'VectorParameter' ? num(c && c.A, 1) : 1 }
          break
        }
        case 'ParticleColor':
        case 'CurveAtlasRowParameter':
        case 'VertexColor':
          colorOutputs(n, false)
          break
        case 'ComponentMask': {
          const on = ['R', 'G', 'B', 'A'].filter(k => p[k])
          n.label = 'Mask ( ' + on.join(' ') + ' )'
          break
        }
        case 'ScalarParameter': {
          n.label = nameOf(p.ParameterName) || x.Name
          n.content = { kind: 'num', v: num(p.DefaultValue, 0) }
          break
        }
        case 'Constant':
          n.content = { kind: 'num', v: num(p.R, 0) }
          break
        case 'Custom': {
          ;(Array.isArray(p.Inputs) ? p.Inputs : []).forEach(i => wire(n, i && i.Input, nameOf(i && i.InputName) || 'Input'))
          n.content = { kind: 'code', text: String(p.Code || '') }
          take('Inputs', 'Code')
          break
        }
        case 'StaticSwitchParameter':
        case 'ChannelMaskParameter':
          n.label = nameOf(p.ParameterName) || x.Name
          break
        case 'StaticBoolParameter':
          n.label = nameOf(p.ParameterName) || x.Name
          n.content = { kind: 'bool', v: !!p.DefaultValue }
          break
        case 'StaticComponentMaskParameter':
          n.label = nameOf(p.ParameterName) || x.Name
          n.content = { kind: 'mask', r: !!p.DefaultR, g: !!p.DefaultG, b: !!p.DefaultB, a: !!p.DefaultA }
          break
        case 'Switch': {
          ;(Array.isArray(p.Inputs) ? p.Inputs : []).forEach((i, idx) => {
            let nm = nameOf(i && i.InputName)
            if (!nm || nm.toLowerCase() === 'none') nm = String(idx)
            wire(n, i && i.Input, nm)
          })
          take('Inputs')
          break
        }
        case 'Convert': {
          n.label = nameOf(p.NodeName) || x.Name
          ;(Array.isArray(p.ConvertInputs) ? p.ConvertInputs : []).forEach(i => wire(n, i && i.ExpressionInput, nameOf(i && i.Name)))
          ;(Array.isArray(p.Outputs) ? p.Outputs : []).forEach((o, idx) => {
            let nm = nameOf(o && o.OutputName)
            if (!nm || nm.toLowerCase() === 'none') nm = String(idx)
            addPin(n, n.outputs, nm)
          })
          take('ConvertInputs', 'Outputs')
          break
        }
        case 'PreSkinnedLocalBounds':
          ;['Half Extents', 'Extents', 'Min', 'Max'].forEach(s => addPin(n, n.outputs, s))
          break
        default:
          break
      }

      // every other expression input on the node becomes a pin
      Object.keys(p).forEach(k => {
        if (handled.has(k)) return
        const v = p[k]
        if (isInput(v)) {
          wire(n, v, k === 'Input' ? '' : k)
        } else if (Array.isArray(v) && v.length && v.every(isInput)) {
          v.forEach((e, i) => wire(n, e, k + '[' + i + ']'))
        }
      })

      // properties for the side panel
      Object.keys(p).forEach(k => {
        if (IGNORED.has(k)) return
        const v = p[k]
        if (isInput(v)) return
        if (Array.isArray(v) && v.length && v.every(isInput)) return
        n.props.push([k, v])
      })

      if (!n.outputs.length && Array.isArray(p.Outputs) && p.Outputs.length > 1 && !handled.has('Outputs')) {
        p.Outputs.forEach(o => addPin(n, n.outputs, nameOf(o && o.OutputName)))
      }
      if (!n.label) n.label = spaced(String(x.Name).replace(/^MaterialExpression/, '').split('_')[0])
      if (!n.color) {
        const hit = HEADER_COLORS.find(([k]) => String(x.Type).toLowerCase().indexOf(k.toLowerCase()) >= 0)
        n.color = hit ? hit[1] : DEFAULT_HEADER
      }
      if (!n.outputs.length) addPin(n, n.outputs, '')
      return n
    }

    // wires
    const conns = []
    pend.forEach(c => {
      const from = nodes.get(c.from)
      if (!from) return
      conns.push({ from, fi: Math.min(c.out, from.outputs.length - 1), to: c.to, ti: c.ti })
    })

    // collapsed subgraphs: the nodes that feed a composite's output pin belong to it
    const into = new Map()
    conns.forEach(c => { if (!into.has(c.to)) into.set(c.to, []); into.get(c.to).push(c.from) })
    const composites = order.filter(n => n.composite)
    const sets = composites.map(cn => {
      const set = new Set()
      const out = cn.composite.output
      const start = out && typeof out === 'object' ? byName.get(shortName(out.ObjectName)) : null
      const sn = start ? nodes.get(keyOf(start)) : null
      const stack = sn ? [sn] : []
      while (stack.length) {
        const cur = stack.pop()
        if (set.has(cur)) continue
        set.add(cur)
        ;(into.get(cur) || []).forEach(f => stack.push(f))
      }
      const pins = (ref, dst, mk) => {
        const e = ref && typeof ref === 'object' ? byName.get(shortName(ref.ObjectName)) : null
        const pr = e && e.Properties && Array.isArray(e.Properties.ReroutePins) ? e.Properties.ReroutePins : []
        pr.forEach(r => mk(dst, nameOf(r && r.Name) || 'Pin'))
      }
      cn.inputs = []
      cn.outputs = []
      pins(cn.composite.input, cn.inputs, (d, nm) => d.push({ name: nm }))
      pins(cn.composite.output, cn.outputs, (d, nm) => d.push({ name: nm, color: '' }))
      if (!cn.outputs.length) cn.outputs.push({ name: '', color: '' })
      return { cn, set }
    })
    sets.sort((a, b) => a.set.size - b.set.size)
    sets.forEach(({ cn, set }) => {
      cn.subCount = set.size
      set.forEach(m => { if (!m.owner && m !== cn) m.owner = cn.key })
    })

    // the material's own inputs (base colour ...) sit on a node of their own
    let rootNode = null
    const editor = all.find(x => x && /EditorOnlyData$/.test(String(x.Type || '')))
    const isFunction = !!(editor && /^MaterialFunction/.test(String(editor.Type)))
    if (editor && !isFunction && editor.Properties) {
      rootNode = { key: '__material', name: opts.name || 'Material', type: 'Material', t: 'Material', label: opts.name || 'Material', color: '#786859', x: 0, y: 0, inputs: [], outputs: [], content: null, footer: '', props: [], owner: null, composite: null, isRoot: true, w: 0, h: 0 }
      const ep = editor.Properties
      const pairs = connected(ep.MaterialAttributes)
        ? [['MaterialAttributes', ep.MaterialAttributes]]
        : Object.keys(ep).filter(k => isInput(ep[k])).map(k => [k, ep[k]])
      pairs.forEach(([k, v]) => {
        const fk = keyOfRef(v)
        const from = connected(v) ? nodes.get(fk) : null
        if (!from) return
        const ti = rootNode.inputs.push({ name: k }) - 1
        conns.push({ from, fi: Math.min(Math.max(0, num(v.OutputIndex, 0) | 0), from.outputs.length - 1), to: rootNode, ti })
      })
      if (!rootNode.inputs.length) rootNode = null
    }
    if (rootNode) { nodes.set(rootNode.key, rootNode); order.push(rootNode) }

    return { nodes, order, comments, conns, root: rootNode, isFunction, warnings, name: opts.name || '' }
  }

  // Sizes every node like Fortnite Porting's Nodify nodes: sockets left and right, content between them
  const CONTENT = { tex: [128, 128], color: [100, 22], num: [80, 22], bool: [80, 22], mask: [120, 22], code: [220, 84], speaker: [128, 128] }
  function sizeNode(n, mh, mp) {
    const rows = Math.max(n.inputs.length, n.outputs.length)
    let a = 0, b = 0
    n.inputs.forEach(p => { a = Math.max(a, mp(p.name)) })
    n.outputs.forEach(p => { b = Math.max(b, mp(p.name)) })
    const hasIn = n.inputs.length > 0
    const hasOut = n.outputs.length > 0
    const cd = n.content && CONTENT[n.content.kind]
    const speaker = !!(n.content && n.content.kind === 'speaker')
    const hdr = mh(n.label) + 20 + (n.composite || n.package ? 76 : 0)
    let w, body
    if (cd) {
      const lft = hasIn ? 26.5 + a + 28 : 8.5
      const rgt = hasOut ? 28 + b + 26.5 : 8.5
      w = Math.max(hdr, lft + cd[0] + rgt)
      n.cw = cd[0]
      n.cx = Math.round(lft + (w - lft - rgt - cd[0]) / 2)
      body = Math.max(rows * H.row, cd[1] + (speaker ? 0 : 16))
    } else {
      w = Math.max(hdr, (hasIn ? 26.5 + a : 8.5) + 34 + (hasOut ? b + 26.5 : 8.5))
      body = rows * H.row + 2
    }
    n.contentH = cd ? cd[1] : 0
    n.rows = rows
    n.body = body
    n.w = Math.ceil(w)
    n.h = 4 + H.header + body + (n.footer ? H.footer + H.pad : 0)
  }
  function layout(graph, measure, measurePort) {
    const mh = measure || (s => String(s).length * 7.6)
    const mp = measurePort || (s => String(s).length * 6.6)
    graph.order.forEach(n => sizeNode(n, mh, mp))
    const r = graph.root
    if (r) {
      const others = graph.order.filter(n => n !== r && !n.owner)
      let right = -Infinity
      let sum = 0
      others.forEach(n => { right = Math.max(right, n.x + n.w); sum += n.y })
      r.x = (others.length ? right : 0) + 260
      const feeders = graph.conns.filter(c => c.to === r)
      r.y = feeders.length ? feeders.reduce((s, c) => s + c.from.y, 0) / feeders.length : others.length ? sum / others.length : 0
    }
  }

  // The nodes and wires of one level: the top level, or the inside of one composite node
  function view(graph, owner) {
    const o = owner || null
    const nodes = graph.order.filter(n => (n.owner || null) === o)
    const set = new Set(nodes)
    const conns = graph.conns.filter(c => set.has(c.from) && set.has(c.to))
    return { nodes, conns, comments: o ? [] : graph.comments }
  }

  function pinPos(n, side, i) {
    return { x: side === 'out' ? n.x + n.w - 12 : n.x + 12, y: n.y + H.top + i * H.row + H.row / 2 }
  }

  const api = { build, layout, sizeNode, view, pinPos, H, shortName, spaced }
  if (typeof module !== 'undefined' && module.exports) module.exports = api
  else root.MaterialGraph = api
})(typeof window !== 'undefined' ? window : this)
