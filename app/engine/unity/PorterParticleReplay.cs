using System;
using System.Collections.Generic;
using UnityEngine;

// Plays back a baked Niagara replay (PRB1 file, see app/engine/ParticleBake.py) for the things a Unity
// Particle System cannot draw: ribbons, per-particle lights, decals and sprites that need more than four
// Dynamic Parameter values (and, from the Particle Converter, every mesh, sprite and ribbon, as the Blender add-on plays them). It runs in the game, there is nothing to set up: the converted prefab has it
// already, with the data, material and field layout filled in.
//
// Mesh streams, to match the converted shaders (_PorterParticleStreams = 1):
//   UV0 (x, y) texture coordinates, UV1 particle colour (the shader reads it as the Custom1 stream),
//   UV2 Dynamic Parameter slots 0..3, UV3 slots 4..7.
public class PorterParticleReplay : MonoBehaviour
{
    public TextAsset data;
    public string kind = "Ribbon";              // Ribbon, Light, Decal or Sprite
    public string[] fieldNames;
    public int[] fieldOffsets;
    public int[] fieldWidths;
    public int ageSlot = -1;                    // slot that carries the particle's age 0..1 (ParticleRelativeTime), -1 for none
    public int randSlot = -1;                   // slot that carries its random number 0..1
    public int[] dpComps;                       // Dynamic Parameter component numbers the rows carry, in slot order
    public bool loop = true;
    public bool playOnAwake = true;
    public float speed = 1f;
    public float lightScale = 1f;               // calibration: Unity light intensity per unit of Unreal light colour brightness (unverified against Unreal)
    public float radiusScale = 1f;
    public float exponentToRange = 1f;
    public Material material;
    public Material[] materials;                // a mesh renderer's materials, one a submesh
    public Mesh[] meshes;                       // a mesh renderer's meshes (Read/Write on), picked by each particle's mesh index
    public int facingMode;                      // sprite: 1 faces the particle's own direction; mesh: 1 X along its velocity, 2 X to the camera
    public bool lengthwise;                     // sprite: its length runs along its velocity (or its own vector)
    public float pivotX = 0.5f, pivotY = 0.5f;  // sprite: where the particle sits on its quad (UV space, V down)
    public int subX = 1, subY = 1;              // sprite: the flipbook's columns and rows
    public int ribbonShape;                     // ribbon: 0 a plane, 1 several planes turned about it, 2 a tube (facingMode: 0 width across the view, 1 across the particles' facing, 2 along their side vector)
    public bool projectDecals = true;           // decal: project onto the scene (needs the Porter/DecalProject shader in the project and a depth texture), else a flat quad
    public Shader decalShader;                  // set by the builder; found by name when empty
    public int ribbonSides = 2;                 // ribbon: how many planes (shape 1) or how many sides the tube has (shape 2)

    int frames, stride;
    float tick;
    int[] counts;
    int[] frameStart;
    float[] rows;
    float startTime;
    bool playing;
    int lastFrame = -2;

    Mesh mesh;
    MeshFilter filter;
    MeshRenderer meshRenderer;
    readonly List<Light> lights = new List<Light>();
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector3> normals = new List<Vector3>();
    readonly List<Vector4> tangents = new List<Vector4>();
    readonly List<Vector4> uv0 = new List<Vector4>();
    readonly List<Vector4> uv1 = new List<Vector4>();
    readonly List<Vector4> uv2 = new List<Vector4>();
    readonly List<Vector4> uv3 = new List<Vector4>();
    readonly List<Color> cols = new List<Color>();
    readonly List<int> tris = new List<int>();
    readonly List<int> order = new List<int>();

    void OnEnable()
    {
        if (!Load()) { enabled = false; return; }
        if (kind != "Light")
        {
            filter = GetComponent<MeshFilter>();
            if (filter == null) filter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
            if (material != null) meshRenderer.sharedMaterial = material;
            if (kind == "Decal" && projectDecals) SetUpProjection();
            if (materials != null && materials.Length > 0) meshRenderer.sharedMaterials = materials;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            mesh = new Mesh();
            mesh.name = "PorterReplay_" + kind;
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
        }
        if (playOnAwake) Play();
    }

    // decals: a projection material made from the decal material's texture and tint, and depth textures on the cameras that see it
    bool projecting;
    Material projMaterial;

    void SetUpProjection()
    {
        if (decalShader == null) decalShader = Shader.Find("Porter/DecalProject");
        if (decalShader == null) { projecting = false; return; }
        projMaterial = new Material(decalShader);
        projMaterial.name = "PorterDecalProject";
        if (material != null)
        {
            Texture best = null;
            string[] names = material.GetTexturePropertyNames();
            for (int i = 0; i < names.Length && best == null; i++)
            {
                string n = names[i].ToLowerInvariant();
                if ((n.Contains("base") || n.Contains("diffuse") || n.Contains("main") || n.Contains("color") || n.Contains("albedo")) && material.GetTexture(names[i]) != null) best = material.GetTexture(names[i]);
            }
            for (int i = 0; i < names.Length && best == null; i++) best = material.GetTexture(names[i]);
            if (best != null) projMaterial.SetTexture("_MainTex", best);
            if (material.HasProperty("_Color")) projMaterial.SetColor("_Color", material.GetColor("_Color"));
        }
        meshRenderer.sharedMaterial = projMaterial;
        projecting = true;
        Camera.onPreCull += NeedDepth;
    }

    static void NeedDepth(Camera c)
    {
        c.depthTextureMode |= DepthTextureMode.Depth;
    }

    void OnDisable()
    {
        if (projecting) { Camera.onPreCull -= NeedDepth; projecting = false; }
        if (projMaterial != null) { if (Application.isPlaying) Destroy(projMaterial); else DestroyImmediate(projMaterial); projMaterial = null; }
        if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); mesh = null; }
        for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].enabled = false;
    }

    public void Play()
    {
        startTime = Time.time;
        playing = true;
        lastFrame = -2;
    }

    public void Stop()
    {
        playing = false;
        Clear();
    }

    bool Load()
    {
        if (data == null) return false;
        byte[] b = data.bytes;
        if (b == null || b.Length < 16 || b[0] != (byte)'P' || b[1] != (byte)'R' || b[2] != (byte)'B' || b[3] != (byte)'1')
        {
            Debug.LogWarning("PorterParticleReplay: " + name + " has no PRB1 data");
            return false;
        }
        frames = BitConverter.ToInt32(b, 4);
        stride = BitConverter.ToInt32(b, 8);
        tick = BitConverter.ToSingle(b, 12);
        if (frames <= 0 || stride <= 0 || tick <= 0f) return false;
        counts = new int[frames];
        frameStart = new int[frames + 1];
        Buffer.BlockCopy(b, 16, counts, 0, 4 * frames);
        int total = 0;
        for (int i = 0; i < frames; i++) { frameStart[i] = total; total += counts[i]; }
        frameStart[frames] = total;
        rows = new float[total * stride];
        Buffer.BlockCopy(b, 16 + 4 * frames, rows, 0, 4 * total * stride);
        return true;
    }

    int Field(string n)
    {
        if (fieldNames == null) return -1;
        for (int i = 0; i < fieldNames.Length; i++) if (fieldNames[i] == n) return fieldOffsets[i];
        return -1;
    }

    float Get(int row, int field, int comp, float def)
    {
        return field < 0 ? def : rows[row * stride + field + comp];
    }

    void Clear()
    {
        if (mesh != null) mesh.Clear();
        for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].enabled = false;
        lastFrame = -2;
    }

    void LateUpdate()
    {
        if (!playing || rows == null) return;
        float t = (Time.time - startTime) * speed;
        int f = (int)(t / tick);
        if (f >= frames)
        {
            if (!loop) { playing = false; Clear(); return; }
            f %= frames;
        }
        if (f < 0) return;
        if (kind == "Light") { DrawLights(f); return; }
        // billboards follow the camera, so the mesh is rebuilt every frame
        if (mesh == null) return;
        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : transform.position - Vector3.forward * 10f;
        Vector3 camRight = cam != null ? cam.transform.right : Vector3.right;
        Vector3 camUp = cam != null ? cam.transform.up : Vector3.up;
        Vector3 localCam = transform.InverseTransformPoint(camPos);
        Vector3 localRight = transform.InverseTransformDirection(camRight);
        Vector3 localUp = transform.InverseTransformDirection(camUp);
        verts.Clear(); normals.Clear(); tangents.Clear(); uv0.Clear(); uv1.Clear(); uv2.Clear(); uv3.Clear(); cols.Clear(); tris.Clear();
        if (kind == "Ribbon") BuildRibbon(f, localCam);
        else if (kind == "Decal") BuildDecals(f);
        else if (kind == "Mesh") BuildMeshes(f, localCam);
        else BuildSprites(f, localRight, localUp, localCam);
        mesh.Clear();
        if (verts.Count == 0) return;
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTangents(tangents);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.SetUVs(2, uv2);
        mesh.SetUVs(3, uv3);
        mesh.SetColors(cols);
        if (kind == "Mesh")
        {
            mesh.subMeshCount = meshTris.Length;
            for (int s = 0; s < meshTris.Length; s++) mesh.SetTriangles(meshTris[s], s);
        }
        else mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
    }

    // ---- shared per-vertex data
    void AddVertex(Vector3 p, Vector3 n, Vector4 tan, Vector2 uv, int row)
    {
        AddVertexBlend(p, n, tan, new Vector4(uv.x, uv.y, 0f, 0f), row, row, 0f);
    }

    // a vertex whose colour and per-particle values are the blend of two particles' (a point smoothed between two of a ribbon's)
    void AddVertexBlend(Vector3 p, Vector3 n, Vector4 tan, Vector4 uv, int rowA, int rowB, float t)
    {
        verts.Add(p);
        normals.Add(n);
        tangents.Add(tan);
        uv0.Add(uv);
        Vector4 c, a, b;
        RowStreams(rowA, out c, out a, out b);
        if (t > 0f && rowB != rowA)
        {
            Vector4 c2, a2, b2;
            RowStreams(rowB, out c2, out a2, out b2);
            c = Vector4.Lerp(c, c2, t);
            a = Vector4.Lerp(a, a2, t);
            b = Vector4.Lerp(b, b2, t);
        }
        uv1.Add(c);
        cols.Add(Color.white);
        uv2.Add(a);
        uv3.Add(b);
    }

    // a particle's colour (UV1) and its eight per-particle value slots (UV2, UV3)
    void RowStreams(int row, out Vector4 color, out Vector4 a, out Vector4 b)
    {
        int fc = Field("color");
        color = new Vector4(Get(row, fc, 0, 1f), Get(row, fc, 1, 1f), Get(row, fc, 2, 1f), Get(row, fc, 3, 1f));
        a = Vector4.zero;
        b = Vector4.zero;
        if (dpComps != null)
        {
            for (int k = 0; k < dpComps.Length && k < 8; k++)
            {
                float v = Get(row, Field("dp" + dpComps[k]), 0, 0f);
                if (k < 4) a[k] = v; else b[k - 4] = v;
            }
        }
        if (ageSlot >= 0 && ageSlot < 8) { float av = Get(row, Field("age"), 0, 0f); if (ageSlot < 4) a[ageSlot] = av; else b[ageSlot - 4] = av; }
        if (randSlot >= 0 && randSlot < 8) { float rv = Get(row, Field("random"), 0, 0f); if (randSlot < 4) a[randSlot] = rv; else b[randSlot - 4] = rv; }
    }

    void Quad(int a, int b, int c, int d)
    {
        tris.Add(a); tris.Add(b); tris.Add(c);
        tris.Add(a); tris.Add(c); tris.Add(d);
        // both faces, so a particle is seen from either side
        tris.Add(a); tris.Add(c); tris.Add(b);
        tris.Add(a); tris.Add(d); tris.Add(c);
    }

    // ---- ribbons: one strip per ribbon, its points in link order, U along it and V across (as the Blender add-on strings them)
    void BuildRibbon(int f, Vector3 localCam)
    {
        int n = counts[f];
        if (n < 2) return;
        int fid = Field("id"), fp = Field("pos"), fw = Field("width"), frib = Field("ribbon"), frk = Field("rank");
        int first = frameStart[f];
        order.Clear();
        for (int i = 0; i < n; i++) order.Add(first + i);
        // the rank is the bake's order (ribbon, then link order); files from before it carried none and ran in spawn order
        int key = frk >= 0 ? frk : fid;
        order.Sort((x, y) => Get(x, key, 0, 0f).CompareTo(Get(y, key, 0, 0f)));
        int start = 0;
        while (start < n)
        {
            int end = start + 1;
            float id = frib >= 0 ? Get(order[start], frib, 0, 0f) : 0f;
            while (end < n && (frib < 0 || Get(order[end], frib, 0, 0f) == id)) end++;
            BuildStrip(start, end, localCam, fp, fw);
            start = end;
        }
    }

    Vector3 Pt(int r, int fp)
    {
        return new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f));
    }

    static Vector3 Catmull(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    // one ribbon: its points smoothed (Catmull-Rom, four samples between each pair, the rest blended), then swept: a plane, several planes
    // turned about it, or a tube; its width across the view, the particles' facing or their side vector
    void BuildStrip(int start, int end, Vector3 localCam, int fp, int fw)
    {
        int m = end - start;
        if (m < 2) return;
        int fu = Field("u"), fv0 = Field("v0"), fv1 = Field("v1"), fu1 = Field("u1"), fv10 = Field("v10"), fv11 = Field("v11"), fside = Field("side");
        const int sub = 4;
        int n = (m - 1) * sub + 1;
        var pos = new Vector3[n];
        var rowA = new int[n];
        var rowB = new int[n];
        var blend = new float[n];
        for (int i = 0; i < m - 1; i++)
        {
            int r0 = order[start + i], r1 = order[start + i + 1];
            Vector3 p0 = Pt(order[start + Mathf.Max(i - 1, 0)], fp), p1 = Pt(r0, fp), p2 = Pt(r1, fp), p3 = Pt(order[start + Mathf.Min(i + 2, m - 1)], fp);
            for (int s = 0; s < sub; s++)
            {
                int k = i * sub + s;
                float t = s / (float)sub;
                pos[k] = Catmull(p0, p1, p2, p3, t);
                rowA[k] = r0;
                rowB[k] = r1;
                blend[k] = t;
            }
        }
        int lastRow = order[start + m - 1];
        pos[n - 1] = Pt(lastRow, fp);
        rowA[n - 1] = lastRow;
        rowB[n - 1] = lastRow;
        int ring = ribbonShape == 2 ? Mathf.Max(3, ribbonSides) : 0;
        int planes = ribbonShape == 1 ? Mathf.Max(1, ribbonSides) : 1;
        int vps = ring > 0 ? ring + 1 : planes * 2;
        int baseIndex = verts.Count;
        for (int k = 0; k < n; k++)
        {
            int ra = rowA[k], rb = rowB[k];
            float t = blend[k];
            Vector3 tan = pos[Mathf.Min(k + 1, n - 1)] - pos[Mathf.Max(k - 1, 0)];
            if (tan.sqrMagnitude < 1e-12f) tan = Vector3.up;
            tan.Normalize();
            Vector3 face = new Vector3(Mathf.Lerp(Get(ra, fside, 0, 0f), Get(rb, fside, 0, 0f), t), Mathf.Lerp(Get(ra, fside, 1, 0f), Get(rb, fside, 1, 0f), t), Mathf.Lerp(Get(ra, fside, 2, 0f), Get(rb, fside, 2, 0f), t));
            Vector3 toCam = localCam - pos[k];
            Vector3 side = facingMode == 2 ? face : (facingMode == 1 ? Vector3.Cross(tan, face) : Vector3.Cross(tan, toCam));
            if (side.sqrMagnitude < 1e-12f) side = Vector3.Cross(tan, Vector3.up);
            if (side.sqrMagnitude < 1e-12f) side = Vector3.Cross(tan, Vector3.right);
            side.Normalize();
            float width = Mathf.Lerp(Get(ra, fw, 0, 0.01f), Get(rb, fw, 0, 0.01f), t);
            float half = width * 0.5f;
            float u0 = fu >= 0 ? Mathf.Lerp(Get(ra, fu, 0, 0f), Get(rb, fu, 0, 0f), t) : (float)k / (n - 1);
            float va0 = Mathf.Lerp(Get(ra, fv0, 0, 0f), Get(rb, fv0, 0, 0f), t), va1 = Mathf.Lerp(Get(ra, fv1, 0, 1f), Get(rb, fv1, 0, 1f), t);
            float u1 = fu1 >= 0 ? Mathf.Lerp(Get(ra, fu1, 0, 0f), Get(rb, fu1, 0, 0f), t) : u0;
            float vb0 = Mathf.Lerp(Get(ra, fv10, 0, 0f), Get(rb, fv10, 0, 0f), t), vb1 = Mathf.Lerp(Get(ra, fv11, 0, 1f), Get(rb, fv11, 0, 1f), t);
            Vector4 tan4 = new Vector4(tan.x, tan.y, tan.z, 1f);
            if (ring > 0)
            {
                Vector3 up2 = Vector3.Cross(tan, side).normalized;
                for (int q = 0; q <= ring; q++)
                {
                    float fq = q / (float)ring;
                    float ang = 2f * Mathf.PI * fq;
                    Vector3 dir = side * Mathf.Cos(ang) + up2 * Mathf.Sin(ang);
                    AddVertexBlend(pos[k] + dir * half, dir, tan4, new Vector4(u0, Mathf.Lerp(va0, va1, fq), u1, Mathf.Lerp(vb0, vb1, fq)), ra, rb, t);
                }
            }
            else
            {
                for (int pl = 0; pl < planes; pl++)
                {
                    Vector3 sd = planes > 1 ? Quaternion.AngleAxis(pl * 180f / planes, tan) * side : side;
                    Vector3 nrm = Vector3.Cross(sd, tan).normalized;
                    AddVertexBlend(pos[k] - sd * half, nrm, tan4, new Vector4(u0, va0, u1, vb0), ra, rb, t);
                    AddVertexBlend(pos[k] + sd * half, nrm, tan4, new Vector4(u0, va1, u1, vb1), ra, rb, t);
                }
            }
        }
        int units = ring > 0 ? ring : planes;
        int step = ring > 0 ? 1 : 2;
        for (int k = 0; k < n - 1; k++)
        {
            for (int q = 0; q < units; q++)
            {
                int a = baseIndex + k * vps + q * step;
                int b = a + 1, c = a + vps, d = b + vps;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
                // both faces, so the strip is seen from either side
                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(b); tris.Add(d); tris.Add(c);
            }
        }
    }

    // ---- sprites: a quad turned as the renderer says (to the camera, along its velocity, or to its own direction), spun, offset by its pivot
    void BuildSprites(int f, Vector3 right, Vector3 up, Vector3 localCam)
    {
        int fp = Field("pos"), fs = Field("size"), fr = Field("rotation"), ffac = Field("facing"), fal = Field("align"), fsub = Field("subimage");
        int fco = Field("camOff");
        int first = frameStart[f];
        int sx = Mathf.Max(1, subX), sy = Mathf.Max(1, subY);
        int total = sx * sy;
        for (int i = 0; i < counts[f]; i++)
        {
            int r = first + i;
            Vector3 p = new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f));
            float w = Get(r, fs, 0, 0.1f) * 0.5f, h = Get(r, fs, 1, 0.1f) * 0.5f;
            float ang = Get(r, fr, 0, 0f);
            float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
            Vector3 toCam = localCam - p;
            // drawn this far towards the camera from where it is (the renderer's camera offset)
            float camOff = Get(r, fco, 0, 0f);
            if (camOff != 0f && toCam.sqrMagnitude > 1e-12f)
            {
                p += toCam.normalized * camOff;
                toCam = localCam - p;
            }
            Vector3 along = new Vector3(Get(r, fal, 0, 0f), Get(r, fal, 1, 0f), Get(r, fal, 2, 0f));
            bool aligned = lengthwise && along.sqrMagnitude > 1e-10f;
            Vector3 ax, ay, nrm;
            if (facingMode == 1)
            {
                // its face to the particle's own direction (a custom facing vector)
                Vector3 face = new Vector3(Get(r, ffac, 0, 0f), Get(r, ffac, 1, 0f), Get(r, ffac, 2, 0f));
                nrm = face.sqrMagnitude > 1e-10f ? face.normalized : (toCam.sqrMagnitude > 1e-12f ? toCam.normalized : Vector3.forward);
                if (aligned)
                {
                    ay = Vector3.ProjectOnPlane(along, nrm);
                    ay = ay.sqrMagnitude > 1e-10f ? ay.normalized : Vector3.up;
                    ax = Vector3.Cross(ay, nrm).normalized;
                }
                else
                {
                    Quaternion q = Quaternion.FromToRotation(Vector3.forward, nrm);
                    Vector3 bx = q * Vector3.right, by = q * Vector3.up;
                    ax = bx * cs + by * sn;
                    ay = by * cs - bx * sn;
                }
            }
            else if (aligned)
            {
                // its length along its velocity, its face to the camera as far as that allows
                ay = along.normalized;
                nrm = toCam - Vector3.Project(toCam, ay);
                nrm = nrm.sqrMagnitude > 1e-10f ? nrm.normalized : Vector3.Cross(ay, Vector3.right).normalized;
                ax = Vector3.Cross(ay, nrm).normalized;
            }
            else
            {
                ax = right * cs + up * sn;
                ay = up * cs - right * sn;
                nrm = toCam.sqrMagnitude > 1e-12f ? toCam.normalized : Vector3.forward;
            }
            Vector3 center = p + ax * ((0.5f - pivotX) * 2f * w) + ay * ((pivotY - 0.5f) * 2f * h);
            Vector3 rx = ax * w, ry = ay * h;
            Vector4 tan = new Vector4(ax.x, ax.y, ax.z, 1f);
            Vector2 uvMin = Vector2.zero, uvSize = Vector2.one;
            if (total > 1)
            {
                int frame = Mathf.FloorToInt(Get(r, fsub, 0, 0f)) % total;
                if (frame < 0) frame = 0;
                int col = frame % sx, row = frame / sx;
                uvSize = new Vector2(1f / sx, 1f / sy);
                uvMin = new Vector2(col * uvSize.x, (sy - 1 - row) * uvSize.y);
            }
            int a = verts.Count;
            AddVertex(center - rx - ry, nrm, tan, uvMin, r);
            AddVertex(center + rx - ry, nrm, tan, new Vector2(uvMin.x + uvSize.x, uvMin.y), r);
            AddVertex(center + rx + ry, nrm, tan, uvMin + uvSize, r);
            AddVertex(center - rx + ry, nrm, tan, new Vector2(uvMin.x, uvMin.y + uvSize.y), r);
            tris.Add(a); tris.Add(a + 2); tris.Add(a + 1);
            tris.Add(a); tris.Add(a + 3); tris.Add(a + 2);
            if (facingMode == 1)
            {
                // a sprite with a facing of its own is seen from either side
                tris.Add(a); tris.Add(a + 1); tris.Add(a + 2);
                tris.Add(a); tris.Add(a + 2); tris.Add(a + 3);
            }
        }
    }

    // ---- meshes: the renderer's mesh copied to every particle (its rotation, scale and position), turned as the renderer says
    class MeshSource
    {
        public Vector3[] verts, normals;
        public Vector4[] tangents;
        public Vector2[] uvs;
        public int[][] tris;
    }

    MeshSource[] sources;
    List<int>[] meshTris = new List<int>[1] { new List<int>() };

    void ReadSources()
    {
        sources = new MeshSource[meshes != null ? meshes.Length : 0];
        int subs = 1;
        for (int i = 0; i < sources.Length; i++)
        {
            Mesh m = meshes[i];
            if (m == null) continue;
            if (!m.isReadable)
            {
                Debug.LogWarning("PorterParticlePlayer: mesh " + m.name + " is not readable (turn Read/Write on in its import settings), " + name + " draws nothing for it");
                continue;
            }
            var src = new MeshSource();
            src.verts = m.vertices;
            src.normals = m.normals;
            var t = new List<Vector4>();
            m.GetTangents(t);
            src.tangents = t.ToArray();
            src.uvs = m.uv;
            src.tris = new int[m.subMeshCount][];
            for (int s = 0; s < m.subMeshCount; s++) src.tris[s] = m.GetTriangles(s);
            subs = Mathf.Max(subs, m.subMeshCount);
            sources[i] = src;
        }
        meshTris = new List<int>[subs];
        for (int i = 0; i < subs; i++) meshTris[i] = new List<int>();
        // as many materials as submeshes, the last one repeated
        if (materials != null && materials.Length > 0 && meshRenderer != null && materials.Length < subs)
        {
            var padded = new Material[subs];
            for (int i = 0; i < subs; i++) padded[i] = materials[Mathf.Min(i, materials.Length - 1)];
            meshRenderer.sharedMaterials = padded;
        }
    }

    // its X axis along a direction, its Y as far up as that leaves
    static Quaternion XAlong(Vector3 dir)
    {
        Vector3 x = dir.normalized;
        Vector3 y = Vector3.up - Vector3.Project(Vector3.up, x);
        if (y.sqrMagnitude < 1e-8f) y = Vector3.forward - Vector3.Project(Vector3.forward, x);
        y.Normalize();
        Vector3 z = Vector3.Cross(x, y);
        return Quaternion.LookRotation(z, y);
    }

    void BuildMeshes(int f, Vector3 localCam)
    {
        if (sources == null) ReadSources();
        for (int s = 0; s < meshTris.Length; s++) meshTris[s].Clear();
        if (sources.Length == 0) return;
        int fp = Field("pos"), fq = Field("rotation"), fs = Field("scale"), fv = Field("velocity"), fi = Field("meshIndex"), fco = Field("camOff");
        int first = frameStart[f];
        for (int i = 0; i < counts[f]; i++)
        {
            int r = first + i;
            MeshSource src = sources[Mathf.Clamp(Mathf.RoundToInt(Get(r, fi, 0, 0f)), 0, sources.Length - 1)];
            if (src == null) continue;
            Vector3 p = new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f));
            float camOff = Get(r, fco, 0, 0f);
            if (camOff != 0f && (localCam - p).sqrMagnitude > 1e-12f) p += (localCam - p).normalized * camOff;
            Quaternion own = new Quaternion(Get(r, fq, 0, 0f), Get(r, fq, 1, 0f), Get(r, fq, 2, 0f), Get(r, fq, 3, 1f));
            if (own.x == 0f && own.y == 0f && own.z == 0f && own.w == 0f) own = Quaternion.identity;
            Quaternion q = own;
            if (facingMode == 1)
            {
                Vector3 vel = new Vector3(Get(r, fv, 0, 0f), Get(r, fv, 1, 0f), Get(r, fv, 2, 0f));
                if (vel.sqrMagnitude > 1e-10f) q = XAlong(vel) * own;
            }
            else if (facingMode == 2)
            {
                Vector3 toCam = localCam - p;
                if (toCam.sqrMagnitude > 1e-10f) q = XAlong(toCam) * own;
            }
            Vector3 sc = new Vector3(Get(r, fs, 0, 1f), Get(r, fs, 1, 1f), Get(r, fs, 2, 1f));
            int baseIndex = verts.Count;
            for (int v = 0; v < src.verts.Length; v++)
            {
                Vector3 pos = p + q * Vector3.Scale(src.verts[v], sc);
                // the engine turns a mesh particle's normals by its scale, not by the scale's inverse
                Vector3 nrm = v < src.normals.Length ? (q * Vector3.Scale(src.normals[v], sc)).normalized : Vector3.up;
                Vector4 t4 = v < src.tangents.Length ? src.tangents[v] : new Vector4(1f, 0f, 0f, 1f);
                Vector3 tw = q * new Vector3(t4.x, t4.y, t4.z);
                AddVertex(pos, nrm, new Vector4(tw.x, tw.y, tw.z, t4.w), v < src.uvs.Length ? src.uvs[v] : Vector2.zero, r);
            }
            for (int s = 0; s < src.tris.Length; s++)
            {
                List<int> list = meshTris[Mathf.Min(s, meshTris.Length - 1)];
                int[] t = src.tris[s];
                for (int k = 0; k < t.Length; k++) list.Add(baseIndex + t[k]);
            }
        }
    }

    // ---- decals: projected onto the scene through the depth texture (Porter/DecalProject), a box a decal; when that shader or the
    // depth texture is not there, each one is a flat quad lying in the particle's plane
    void BuildDecals(int f)
    {
        int fp = Field("pos"), fq = Field("rotation"), fs = Field("size");
        int first = frameStart[f];
        for (int i = 0; i < counts[f]; i++)
        {
            int r = first + i;
            Vector3 p = new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f));
            Quaternion q = new Quaternion(Get(r, fq, 0, 0f), Get(r, fq, 1, 0f), Get(r, fq, 2, 0f), Get(r, fq, 3, 1f));
            if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f) q = Quaternion.identity;
            float w = Mathf.Max(Get(r, fs, 0, 1f) * 0.5f, 1e-4f), d = Mathf.Max(Get(r, fs, 2, 1f) * 0.5f, 1e-4f);
            float h = Get(r, fs, 1, 0f) * 0.5f;
            Vector3 rx = q * Vector3.right * w, rz = q * Vector3.forward * d;
            Vector3 nrm = q * Vector3.up;
            if (projecting)
            {
                // how deep it projects: the size's own depth, else as far as it is wide
                if (h < 1e-4f) h = Mathf.Min(w, d);
                Vector3 ax = q * Vector3.right / w, ay = nrm / h, az = q * Vector3.forward / d;
                Vector4 c;
                int fc = Field("color");
                c = new Vector4(Get(r, fc, 0, 1f), Get(r, fc, 1, 1f), Get(r, fc, 2, 1f), Get(r, fc, 3, 1f));
                int a0 = verts.Count;
                for (int k = 0; k < 8; k++)
                {
                    float sx = (k & 1) == 0 ? -1f : 1f, sy = (k & 2) == 0 ? -1f : 1f, sz = (k & 4) == 0 ? -1f : 1f;
                    verts.Add(p + rx * sx + nrm * (h * sy) + rz * sz);
                    normals.Add(ax);
                    tangents.Add(new Vector4(ay.x, ay.y, ay.z, 1f));
                    uv0.Add(new Vector4(az.x, az.y, az.z, 0f));
                    uv1.Add(c);
                    uv2.Add(new Vector4(p.x, p.y, p.z, 0f));
                    uv3.Add(Vector4.zero);
                    cols.Add(Color.white);
                }
                // six faces; the shader draws their back sides, so each pixel in the box is drawn once
                int[,] fcs = { {0,2,3,1}, {4,5,7,6}, {0,1,5,4}, {2,6,7,3}, {0,4,6,2}, {1,3,7,5} };
                for (int s = 0; s < 6; s++)
                {
                    tris.Add(a0 + fcs[s, 0]); tris.Add(a0 + fcs[s, 1]); tris.Add(a0 + fcs[s, 2]);
                    tris.Add(a0 + fcs[s, 0]); tris.Add(a0 + fcs[s, 2]); tris.Add(a0 + fcs[s, 3]);
                }
                continue;
            }
            Vector4 tan = new Vector4(rx.normalized.x, rx.normalized.y, rx.normalized.z, 1f);
            int a = verts.Count;
            AddVertex(p - rx - rz, nrm, tan, new Vector2(0f, 0f), r);
            AddVertex(p + rx - rz, nrm, tan, new Vector2(1f, 0f), r);
            AddVertex(p + rx + rz, nrm, tan, new Vector2(1f, 1f), r);
            AddVertex(p - rx + rz, nrm, tan, new Vector2(0f, 1f), r);
            Quad(a, a + 1, a + 2, a + 3);
        }
    }

    // ---- lights: a pool of point lights, one per live light particle
    void DrawLights(int f)
    {
        int n = counts[f];
        int fp = Field("pos"), fc = Field("color"), fr = Field("radius");
        int first = frameStart[f];
        while (lights.Count < n)
        {
            var go = new GameObject("PorterReplayLight" + lights.Count);
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSave;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.shadows = LightShadows.None;
            lights.Add(l);
        }
        for (int i = 0; i < lights.Count; i++)
        {
            var l = lights[i];
            if (l == null) continue;
            if (i >= n) { l.enabled = false; continue; }
            int r = first + i;
            l.transform.localPosition = new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f));
            float cr = Mathf.Max(Get(r, fc, 0, 1f), 0f), cg = Mathf.Max(Get(r, fc, 1, 1f), 0f), cb = Mathf.Max(Get(r, fc, 2, 1f), 0f);
            float m = Mathf.Max(cr, Mathf.Max(cg, cb));
            float alpha = Mathf.Clamp01(Get(r, fc, 3, 1f));
            if (m <= 1e-4f || alpha <= 0f) { l.enabled = false; continue; }
            l.color = new Color(cr / m, cg / m, cb / m).gamma;
            l.intensity = m * lightScale * alpha;
            l.range = Mathf.Max(0.01f, Get(r, fr, 0, 1f) * radiusScale);
            l.enabled = true;
        }
    }
}
