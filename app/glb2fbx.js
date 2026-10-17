const fs = require('fs')
const path = require('path')

function parseGlb(buf) {
  if (buf.length < 20 || buf.readUInt32LE(0) !== 0x46546c67) throw new Error('not a glb file')
  let off = 12
  let json = null
  let bin = null
  while (off + 8 <= buf.length) {
    const len = buf.readUInt32LE(off)
    const type = buf.readUInt32LE(off + 4)
    const data = buf.subarray(off + 8, off + 8 + len)
    if (type === 0x4e4f534a) json = JSON.parse(data.toString('utf8'))
    else if (type === 0x004e4942 && !bin) bin = data
    off += 8 + len + ((4 - (len % 4)) % 4)
  }
  if (!json) throw new Error('glb has no json chunk')
  return { json, bin }
}

const COMPONENTS = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT2: 4, MAT3: 9, MAT4: 16 }
const BYTES = { 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4 }

function readAccessor(json, bin, index) {
  const acc = json.accessors[index]
  const n = COMPONENTS[acc.type]
  const out = new Float64Array(acc.count * n)
  if (acc.bufferView === undefined) return out
  const view = json.bufferViews[acc.bufferView]
  const size = BYTES[acc.componentType]
  const stride = view.byteStride || size * n
  const base = (view.byteOffset || 0) + (acc.byteOffset || 0)
  const dv = new DataView(bin.buffer, bin.byteOffset, bin.byteLength)
  for (let i = 0; i < acc.count; i++) {
    for (let c = 0; c < n; c++) {
      const p = base + i * stride + c * size
      let v
      switch (acc.componentType) {
        case 5120: v = dv.getInt8(p); if (acc.normalized) v = Math.max(v / 127, -1); break
        case 5121: v = dv.getUint8(p); if (acc.normalized) v /= 255; break
        case 5122: v = dv.getInt16(p, true); if (acc.normalized) v = Math.max(v / 32767, -1); break
        case 5123: v = dv.getUint16(p, true); if (acc.normalized) v /= 65535; break
        case 5125: v = dv.getUint32(p, true); break
        default: v = dv.getFloat32(p, true)
      }
      out[i * n + c] = v
    }
  }
  return out
}

function mul(a, b) {
  const r = new Array(16).fill(0)
  for (let c = 0; c < 4; c++) for (let rI = 0; rI < 4; rI++) {
    let s = 0
    for (let k = 0; k < 4; k++) s += a[k * 4 + rI] * b[c * 4 + k]
    r[c * 4 + rI] = s
  }
  return r
}

function localMatrix(node) {
  if (node.matrix) return node.matrix.slice()
  const t = node.translation || [0, 0, 0]
  const q = node.rotation || [0, 0, 0, 1]
  const s = node.scale || [1, 1, 1]
  const [x, y, z, w] = q
  const xx = x * x, yy = y * y, zz = z * z
  const xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z
  return [
    (1 - 2 * (yy + zz)) * s[0], (2 * (xy + wz)) * s[0], (2 * (xz - wy)) * s[0], 0,
    (2 * (xy - wz)) * s[1], (1 - 2 * (xx + zz)) * s[1], (2 * (yz + wx)) * s[1], 0,
    (2 * (xz + wy)) * s[2], (2 * (yz - wx)) * s[2], (1 - 2 * (xx + yy)) * s[2], 0,
    t[0], t[1], t[2], 1
  ]
}

function normalMatrix(m) {
  const a = m[0], b = m[4], c = m[8], d = m[1], e = m[5], f = m[9], g = m[2], h = m[6], i = m[10]
  const A = e * i - f * h, B = f * g - d * i, C = d * h - e * g
  const det = a * A + b * B + c * C
  if (Math.abs(det) < 1e-12) return [1, 0, 0, 0, 1, 0, 0, 0, 1]
  const inv = 1 / det
  const r00 = A * inv, r01 = (c * h - b * i) * inv, r02 = (b * f - c * e) * inv
  const r10 = B * inv, r11 = (a * i - c * g) * inv, r12 = (c * d - a * f) * inv
  const r20 = C * inv, r21 = (b * g - a * h) * inv, r22 = (a * e - b * d) * inv
  return [r00, r10, r20, r01, r11, r21, r02, r12, r22]
}

function collect(json, bin) {
  const nodes = json.nodes || []
  const sceneIndex = json.scene !== undefined ? json.scene : 0
  const roots = json.scenes && json.scenes[sceneIndex] ? json.scenes[sceneIndex].nodes || [] : nodes.map((_, i) => i)
  const pos = []
  const nor = []
  const uv = []
  const idx = []
  const slotOfTri = []
  let slotCount = 0
  let hasNormals = true
  let hasUv = true
  const visit = (ni, parent, depth) => {
    if (depth > 64) return
    const node = nodes[ni]
    const world = mul(parent, localMatrix(node))
    if (node.mesh !== undefined) {
      const nm = normalMatrix(world)
      for (const prim of json.meshes[node.mesh].primitives) {
        const mode = prim.mode === undefined ? 4 : prim.mode
        if (mode !== 4 || prim.attributes.POSITION === undefined) continue
        const p = readAccessor(json, bin, prim.attributes.POSITION)
        const count = p.length / 3
        const base = pos.length / 3
        for (let i = 0; i < count; i++) {
          const x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2]
          pos.push(world[0] * x + world[4] * y + world[8] * z + world[12], world[1] * x + world[5] * y + world[9] * z + world[13], world[2] * x + world[6] * y + world[10] * z + world[14])
        }
        if (prim.attributes.NORMAL !== undefined) {
          const n = readAccessor(json, bin, prim.attributes.NORMAL)
          for (let i = 0; i < count; i++) {
            const x = n[i * 3], y = n[i * 3 + 1], z = n[i * 3 + 2]
            let nx = nm[0] * x + nm[3] * y + nm[6] * z
            let ny = nm[1] * x + nm[4] * y + nm[7] * z
            let nz = nm[2] * x + nm[5] * y + nm[8] * z
            const l = Math.hypot(nx, ny, nz) || 1
            nor.push(nx / l, ny / l, nz / l)
          }
        } else {
          hasNormals = false
        }
        if (prim.attributes.TEXCOORD_0 !== undefined) {
          const t = readAccessor(json, bin, prim.attributes.TEXCOORD_0)
          for (let i = 0; i < count; i++) uv.push(t[i * 2], 1 - t[i * 2 + 1])
        } else {
          hasUv = false
        }
        const before = idx.length
        if (prim.indices !== undefined) {
          const ix = readAccessor(json, bin, prim.indices)
          for (let i = 0; i + 2 < ix.length; i += 3) idx.push(base + ix[i], base + ix[i + 1], base + ix[i + 2])
        } else {
          for (let i = 0; i + 2 < count; i += 3) idx.push(base + i, base + i + 1, base + i + 2)
        }
        const added = (idx.length - before) / 3
        for (let i = 0; i < added; i++) slotOfTri.push(slotCount)
        if (added > 0) slotCount++
      }
    }
    for (const c of node.children || []) visit(c, world, depth + 1)
  }
  const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
  for (const r of roots) visit(r, identity, 0)
  if (!pos.length || !idx.length) throw new Error('the model has no triangle mesh')
  return { pos, nor: hasNormals ? nor : null, uv: hasUv ? uv : null, idx, slotOfTri, slotCount }
}

function num(v) {
  return String(+Number(v).toPrecision(9))
}

function arr(values, fmt) {
  const parts = new Array(values.length)
  for (let i = 0; i < values.length; i++) parts[i] = fmt(values[i])
  return parts.join(',')
}

function buildFbx(name, mesh) {
  const { pos, nor, uv, idx, slotOfTri, slotCount } = mesh
  const slots = slotCount > 1 ? slotCount : 0
  const tris = idx.length / 3
  const polyIdx = new Array(idx.length)
  for (let i = 0; i < idx.length; i += 3) {
    polyIdx[i] = idx[i]
    polyIdx[i + 1] = idx[i + 1]
    polyIdx[i + 2] = -idx[i + 2] - 1
  }
  const normals = []
  if (nor) for (const v of idx) normals.push(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2])
  const uvs = uv ? uv : null
  const uvPerVert = []
  if (uvs) for (let i = 0; i < uvs.length; i += 2) uvPerVert.push(uvs[i], uvs[i + 1])
  const modelId = 1000001
  const geomId = 1000002
  const safe = name.replace(/[^A-Za-z0-9_\-]/g, '_') || 'Mesh'
  const L = []
  L.push('; FBX 7.4.0 project file')
  L.push('FBXHeaderExtension:  {')
  L.push('\tFBXHeaderVersion: 1003')
  L.push('\tFBXVersion: 7400')
  L.push('\tCreator: "Fortnite Unity Converter"')
  L.push('}')
  L.push('GlobalSettings:  {')
  L.push('\tVersion: 1000')
  L.push('\tProperties70:  {')
  L.push('\t\tP: "UpAxis", "int", "Integer", "",1')
  L.push('\t\tP: "UpAxisSign", "int", "Integer", "",1')
  L.push('\t\tP: "FrontAxis", "int", "Integer", "",2')
  L.push('\t\tP: "FrontAxisSign", "int", "Integer", "",1')
  L.push('\t\tP: "CoordAxis", "int", "Integer", "",0')
  L.push('\t\tP: "CoordAxisSign", "int", "Integer", "",1')
  L.push('\t\tP: "OriginalUpAxis", "int", "Integer", "",1')
  L.push('\t\tP: "OriginalUpAxisSign", "int", "Integer", "",1')
  L.push('\t\tP: "UnitScaleFactor", "double", "Number", "",100')
  L.push('\t\tP: "OriginalUnitScaleFactor", "double", "Number", "",100')
  L.push('\t}')
  L.push('}')
  L.push('Definitions:  {')
  L.push('\tVersion: 100')
  L.push('\tCount: ' + (slots ? 4 : 3))
  L.push('\tObjectType: "GlobalSettings" {')
  L.push('\t\tCount: 1')
  L.push('\t}')
  L.push('\tObjectType: "Model" {')
  L.push('\t\tCount: 1')
  L.push('\t}')
  L.push('\tObjectType: "Geometry" {')
  L.push('\t\tCount: 1')
  L.push('\t}')
  if (slots) {
    L.push('\tObjectType: "Material" {')
    L.push('\t\tCount: ' + slots)
    L.push('\t}')
  }
  L.push('}')
  L.push('Objects:  {')
  L.push('\tGeometry: ' + geomId + ', "Geometry::' + safe + '", "Mesh" {')
  L.push('\t\tProperties70:  {')
  L.push('\t\t}')
  L.push('\t\tVertices: *' + pos.length + ' {')
  L.push('\t\t\ta: ' + arr(pos, num))
  L.push('\t\t}')
  L.push('\t\tPolygonVertexIndex: *' + polyIdx.length + ' {')
  L.push('\t\t\ta: ' + polyIdx.join(','))
  L.push('\t\t}')
  L.push('\t\tGeometryVersion: 124')
  if (nor) {
    L.push('\t\tLayerElementNormal: 0 {')
    L.push('\t\t\tVersion: 101')
    L.push('\t\t\tName: ""')
    L.push('\t\t\tMappingInformationType: "ByPolygonVertex"')
    L.push('\t\t\tReferenceInformationType: "Direct"')
    L.push('\t\t\tNormals: *' + normals.length + ' {')
    L.push('\t\t\t\ta: ' + arr(normals, num))
    L.push('\t\t\t}')
    L.push('\t\t}')
  }
  if (uvs) {
    L.push('\t\tLayerElementUV: 0 {')
    L.push('\t\t\tVersion: 101')
    L.push('\t\t\tName: "UVMap"')
    L.push('\t\t\tMappingInformationType: "ByPolygonVertex"')
    L.push('\t\t\tReferenceInformationType: "IndexToDirect"')
    L.push('\t\t\tUV: *' + uvPerVert.length + ' {')
    L.push('\t\t\t\ta: ' + arr(uvPerVert, num))
    L.push('\t\t\t}')
    L.push('\t\t\tUVIndex: *' + idx.length + ' {')
    L.push('\t\t\t\ta: ' + idx.join(','))
    L.push('\t\t\t}')
    L.push('\t\t}')
  }
  if (slots) {
    L.push('\t\tLayerElementMaterial: 0 {')
    L.push('\t\t\tVersion: 101')
    L.push('\t\t\tName: ""')
    L.push('\t\t\tMappingInformationType: "ByPolygon"')
    L.push('\t\t\tReferenceInformationType: "IndexToDirect"')
    L.push('\t\t\tMaterials: *' + slotOfTri.length + ' {')
    L.push('\t\t\t\ta: ' + slotOfTri.join(','))
    L.push('\t\t\t}')
    L.push('\t\t}')
  }
  L.push('\t\tLayer: 0 {')
  L.push('\t\t\tVersion: 100')
  if (nor) {
    L.push('\t\t\tLayerElement:  {')
    L.push('\t\t\t\tType: "LayerElementNormal"')
    L.push('\t\t\t\tTypedIndex: 0')
    L.push('\t\t\t}')
  }
  if (uvs) {
    L.push('\t\t\tLayerElement:  {')
    L.push('\t\t\t\tType: "LayerElementUV"')
    L.push('\t\t\t\tTypedIndex: 0')
    L.push('\t\t\t}')
  }
  if (slots) {
    L.push('\t\t\tLayerElement:  {')
    L.push('\t\t\t\tType: "LayerElementMaterial"')
    L.push('\t\t\t\tTypedIndex: 0')
    L.push('\t\t\t}')
  }
  L.push('\t\t}')
  L.push('\t}')
  L.push('\tModel: ' + modelId + ', "Model::' + safe + '", "Mesh" {')
  L.push('\t\tVersion: 232')
  L.push('\t\tProperties70:  {')
  L.push('\t\t\tP: "InheritType", "enum", "", "",1')
  L.push('\t\t\tP: "DefaultAttributeIndex", "int", "Integer", "",0')
  L.push('\t\t}')
  L.push('\t\tShading: T')
  L.push('\t\tCulling: "CullingOff"')
  L.push('\t}')
  for (let k = 0; k < slots; k++) {
    L.push('\tMaterial: ' + (2000001 + k) + ', "Material::Slot' + k + '", "" {')
    L.push('\t\tVersion: 102')
    L.push('\t\tShadingModel: "lambert"')
    L.push('\t\tMultiLayer: 0')
    L.push('\t\tProperties70:  {')
    L.push('\t\t\tP: "DiffuseColor", "Color", "", "A",0.8,0.8,0.8')
    L.push('\t\t}')
    L.push('\t}')
  }
  L.push('}')
  L.push('Connections:  {')
  L.push('\tC: "OO",' + modelId + ',0')
  L.push('\tC: "OO",' + geomId + ',' + modelId)
  for (let k = 0; k < slots; k++) L.push('\tC: "OO",' + (2000001 + k) + ',' + modelId)
  L.push('}')
  return { text: L.join('\n') + '\n', tris }
}

function glbToFbx(glbFile, fbxFile) {
  const buf = fs.readFileSync(glbFile)
  const { json, bin } = parseGlb(buf)
  const mesh = collect(json, bin || Buffer.alloc(0))
  const name = path.basename(fbxFile, path.extname(fbxFile))
  const out = buildFbx(name, mesh)
  fs.writeFileSync(fbxFile, out.text, 'utf8')
  return fbxFile
}

module.exports = { glbToFbx }
