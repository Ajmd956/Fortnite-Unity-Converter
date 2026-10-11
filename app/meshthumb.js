const SIZE = 128
const FOV = 45 * Math.PI / 180
const PADDING = 1.15
const UNIT = 0.01
const AMBIENT = 0.22
const DIFFUSE = 0.78
const ALBEDO = 178
const NEAR = 0.1
const FAR = 10000

function render(buf) {
  if (!buf || buf.length < 8) return null
  const nv = buf.readInt32LE(0)
  const ni = buf.readInt32LE(4)
  if (nv <= 0 || ni < 3 || buf.length < 8 + nv * 24 + ni * 4) return null
  const pos = new Float32Array(nv * 3)
  const nrm = new Float32Array(nv * 3)
  let o = 8
  const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity]
  for (let i = 0; i < nv; i++) {
    const px = buf.readFloatLE(o), py = buf.readFloatLE(o + 4), pz = buf.readFloatLE(o + 8)
    const nx = buf.readFloatLE(o + 12), ny = buf.readFloatLE(o + 16), nz = buf.readFloatLE(o + 20)
    o += 24
    const X = px * UNIT, Y = pz * UNIT, Z = py * UNIT
    pos[i * 3] = X; pos[i * 3 + 1] = Y; pos[i * 3 + 2] = Z
    const l = Math.hypot(nx, nz, ny) || 1
    nrm[i * 3] = nx / l; nrm[i * 3 + 1] = nz / l; nrm[i * 3 + 2] = ny / l
    if (X < min[0]) min[0] = X; if (Y < min[1]) min[1] = Y; if (Z < min[2]) min[2] = Z
    if (X > max[0]) max[0] = X; if (Y > max[1]) max[1] = Y; if (Z > max[2]) max[2] = Z
  }
  const idx = new Uint32Array(ni)
  for (let i = 0; i < ni; i++) idx[i] = buf.readUInt32LE(o + i * 4)

  const c = [(min[0] + max[0]) / 2, (min[1] + max[1]) / 2, (min[2] + max[2]) / 2]
  const radius = Math.max(Math.hypot((max[0] - min[0]) / 2, (max[1] - min[1]) / 2, (max[2] - min[2]) / 2), 1e-3)
  const th = Math.tan(FOV / 2)
  const dist = radius / th * PADDING
  const k = 1 / Math.sqrt(3)
  const L = [k, k, k]
  const eye = [c[0] + L[0] * dist, c[1] + L[1] * dist, c[2] + L[2] * dist]
  const f = [-L[0], -L[1], -L[2]]
  let rx = f[1] * 0 - f[2] * 1, ry = f[2] * 0 - f[0] * 0, rz = f[0] * 1 - f[1] * 0
  const rl = Math.hypot(rx, ry, rz) || 1
  rx /= rl; ry /= rl; rz /= rl
  const ux = ry * f[2] - rz * f[1], uy = rz * f[0] - rx * f[2], uz = rx * f[1] - ry * f[0]

  const sx = new Float32Array(nv), sy = new Float32Array(nv), sz = new Float32Array(nv)
  const ok = new Uint8Array(nv)
  for (let i = 0; i < nv; i++) {
    const dx = pos[i * 3] - eye[0], dy = pos[i * 3 + 1] - eye[1], dz = pos[i * 3 + 2] - eye[2]
    const xc = rx * dx + ry * dy + rz * dz
    const yc = ux * dx + uy * dy + uz * dz
    const zc = -(f[0] * dx + f[1] * dy + f[2] * dz)
    const w = -zc
    if (w <= 1e-5) continue
    const clipX = xc / th, clipY = yc / th
    const clipZ = (FAR + NEAR) / (NEAR - FAR) * zc + 2 * FAR * NEAR / (NEAR - FAR)
    const ndcZ = clipZ / w
    if (ndcZ < -1 || ndcZ > 1) continue
    sx[i] = (clipX / w * 0.5 + 0.5) * (SIZE - 1)
    sy[i] = (1 - (clipY / w * 0.5 + 0.5)) * (SIZE - 1)
    sz[i] = ndcZ
    ok[i] = 1
  }

  const colors = new Uint8Array(SIZE * SIZE * 4)
  const depths = new Float32Array(SIZE * SIZE).fill(Infinity)
  let drawn = 0
  for (let t = 0; t + 2 < ni; t += 3) {
    const i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2]
    if (i0 >= nv || i1 >= nv || i2 >= nv) continue
    const e1x = pos[i1 * 3] - pos[i0 * 3], e1y = pos[i1 * 3 + 1] - pos[i0 * 3 + 1], e1z = pos[i1 * 3 + 2] - pos[i0 * 3 + 2]
    const e2x = pos[i2 * 3] - pos[i0 * 3], e2y = pos[i2 * 3 + 1] - pos[i0 * 3 + 1], e2z = pos[i2 * 3 + 2] - pos[i0 * 3 + 2]
    const cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x
    if (cx * cx + cy * cy + cz * cz < 1e-12) continue
    const n = [[nrm[i0 * 3], nrm[i0 * 3 + 1], nrm[i0 * 3 + 2]], [nrm[i1 * 3], nrm[i1 * 3 + 1], nrm[i1 * 3 + 2]], [nrm[i2 * 3], nrm[i2 * 3 + 1], nrm[i2 * 3 + 2]]]
    let ax = n[0][0] + n[1][0] + n[2][0], ay = n[0][1] + n[1][1] + n[2][1], az = n[0][2] + n[1][2] + n[2][2]
    const al = ax * ax + ay * ay + az * az
    if (al < 1e-12) continue
    const inv = 1 / Math.sqrt(al)
    ax *= inv; ay *= inv; az *= inv
    if (ax * L[0] + ay * L[1] + az * L[2] < 0) {
      for (const v of n) { v[0] = -v[0]; v[1] = -v[1]; v[2] = -v[2] }
      ax = -ax; ay = -ay; az = -az
    }
    if (ax * L[0] + ay * L[1] + az * L[2] <= 0.02) continue
    if (!ok[i0] || !ok[i1] || !ok[i2]) continue
    triangle(colors, depths, [sx[i0], sy[i0]], sz[i0], n[0], [sx[i1], sy[i1]], sz[i1], n[1], [sx[i2], sy[i2]], sz[i2], n[2], L)
    drawn++
  }
  return drawn ? colors : null
}

function edge(a, b, c) { return (c[0] - a[0]) * (b[1] - a[1]) - (c[1] - a[1]) * (b[0] - a[0]) }

function triangle(colors, depths, a, za, na, b, zb, nb, c, zc, nc, L) {
  let minX = Math.floor(Math.min(a[0], b[0], c[0])), maxX = Math.ceil(Math.max(a[0], b[0], c[0]))
  let minY = Math.floor(Math.min(a[1], b[1], c[1])), maxY = Math.ceil(Math.max(a[1], b[1], c[1]))
  minX = Math.max(0, Math.min(SIZE - 1, minX)); maxX = Math.max(0, Math.min(SIZE - 1, maxX))
  minY = Math.max(0, Math.min(SIZE - 1, minY)); maxY = Math.max(0, Math.min(SIZE - 1, maxY))
  const area = edge(a, b, c)
  if (Math.abs(area) < 1e-5) return
  const invArea = 1 / area
  for (let y = minY; y <= maxY; y++) {
    for (let x = minX; x <= maxX; x++) {
      const p = [x + 0.5, y + 0.5]
      const w0 = edge(b, c, p) * invArea, w1 = edge(c, a, p) * invArea, w2 = edge(a, b, p) * invArea
      if (w0 < 0 || w1 < 0 || w2 < 0) continue
      const depth = w0 * za + w1 * zb + w2 * zc
      const index = y * SIZE + x
      if (depth >= depths[index]) continue
      depths[index] = depth
      let nx = w0 * na[0] + w1 * nb[0] + w2 * nc[0], ny = w0 * na[1] + w1 * nb[1] + w2 * nc[1], nz = w0 * na[2] + w1 * nb[2] + w2 * nc[2]
      const nl = nx * nx + ny * ny + nz * nz
      if (nl > 1e-12) { const s = 1 / Math.sqrt(nl); nx *= s; ny *= s; nz *= s }
      const ndotl = Math.max(0, nx * L[0] + ny * L[1] + nz * L[2])
      const shade = Math.max(0, Math.min(1, AMBIENT + DIFFUSE * ndotl))
      const ch = Math.max(0, Math.min(255, Math.trunc(ALBEDO * shade)))
      const o = index * 4
      colors[o] = ch; colors[o + 1] = ch; colors[o + 2] = ch; colors[o + 3] = 255
    }
  }
}

module.exports = { render, SIZE }
