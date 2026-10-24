using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Plays back a baked Niagara replay (PRB1 file, see app/engine/ParticleBake.py) for the things a Unity
// Particle System cannot draw: ribbons, per-particle lights, decals and sprites that need more than four
// Dynamic Parameter values (and, from the Particle Converter, every mesh, sprite and ribbon, as the Blender add-on plays them). It runs in play mode and in edit mode, there is nothing to set up: the converted prefab has it
// already, with the data, material and field layout filled in.
//
// Mesh streams, to match the converted shaders (_PorterParticleStreams = 1):
//   UV0 (x, y) texture coordinates, UV1 particle colour (the shader reads it as the Custom1 stream),
//   UV2 Dynamic Parameter slots 0..3, UV3 slots 4..7.
[ExecuteAlways]
[AddComponentMenu("Porter/Baked Particle System")]
public class PorterBakedParticleSystem : MonoBehaviour
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
    [Range(0f, 1f)] public float exponentToRange = 1f;     // how far the baked light exponent changes the range (0 ignores it)
    public Material material;
    public Material[] materials;                // a mesh renderer's materials, one a submesh
    public Mesh[] meshes;                       // a mesh renderer's meshes (Read/Write on), picked by each particle's mesh index
    public int facingMode;                      // sprite: 1 faces the particle's own direction, 2 faces the camera's position; mesh: 1 X along its velocity, 2 X to the camera
    public int sortMode;                        // sprites and meshes: 0 draw in file order, 1 far to near by view depth, 2 by view distance, 3 / 4 by the particle's sort key (low / high first)
    public Quaternion[] meshRotations;          // mesh: each mesh's own rotation, turned before the particle's orientation
    public Vector3[] meshScales;                // mesh: each mesh's own scale (Unity axes)
    public bool lengthwise;                     // sprite: its length runs along its velocity (or its own vector)
    public float pivotX = 0.5f, pivotY = 0.5f;  // sprite: where the particle sits on its quad (UV space, V down)
    public int subX = 1, subY = 1;              // sprite: the flipbook's columns and rows
    public int ribbonShape;                     // ribbon: 0 a plane, 1 several planes turned about it, 2 a tube (facingMode: 0 width across the view, 1 across the particles' facing, 2 along their side vector)
    public bool projectDecals = true;           // decal: project onto the scene (needs the Porter/DecalProject shader in the project and a depth texture), else a flat quad
    public Shader decalShader;                  // set by the builder; found by name when empty
    public int ribbonSides = 2;                 // ribbon: how many planes (shape 1) or how many sides the tube has (shape 2)

    // Particle System style controls. The defaults leave the baked look exactly as the bake wrote it.
    public float startDelay = 0f;               // seconds to wait before the effect starts
    public bool prewarm;                        // looping only: start as if the effect had been running for a while already
    public Color colorTint = Color.white;       // multiplies every particle's colour (rgb and alpha)
    public float colorIntensity = 1f;           // multiplies the colour's rgb (Unreal emissive colours go above 1)
    public float opacity = 1f;                  // multiplies the colour's alpha
    public float sizeScale = 1f;                // multiplies sprite sizes, mesh scales, ribbon widths and decal sizes

    // Emission: the baked particles are born again at their baked times, once every cycle when looping
    public float rateMultiplier = 1f;           // 1 as baked; under 1 thins the particles out, over 1 adds copies of them
    public float lifetimeScale = 1f;            // stretches or shortens each particle's whole life (birth times stay)
    public float lifetimeRandom = 0f;           // each particle's life varies by up to this fraction either way
    public int maxParticles = 0;                // 0 for no limit
    public int seed = 1;                        // the random choices above are made from this
    public float copyJitter = 0.05f;            // metres each added copy (rate over 1) is moved at random

    // Space and motion added on top of the baked path
    public bool worldSpace;                     // particles stay where they were born when this object moves
    public Vector3 positionScale = Vector3.one; // scales every particle's position about the object's origin
    public Vector3 extraVelocity;               // metres per second, added to every particle
    public Vector3 extraAcceleration;           // metres per second squared (gravity, wind)
    public float noiseStrength;                 // metres of random wander
    public float noiseFrequency = 1f;           // how fast the wander changes
    public float spinRate;                      // degrees per second added to sprite rotation, or to mesh rotation about Spin Axis
    public Vector3 spinAxis = Vector3.up;

    // Start values: each particle gets its own random pick from these ranges when it is born
    public float startSizeMin = 1f, startSizeMax = 1f;     // multiplies the particle's size
    public float startRotationMin, startRotationMax;       // degrees added to the particle's rotation
    public float speedMultiplier = 1f;                     // stretches or shortens how far each particle travels from where it was born
    public Vector3 directionRotation;                      // degrees: turns each particle's path about its birth point
    public float directionSpread;                          // degrees: tips each particle's path by up to this much, at random

    // Simulation: 0 follows the recording, 1 moves each particle from its baked birth position and velocity under the forces below
    [Range(0f, 1f)] public float simulate;
    public Vector3 simGravity;                             // metres per second squared
    public float simDrag;                                  // per second, slows the baked birth velocity down

    // Main module extras and the module switches (a module that is off does nothing)
    public bool start3DSize;                               // size is scaled per axis by Start Size 3D
    public Vector3 startSize3D = Vector3.one;
    [Range(0f, 1f)] public float flipRotation;             // the share of sprites whose rotation is turned the other way
    public float gravityModifier;                          // multiplies Physics.gravity, as the Unity Particle System does
    public bool useShape;                                  // Shape module: scale, move and scatter every particle's position
    public Vector3 shapeOffset;                            // metres
    public float shapeRandomize;                           // metres each particle is moved at random
    public bool useVelocity, useForce, useNoise, useRotation, useSimulation;

    // Over lifetime (each particle's own life, 0 at birth to 1 at the end of its baked path)
    public bool useColorOverLifetime;
    public Gradient colorOverLifetime = new Gradient();
    public bool useOpacityOverLifetime;
    public AnimationCurve opacityOverLifetime = AnimationCurve.Linear(0f, 1f, 1f, 1f);
    public bool useSizeOverLifetime;
    public AnimationCurve sizeOverLifetime = AnimationCurve.Linear(0f, 1f, 1f, 1f);

    public bool useColorBySpeed;
    public Gradient colorBySpeed = new Gradient();
    public Vector2 colorSpeedRange = new Vector2(0f, 10f);
    public bool useSizeBySpeed;
    public AnimationCurve sizeBySpeed = AnimationCurve.Linear(0f, 1f, 1f, 1f);
    public Vector2 sizeSpeedRange = new Vector2(0f, 10f);

    public bool useCustomData;
    public float[] customDataMultiply = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
    public float[] customDataAdd = new float[8];

    public bool useTextureSheet;
    public int sheetMode;
    public AnimationCurve sheetFrameOverTime = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    public float sheetCycles = 1f;
    public int sheetStartFrame;
    public int sheetFixedFrame;

    public int lightShadows;
    [Range(0f, 1f)] public float lightShadowStrength = 1f;
    public int lightRenderMode;

    public int shadowCasting;
    public bool receiveShadows;
    public int sortingOrder;
    public int lightProbes = 1;
    public int motionVectors = 1;

    public bool useMeshEdits;
    public Vector3[] meshScaleMultiply;
    public Vector3[] meshRotationOffset;

    int frames, stride;
    float tick;
    int[] counts;                               // the rows drawn this frame: the file's own, or a one-frame view of the tracked rows
    int[] frameStart;
    int[] fileCounts, fileStart;
    readonly int[] viewCounts = new int[1];
    readonly int[] viewStart = new int[2];
    float[] rows;                               // the file's rows
    float[] cur;                                // the rows being drawn
    float[] work = new float[0];
    int tracks;
    float cycle;
    int[] trackLen, trackFirst, trackRowStart, trackRows;
    float[] spawnSec;
    float maxDur;
    byte[] lerpKind;
    int quatAt = -1;
    int fPos = -1, fColor = -1, fRot = -1, fSize = -1, fScale = -1, fWidth = -1, fVel = -1, fFace = -1, fAlign = -1, fSide = -1, fRibbon = -1;
    int liveCount;
    float clock, lastNow;
    bool playing;
    bool paused;
    int currentTick;
    int peakParticles;
    bool began;
    float lastEditStep;
    readonly List<float> histT = new List<float>();
    readonly List<Matrix4x4> histM = new List<Matrix4x4>();

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

    static float Now()
    {
        return Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
    }

    void OnEnable()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditorApplication.delayCall += BeginEditMode;
            return;
        }
#endif
        Begin();
    }

#if UNITY_EDITOR
    void BeginEditMode()
    {
        if (this == null || !isActiveAndEnabled || Application.isPlaying) return;
        Begin();
        EditorApplication.update -= EditorTick;
        EditorApplication.update += EditorTick;
    }

    void EditorTick()
    {
        if (this == null)
        {
            EditorApplication.update -= EditorTick;
            return;
        }
        if (Application.isPlaying || !playing) return;
        float now = Time.realtimeSinceStartup;
        if (now - lastEditStep < 1f / 30f) return;
        lastEditStep = now;
        Step();
        SceneView.RepaintAll();
        EditorApplication.QueuePlayerLoopUpdate();
    }
#endif

    void Begin()
    {
        if (began) return;
        if (!Load())
        {
            if (Application.isPlaying) enabled = false;
            return;
        }
        began = true;
        bool edit = !Application.isPlaying;
        if (edit)
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform c = transform.GetChild(i);
                if (c.name.StartsWith("PorterReplayLight")) DestroyImmediate(c.gameObject);
            }
        }
        if (kind != "Light")
        {
            filter = GetComponent<MeshFilter>();
            if (filter == null) { filter = gameObject.AddComponent<MeshFilter>(); if (edit) filter.hideFlags = HideFlags.DontSave; }
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) { meshRenderer = gameObject.AddComponent<MeshRenderer>(); if (edit) meshRenderer.hideFlags = HideFlags.DontSave; }
            if (material != null) meshRenderer.sharedMaterial = material;
            if (kind == "Decal" && projectDecals) SetUpProjection();
            if (materials != null && materials.Length > 0) meshRenderer.sharedMaterials = materials;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            mesh = new Mesh();
            mesh.name = "PorterReplay_" + kind;
            if (edit) mesh.hideFlags = HideFlags.HideAndDontSave;
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
        }
        ApplyRendererSettings();
        if (playOnAwake) Play();
    }

    void ApplyRendererSettings()
    {
        if (meshRenderer == null) return;
        meshRenderer.shadowCastingMode = (UnityEngine.Rendering.ShadowCastingMode)Mathf.Clamp(shadowCasting, 0, 3);
        meshRenderer.receiveShadows = receiveShadows;
        meshRenderer.sortingOrder = sortingOrder;
        meshRenderer.lightProbeUsage = (UnityEngine.Rendering.LightProbeUsage)Mathf.Clamp(lightProbes, 0, 3);
        meshRenderer.motionVectorGenerationMode = (MotionVectorGenerationMode)Mathf.Clamp(motionVectors, 0, 2);
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
        if (!Application.isPlaying) projMaterial.hideFlags = HideFlags.HideAndDontSave;
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
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
        began = false;
        if (projecting) { Camera.onPreCull -= NeedDepth; projecting = false; }
        if (projMaterial != null) { if (Application.isPlaying) Destroy(projMaterial); else DestroyImmediate(projMaterial); projMaterial = null; }
        if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); mesh = null; }
        for (int i = 0; i < lights.Count; i++)
        {
            if (lights[i] == null) continue;
            if (Application.isPlaying) lights[i].enabled = false;
            else DestroyImmediate(lights[i].gameObject);
        }
        if (!Application.isPlaying) lights.Clear();
    }

    public bool IsPlaying { get { return playing; } }
    public bool IsPaused { get { return paused; } }
    public int TotalTicks { get { return frames; } }
    public float TickSeconds { get { return tick; } }
    public int TrackCount { get { return tracks; } }
    public bool Tracked { get { return tracks > 0; } }
    public float Duration { get { return tracks > 0 ? cycle : frames * tick; } }     // one cycle, the time a loop takes
    public int CurrentTick { get { return currentTick; } }
    public int PeakParticles { get { return peakParticles; } }
    public int CurrentParticles { get { return liveCount; } }

    // seconds into the current cycle (0 .. Duration)
    public float PlayTime
    {
        get
        {
            float d = Duration;
            if (d <= 0f) return 0f;
            float t = Mathf.Max(clock, 0f);
            if (t >= d) t = loop ? t % d : d;
            return t;
        }
    }

    public void Play()
    {
        clock = -startDelay;
        lastNow = Now();
        paused = false;
        playing = true;
        histT.Clear();
        histM.Clear();
    }

    public void Restart()
    {
        Play();
    }

    public void Pause()
    {
        if (!playing || paused) return;
        paused = true;
    }

    public void Resume()
    {
        if (!paused) return;
        lastNow = Now();
        paused = false;
    }

    // jump to a time and hold there (the inspector's playback slider)
    public void Scrub(float seconds)
    {
        playing = true;
        paused = true;
        clock = Mathf.Clamp(seconds, 0f, Duration);
    }

    public void Stop()
    {
        playing = false;
        paused = false;
        Clear();
    }

    // sorting: the rows of this tick in the order they are drawn (null = file order)
    int[] drawOrder;
    float[] sortKeys;

    int RowAt(int first, int i)
    {
        return drawOrder != null ? drawOrder[i] : first + i;
    }

    void BuildOrder(int f, Vector3 localCam, Vector3 localForward)
    {
        drawOrder = null;
        int n = counts[f];
        if (sortMode <= 0 || n < 2 || (kind != "Sprite" && kind != "Mesh")) return;
        int first = frameStart[f];
        int fp = Field("pos"), fk = Field("sortKey");
        if (sortKeys == null || sortKeys.Length < n) { sortKeys = new float[n]; drawOrder = new int[n]; }
        else drawOrder = new int[n];
        for (int i = 0; i < n; i++)
        {
            int r = first + i;
            drawOrder[i] = r;
            Vector3 d = new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f)) - localCam;
            // far first, so the near ones are drawn over them
            if (sortMode == 1) sortKeys[i] = -Vector3.Dot(d, localForward);
            else if (sortMode == 2) sortKeys[i] = -d.magnitude;
            else if (sortMode == 3) sortKeys[i] = Get(r, fk, 0, 0f);
            else sortKeys[i] = -Get(r, fk, 0, 0f);
        }
        Array.Sort(sortKeys, drawOrder, 0, n);
    }

    Vector4 Tint(Vector4 c)
    {
        float k = colorIntensity;
        return new Vector4(c.x * colorTint.r * k, c.y * colorTint.g * k, c.z * colorTint.b * k, c.w * colorTint.a * opacity);
    }

    bool Load()
    {
        if (data == null)
        {
            Debug.LogWarning("PorterBakedParticleSystem: " + name + " has no baked data assigned, it will not play");
            return false;
        }
        byte[] b = data.bytes;
        bool v2 = b != null && b.Length >= 24 && b[0] == (byte)'P' && b[1] == (byte)'R' && b[2] == (byte)'B' && b[3] == (byte)'2';
        bool v1 = b != null && b.Length >= 16 && b[0] == (byte)'P' && b[1] == (byte)'R' && b[2] == (byte)'B' && b[3] == (byte)'1';
        if (!v1 && !v2)
        {
            Debug.LogWarning("PorterBakedParticleSystem: " + name + " has no PRB1 or PRB2 data");
            return false;
        }
        frames = BitConverter.ToInt32(b, 4);
        stride = BitConverter.ToInt32(b, 8);
        tick = BitConverter.ToSingle(b, 12);
        int head = 16;
        tracks = 0;
        cycle = 0f;
        if (v2)
        {
            tracks = BitConverter.ToInt32(b, 16);
            cycle = BitConverter.ToSingle(b, 20);
            head = 24;
        }
        if (frames <= 0 || stride <= 0 || tick <= 0f) return false;
        if (b.Length < head + 4 * frames) return false;
        fileCounts = new int[frames];
        fileStart = new int[frames + 1];
        Buffer.BlockCopy(b, head, fileCounts, 0, 4 * frames);
        int total = 0;
        peakParticles = 0;
        for (int i = 0; i < frames; i++) { fileStart[i] = total; total += fileCounts[i]; if (fileCounts[i] > peakParticles) peakParticles = fileCounts[i]; }
        fileStart[frames] = total;
        long need = (long)head + 4L * frames + 4L * total * stride + (v2 ? 4L * total : 0L);
        if (b.Length < need)
        {
            Debug.LogWarning("PorterBakedParticleSystem: " + name + " baked data is shorter than its header says");
            return false;
        }
        rows = new float[total * stride];
        Buffer.BlockCopy(b, head + 4 * frames, rows, 0, 4 * total * stride);
        counts = fileCounts;
        frameStart = fileStart;
        cur = rows;
        CacheFields();
        if (v2 && tracks > 0) ReadTracks(b, head + 4 * frames + 4 * total * stride, total);
        else tracks = 0;
        return true;
    }

    int Field(string n)
    {
        if (fieldNames == null) return -1;
        for (int i = 0; i < fieldNames.Length; i++) if (fieldNames[i] == n) return fieldOffsets[i];
        return -1;
    }

    int Width(string n)
    {
        if (fieldNames == null) return 0;
        for (int i = 0; i < fieldNames.Length; i++) if (fieldNames[i] == n) return fieldWidths[i];
        return 0;
    }

    void CacheFields()
    {
        fPos = Field("pos"); fColor = Field("color"); fRot = Field("rotation"); fVel = Field("velocity"); fFace = Field("facing");
        fAlign = Field("align"); fSide = Field("side"); fRibbon = Field("ribbon"); fWidth = Field("width");
        fSize = Field("size"); fScale = Field("scale");
        quatAt = (fRot >= 0 && Width("rotation") == 4) ? fRot : -1;
        // fields that are numbers of a kind (an index, an id), not quantities to blend between two ticks
        lerpKind = new byte[stride];
        string[] steps = { "id", "ribbon", "rank", "subimage", "meshIndex" };
        for (int k = 0; k < steps.Length; k++)
        {
            int o = Field(steps[k]);
            int w = Width(steps[k]);
            for (int c = 0; c < w && o >= 0 && o + c < stride; c++) lerpKind[o + c] = 1;
        }
    }

    // one track per particle: where its rows are, and when it is born
    void ReadTracks(byte[] b, int offset, int total)
    {
        int[] of = new int[total];
        Buffer.BlockCopy(b, offset, of, 0, 4 * total);
        trackLen = new int[tracks];
        trackFirst = new int[tracks];
        for (int t = 0; t < tracks; t++) trackFirst[t] = -1;
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < fileCounts[f]; i++)
            {
                int t = of[fileStart[f] + i];
                if (t < 0 || t >= tracks) continue;
                if (trackFirst[t] < 0) trackFirst[t] = f;
                trackLen[t]++;
            }
        }
        trackRowStart = new int[tracks + 1];
        for (int t = 0; t < tracks; t++) trackRowStart[t + 1] = trackRowStart[t] + trackLen[t];
        trackRows = new int[trackRowStart[tracks]];
        int[] fill = new int[tracks];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < fileCounts[f]; i++)
            {
                int t = of[fileStart[f] + i];
                if (t < 0 || t >= tracks) continue;
                trackRows[trackRowStart[t] + fill[t]] = fileStart[f] + i;
                fill[t]++;
            }
        }
        spawnSec = new float[tracks];
        maxDur = 0f;
        for (int t = 0; t < tracks; t++)
        {
            spawnSec[t] = Mathf.Max(trackFirst[t], 0) * tick;
            maxDur = Mathf.Max(maxDur, Mathf.Max(trackLen[t] - 1, 0) * tick);
            // tracks come numbered in birth order; keep the search below valid if a file ever does not
            if (t > 0 && spawnSec[t] < spawnSec[t - 1]) spawnSec[t] = spawnSec[t - 1];
        }
        if (cycle <= 0f) cycle = frames * tick;
    }

    float Get(int row, int field, int comp, float def)
    {
        return field < 0 ? def : cur[row * stride + field + comp];
    }

    void Clear()
    {
        liveCount = 0;
        if (mesh != null) mesh.Clear();
        for (int i = 0; i < lights.Count; i++) if (lights[i] != null) lights[i].enabled = false;
    }

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        Step();
    }

    void Step()
    {
        if (!playing || rows == null) return;
        ApplyRendererSettings();
        float now = Now();
        float dt = Mathf.Clamp(now - lastNow, 0f, 0.25f);
        lastNow = now;
        if (!paused) clock += dt * Mathf.Max(speed, 0f);
        float t = clock;
        // still inside the start delay
        if (t < 0f) { Clear(); return; }
        int f = 0;
        if (tracks > 0)
        {
            float lifeMax = Mathf.Max(lifetimeScale, 0.01f) * (1f + Mathf.Clamp01(lifetimeRandom));
            if (!loop && t > cycle + maxDur * lifeMax + 0.1f)
            {
                if (paused) t = cycle + maxDur * lifeMax;
                else { playing = false; Clear(); return; }
            }
            if (worldSpace) RecordHistory(t, lifeMax);
            Assemble(t);
            currentTick = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(PlayTime, 0f) / tick), 0, frames - 1);
        }
        else
        {
            f = (int)(t / tick);
            if (f >= frames)
            {
                if (loop) f %= frames;
                else if (paused) f = frames - 1;
                else { playing = false; Clear(); return; }
            }
            if (f < 0) return;
            counts = fileCounts;
            frameStart = fileStart;
            cur = rows;
            currentTick = f;
            liveCount = counts[f];
        }
        if (kind == "Light") { DrawLights(f); return; }
        // billboards follow the camera, so the mesh is rebuilt every frame
        if (mesh == null) return;
        Camera cam = Camera.main;
#if UNITY_EDITOR
        if (!Application.isPlaying && SceneView.lastActiveSceneView != null) cam = SceneView.lastActiveSceneView.camera;
#endif
        Vector3 camPos = cam != null ? cam.transform.position : transform.position - Vector3.forward * 10f;
        Vector3 camRight = cam != null ? cam.transform.right : Vector3.right;
        Vector3 camUp = cam != null ? cam.transform.up : Vector3.up;
        Vector3 localCam = transform.InverseTransformPoint(camPos);
        Vector3 localRight = transform.InverseTransformDirection(camRight);
        Vector3 localUp = transform.InverseTransformDirection(camUp);
        Vector3 localForward = transform.InverseTransformDirection(cam != null ? cam.transform.forward : Vector3.forward);
        if (kind == "Sprite" || kind == "Mesh") BuildOrder(f, localCam, localForward);
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

    // ---- the tracked replay: every particle's baked life is started again at its baked birth time, in every cycle that still has live ones
    const int LutSize = 32;
    float[] sizeLut, opacityLut;
    Color[] colorLut;

    void BuildLuts()
    {
        if (sizeLut == null) { sizeLut = new float[LutSize]; opacityLut = new float[LutSize]; colorLut = new Color[LutSize]; }
        for (int i = 0; i < LutSize; i++)
        {
            float x = i / (float)(LutSize - 1);
            sizeLut[i] = useSizeOverLifetime && sizeOverLifetime != null ? Mathf.Max(0f, sizeOverLifetime.Evaluate(x)) : 1f;
            opacityLut[i] = useOpacityOverLifetime && opacityOverLifetime != null ? Mathf.Clamp01(opacityOverLifetime.Evaluate(x)) : 1f;
            colorLut[i] = useColorOverLifetime && colorOverLifetime != null ? colorOverLifetime.Evaluate(x) : Color.white;
        }
    }

    static float Lookup(float[] lut, float x)
    {
        float f = Mathf.Clamp01(x) * (LutSize - 1);
        int i = Mathf.Min((int)f, LutSize - 2);
        return Mathf.Lerp(lut[i], lut[i + 1], f - i);
    }

    static uint Hash(int a, int b, int c, int d)
    {
        unchecked
        {
            uint h = (uint)a * 747796405u + 2891336453u;
            h ^= (uint)b * 2654435761u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)c * 2246822519u + 2654435769u;
            h *= 3266489917u;
            h ^= (uint)d * 668265263u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }

    static float Rand(uint h)
    {
        return (h & 0xFFFFFFu) / 16777216f;
    }

    int FirstBornAfter(float seconds)
    {
        int lo = 0, hi = tracks;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (spawnSec[mid] < seconds) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    void RecordHistory(float t, float lifeMax)
    {
        if (histT.Count > 0 && t < histT[histT.Count - 1] - 1e-4f) { histT.Clear(); histM.Clear(); }
        histT.Add(t);
        histM.Add(transform.localToWorldMatrix);
        float keep = t - (maxDur * lifeMax + cycle + 1f);
        int drop = 0;
        while (drop < histT.Count - 1 && histT[drop + 1] < keep) drop++;
        if (drop > 0) { histT.RemoveRange(0, drop); histM.RemoveRange(0, drop); }
    }

    Matrix4x4 SpawnMatrix(float time)
    {
        int n = histT.Count;
        if (n == 0) return transform.localToWorldMatrix;
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (histT[mid] <= time) lo = mid; else hi = mid - 1;
        }
        return histM[lo];
    }

    void Assemble(float t)
    {
        float ls = Mathf.Max(lifetimeScale, 0.01f);
        float lr = Mathf.Clamp01(lifetimeRandom);
        float lsMax = ls * (1f + lr);
        float rate = Mathf.Max(rateMultiplier, 0f);
        int copies = Mathf.CeilToInt(rate);
        int cap = maxParticles > 0 ? maxParticles : int.MaxValue;
        BuildLuts();
        Matrix4x4 w2l = transform.worldToLocalMatrix;
        int n = 0;
        int kMax = loop ? Mathf.FloorToInt(t / cycle) : 0;
        int kMin = 0;
        if (loop)
        {
            kMin = Mathf.FloorToInt((t - maxDur * lsMax - 0.2f) / cycle);
            if (!prewarm && kMin < 0) kMin = 0;
        }
        for (int k = kMin; k <= kMax && n < cap; k++)
        {
            float baseT = k * cycle;
            float local = t - baseT;
            if (local < 0f) continue;
            for (int tr = FirstBornAfter(local - maxDur * lsMax - 0.2f); tr < tracks && n < cap; tr++)
            {
                float born = spawnSec[tr];
                if (born > local) break;
                int len = trackLen[tr];
                if (len <= 0) continue;
                for (int c = 0; c < copies && n < cap; c++)
                {
                    int kk = k + 100000;
                    float wanted = rate - c;
                    if (wanted < 1f && Rand(Hash(seed, tr, kk, c + 977)) >= wanted) continue;
                    // copies after the first are born a little later, so they are not on top of the first
                    float shift = c == 0 ? 0f : Rand(Hash(seed, tr, kk, c + 55)) * 0.1f;
                    float life = ls * (1f + lr * (Rand(Hash(seed, tr, kk, c + 31)) * 2f - 1f));
                    float elapsed = local - born - shift;
                    if (elapsed < 0f) continue;
                    float sTick = elapsed / life / tick;
                    float last = Mathf.Max(len - 1, 0.999f);
                    if (sTick > last) continue;
                    if ((n + 1) * stride > work.Length) Array.Resize(ref work, Mathf.Max(work.Length * 2, (n + 1) * stride * 2));
                    WriteRow(n, tr, sTick, elapsed, k, c, kk, baseT + born + shift, ref w2l);
                    n++;
                }
            }
        }
        viewCounts[0] = n;
        viewStart[0] = 0;
        viewStart[1] = n;
        counts = viewCounts;
        frameStart = viewStart;
        cur = work;
        liveCount = n;
    }

    void ConvDirection(int d, int o, ref Matrix4x4 conv)
    {
        if (o < 0) return;
        Vector3 v = conv.MultiplyVector(new Vector3(work[d + o], work[d + o + 1], work[d + o + 2]));
        work[d + o] = v.x; work[d + o + 1] = v.y; work[d + o + 2] = v.z;
    }

    // one particle's row at a moment of its life (the baked ticks either side of it blended), with the controls above applied
    void WriteRow(int dst, int tr, float s, float elapsed, int cycleNo, int copy, int kk, float birthClock, ref Matrix4x4 w2l)
    {
        int len = trackLen[tr];
        int i0 = (int)s;
        float fr = s - i0;
        if (i0 >= len - 1) { i0 = len - 1; fr = 0f; }
        int a = trackRows[trackRowStart[tr] + i0] * stride;
        int b = fr > 0f ? trackRows[trackRowStart[tr] + i0 + 1] * stride : a;
        int d = dst * stride;
        for (int c = 0; c < stride; c++)
            work[d + c] = (fr <= 0f || lerpKind[c] == 1) ? rows[a + c] : rows[a + c] + (rows[b + c] - rows[a + c]) * fr;
        if (quatAt >= 0 && fr > 0f)
        {
            Quaternion qa = new Quaternion(rows[a + quatAt], rows[a + quatAt + 1], rows[a + quatAt + 2], rows[a + quatAt + 3]);
            Quaternion qb = new Quaternion(rows[b + quatAt], rows[b + quatAt + 1], rows[b + quatAt + 2], rows[b + quatAt + 3]);
            float dot = qa.x * qb.x + qa.y * qb.y + qa.z * qb.z + qa.w * qb.w;
            float sg = dot < 0f ? -1f : 1f;
            float x = qa.x + (qb.x * sg - qa.x) * fr, y = qa.y + (qb.y * sg - qa.y) * fr, z = qa.z + (qb.z * sg - qa.z) * fr, w = qa.w + (qb.w * sg - qa.w) * fr;
            float m = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (m > 1e-8f) { work[d + quatAt] = x / m; work[d + quatAt + 1] = y / m; work[d + quatAt + 2] = z / m; work[d + quatAt + 3] = w / m; }
        }
        float age01 = len > 1 ? Mathf.Clamp01(s / (len - 1)) : 0f;
        // size
        float sz = (useSizeOverLifetime ? Lookup(sizeLut, age01) : 1f) * (startSizeMin == startSizeMax ? startSizeMin : Mathf.Lerp(startSizeMin, startSizeMax, Rand(Hash(seed, tr, 11, 0))));
        if (sz != 1f)
        {
            if (fSize >= 0) { int w = Mathf.Max(1, Width("size")); for (int c = 0; c < w; c++) work[d + fSize + c] *= sz; }
            if (fScale >= 0) { for (int c = 0; c < 3; c++) work[d + fScale + c] *= sz; }
            if (fWidth >= 0) work[d + fWidth] *= sz;
        }
        if (useSizeBySpeed && fVel >= 0 && sizeBySpeed != null)
        {
            float sp = new Vector3(work[d + fVel], work[d + fVel + 1], work[d + fVel + 2]).magnitude;
            float sk = Mathf.Max(0f, sizeBySpeed.Evaluate(Mathf.InverseLerp(sizeSpeedRange.x, Mathf.Max(sizeSpeedRange.y, sizeSpeedRange.x + 1e-4f), sp)));
            if (fSize >= 0) { int ws = Mathf.Max(1, Width("size")); for (int c = 0; c < ws; c++) work[d + fSize + c] *= sk; }
            if (fScale >= 0) { for (int c = 0; c < 3; c++) work[d + fScale + c] *= sk; }
            if (fWidth >= 0) work[d + fWidth] *= sk;
        }
        if (useColorBySpeed && fColor >= 0 && fVel >= 0 && colorBySpeed != null)
        {
            float sp2 = new Vector3(work[d + fVel], work[d + fVel + 1], work[d + fVel + 2]).magnitude;
            Color cs = colorBySpeed.Evaluate(Mathf.InverseLerp(colorSpeedRange.x, Mathf.Max(colorSpeedRange.y, colorSpeedRange.x + 1e-4f), sp2));
            work[d + fColor] *= cs.r;
            work[d + fColor + 1] *= cs.g;
            work[d + fColor + 2] *= cs.b;
            work[d + fColor + 3] *= cs.a;
        }
        if (start3DSize)
        {
            if (fSize >= 0) { int w3 = Mathf.Max(1, Width("size")); for (int c3 = 0; c3 < w3; c3++) work[d + fSize + c3] *= startSize3D[Mathf.Min(c3, 2)]; }
            if (fScale >= 0) { for (int c3 = 0; c3 < 3; c3++) work[d + fScale + c3] *= startSize3D[c3]; }
            if (fWidth >= 0) work[d + fWidth] *= startSize3D.x;
        }
        // colour and opacity
        if (fColor >= 0 && (useColorOverLifetime || useOpacityOverLifetime))
        {
            Color cl = Color.white;
            if (useColorOverLifetime)
            {
                float f = Mathf.Clamp01(age01) * (LutSize - 1);
                int ci = Mathf.Min((int)f, LutSize - 2);
                cl = Color.Lerp(colorLut[ci], colorLut[ci + 1], f - ci);
            }
            float op = useOpacityOverLifetime ? Lookup(opacityLut, age01) : 1f;
            work[d + fColor] *= cl.r;
            work[d + fColor + 1] *= cl.g;
            work[d + fColor + 2] *= cl.b;
            work[d + fColor + 3] *= cl.a * op;
        }
        // start values: the path turned about its birth point, and each particle's own random pick
        float sim = useSimulation ? simulate : 0f;
        bool turned = directionRotation != Vector3.zero || directionSpread > 0f;
        Quaternion turn = Quaternion.identity;
        if (turned)
        {
            turn = Quaternion.Euler(directionRotation);
            if (directionSpread > 0f)
            {
                Vector3 axis = new Vector3(Rand(Hash(seed, tr, 41, 0)) * 2f - 1f, Rand(Hash(seed, tr, 41, 1)) * 2f - 1f, Rand(Hash(seed, tr, 41, 2)) * 2f - 1f);
                if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;
                turn = Quaternion.AngleAxis(Rand(Hash(seed, tr, 41, 3)) * directionSpread, axis.normalized) * turn;
            }
            if (fVel >= 0)
            {
                Vector3 v = turn * new Vector3(work[d + fVel], work[d + fVel + 1], work[d + fVel + 2]);
                work[d + fVel] = v.x; work[d + fVel + 1] = v.y; work[d + fVel + 2] = v.z;
            }
        }
        // position: scaled, then the extra velocity, acceleration and wander
        if (fPos >= 0)
        {
            Vector3 p = new Vector3(work[d + fPos], work[d + fPos + 1], work[d + fPos + 2]);
            if (speedMultiplier != 1f || turned || sim > 0f)
            {
                int r0 = trackRows[trackRowStart[tr]] * stride;
                Vector3 p0 = new Vector3(rows[r0 + fPos], rows[r0 + fPos + 1], rows[r0 + fPos + 2]);
                Vector3 off = turn * ((p - p0) * speedMultiplier);
                if (sim > 0f && len > 1 && tick > 0f)
                {
                    int r1 = trackRows[trackRowStart[tr] + 1] * stride;
                    Vector3 v0 = turn * ((new Vector3(rows[r1 + fPos], rows[r1 + fPos + 1], rows[r1 + fPos + 2]) - p0) * (speedMultiplier / tick));
                    float run = simDrag > 1e-4f ? (1f - Mathf.Exp(-simDrag * elapsed)) / simDrag : elapsed;
                    off = Vector3.Lerp(off, v0 * run + simGravity * (0.5f * elapsed * elapsed), sim);
                }
                p = p0 + off;
            }
            if (useShape)
            {
                if (positionScale != Vector3.one) p = Vector3.Scale(p, positionScale);
                p += shapeOffset;
                if (shapeRandomize > 0f)
                    p += new Vector3(Rand(Hash(seed, tr, 51, 0)) - 0.5f, Rand(Hash(seed, tr, 51, 1)) - 0.5f, Rand(Hash(seed, tr, 51, 2)) - 0.5f) * (2f * shapeRandomize);
            }
            Vector3 vel = useVelocity ? extraVelocity : Vector3.zero;
            Vector3 acc = useForce ? extraAcceleration : Vector3.zero;
            if (gravityModifier != 0f) acc += w2l.MultiplyVector(Physics.gravity) * gravityModifier;
            if (vel != Vector3.zero || acc != Vector3.zero) p += vel * elapsed + acc * (0.5f * elapsed * elapsed);
            if (useNoise && noiseStrength != 0f)
            {
                float o = Rand(Hash(seed, tr, 7, 0)) * 100f;
                float x = elapsed * noiseFrequency;
                p += new Vector3(Mathf.PerlinNoise(o, x) - 0.5f, Mathf.PerlinNoise(o + 31.7f, x) - 0.5f, Mathf.PerlinNoise(o + 63.3f, x) - 0.5f) * (2f * noiseStrength);
            }
            if (copy > 0 && copyJitter > 0f)
                p += new Vector3(Rand(Hash(seed, tr, kk, copy + 101)) - 0.5f, Rand(Hash(seed, tr, kk, copy + 202)) - 0.5f, Rand(Hash(seed, tr, kk, copy + 303)) - 0.5f) * (2f * copyJitter);
            work[d + fPos] = p.x; work[d + fPos + 1] = p.y; work[d + fPos + 2] = p.z;
        }
        // spin
        if (flipRotation > 0f && kind == "Sprite" && fRot >= 0 && quatAt < 0 && Rand(Hash(seed, tr, 61, 0)) < flipRotation) work[d + fRot] = -work[d + fRot];
        float rotAdd = useRotation ? spinRate * elapsed : 0f;
        if (startRotationMin != 0f || startRotationMax != 0f) rotAdd += Mathf.Lerp(startRotationMin, startRotationMax, Rand(Hash(seed, tr, 21, 0)));
        if (rotAdd != 0f)
        {
            if (kind == "Sprite" && fRot >= 0 && quatAt < 0) work[d + fRot] += rotAdd * Mathf.Deg2Rad;
            else if (quatAt >= 0 && kind == "Mesh")
            {
                Quaternion q0 = new Quaternion(work[d + quatAt], work[d + quatAt + 1], work[d + quatAt + 2], work[d + quatAt + 3]);
                Vector3 ax = spinAxis.sqrMagnitude > 1e-8f ? spinAxis.normalized : Vector3.up;
                Quaternion q1 = Quaternion.AngleAxis(rotAdd, ax) * q0;
                work[d + quatAt] = q1.x; work[d + quatAt + 1] = q1.y; work[d + quatAt + 2] = q1.z; work[d + quatAt + 3] = q1.w;
            }
        }
        // ribbons of different cycles or copies must not be joined up
        if (fRibbon >= 0) work[d + fRibbon] += ((cycleNo & 63) * 4096) + copy * 262144;
        // world space: where this object was when the particle was born, seen from where it is now
        if (worldSpace)
        {
            Matrix4x4 conv = w2l * SpawnMatrix(birthClock);
            if (fPos >= 0)
            {
                Vector3 p = conv.MultiplyPoint3x4(new Vector3(work[d + fPos], work[d + fPos + 1], work[d + fPos + 2]));
                work[d + fPos] = p.x; work[d + fPos + 1] = p.y; work[d + fPos + 2] = p.z;
            }
            ConvDirection(d, fVel, ref conv);
            ConvDirection(d, fFace, ref conv);
            ConvDirection(d, fAlign, ref conv);
            ConvDirection(d, fSide, ref conv);
            if (quatAt >= 0)
            {
                Quaternion q = conv.rotation * new Quaternion(work[d + quatAt], work[d + quatAt + 1], work[d + quatAt + 2], work[d + quatAt + 3]);
                work[d + quatAt] = q.x; work[d + quatAt + 1] = q.y; work[d + quatAt + 2] = q.z; work[d + quatAt + 3] = q.w;
            }
        }
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
        color = Tint(new Vector4(Get(row, fc, 0, 1f), Get(row, fc, 1, 1f), Get(row, fc, 2, 1f), Get(row, fc, 3, 1f)));
        a = Vector4.zero;
        b = Vector4.zero;
        if (dpComps != null)
        {
            for (int k = 0; k < dpComps.Length && k < 8; k++)
            {
                float v = Get(row, Field("dp" + dpComps[k]), 0, 0f);
                if (useCustomData && customDataMultiply != null && customDataAdd != null && k < customDataMultiply.Length && k < customDataAdd.Length) v = v * customDataMultiply[k] + customDataAdd[k];
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
        order.Sort((x, y) =>
        {
            int cmp = frib >= 0 ? Get(x, frib, 0, 0f).CompareTo(Get(y, frib, 0, 0f)) : 0;
            return cmp != 0 ? cmp : Get(x, key, 0, 0f).CompareTo(Get(y, key, 0, 0f));
        });
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
            float width = Mathf.Lerp(Get(ra, fw, 0, 0.01f), Get(rb, fw, 0, 0.01f), t) * sizeScale;
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
            int r = RowAt(first, i);
            Vector3 p = new Vector3(Get(r, fp, 0, 0f), Get(r, fp, 1, 0f), Get(r, fp, 2, 0f));
            float w = Get(r, fs, 0, 0.1f) * 0.5f * sizeScale, h = Get(r, fs, 1, 0.1f) * 0.5f * sizeScale;
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
            else if (facingMode == 2 && toCam.sqrMagnitude > 1e-12f)
            {
                // the sprite faces the camera's position, not its plane
                nrm = toCam.normalized;
                Vector3 bx = Vector3.Cross(nrm, up);
                if (bx.sqrMagnitude < 1e-10f) bx = right;
                bx.Normalize();
                Vector3 by = Vector3.Cross(bx, nrm);
                ax = bx * cs + by * sn;
                ay = by * cs - bx * sn;
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
                int frame = (useTextureSheet ? SheetFrame(r, fsub, total) : Mathf.FloorToInt(Get(r, fsub, 0, 0f))) % total;
                if (frame < 0) frame += total;
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

    int SheetFrame(int r, int fsub, int total)
    {
        if (sheetMode == 2) return sheetFixedFrame;
        if (sheetMode == 3)
        {
            int fr = Field("random");
            float rv = fr >= 0 ? Get(r, fr, 0, 0.5f) : 0.5f;
            return Mathf.FloorToInt(Mathf.Clamp01(rv) * total * 0.9999f) + sheetStartFrame;
        }
        if (sheetMode == 1)
        {
            int fa = Field("age");
            float age = fa >= 0 ? Mathf.Clamp01(Get(r, fa, 0, 0f)) : 0f;
            float x = age * Mathf.Max(0.0001f, sheetCycles);
            x = (x > 0f && x % 1f == 0f) ? 1f : Mathf.Repeat(x, 1f);
            float u = sheetFrameOverTime != null ? Mathf.Clamp01(sheetFrameOverTime.Evaluate(x)) : x;
            return Mathf.Min(total - 1, Mathf.FloorToInt(u * total)) + sheetStartFrame;
        }
        return Mathf.FloorToInt(Get(r, Field("subimage"), 0, 0f)) + sheetStartFrame;
    }

    // ---- meshes: the renderer's mesh copied to every particle (its rotation, scale and position), turned as the renderer says
    class MeshSource
    {
        public Quaternion rot = Quaternion.identity;
        public Vector3 scale = Vector3.one;
        public Vector3[] verts, normals;
        public Vector4[] tangents;
        public Vector2[] uvs, uvs2;
        public Color[] colors;
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
                Debug.LogWarning("Baked Particle System: mesh " + m.name + " is not readable (turn Read/Write on in its import settings), " + name + " draws nothing for it");
                continue;
            }
            var src = new MeshSource();
            if (meshRotations != null && i < meshRotations.Length)
            {
                Quaternion mr = meshRotations[i];
                if (!(mr.x == 0f && mr.y == 0f && mr.z == 0f && mr.w == 0f)) src.rot = mr;
            }
            if (meshScales != null && i < meshScales.Length && meshScales[i] != Vector3.zero) src.scale = meshScales[i];
            src.verts = m.vertices;
            src.normals = m.normals;
            var t = new List<Vector4>();
            m.GetTangents(t);
            src.tangents = t.ToArray();
            src.uvs = m.uv;
            src.uvs2 = m.uv2;
            src.colors = m.colors;
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
            int r = RowAt(first, i);
            int mi = Mathf.Clamp(Mathf.RoundToInt(Get(r, fi, 0, 0f)), 0, sources.Length - 1);
            MeshSource src = sources[mi];
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
            // the mesh's own rotation and scale come first (in the mesh's own axes), then the particle's orientation and scale
            q = q * src.rot;
            Vector3 sc = Vector3.Scale(new Vector3(Get(r, fs, 0, 1f), Get(r, fs, 1, 1f), Get(r, fs, 2, 1f)), src.scale) * sizeScale;
            if (useMeshEdits)
            {
                if (meshRotationOffset != null && mi < meshRotationOffset.Length) q = q * Quaternion.Euler(meshRotationOffset[mi]);
                if (meshScaleMultiply != null && mi < meshScaleMultiply.Length) sc = Vector3.Scale(sc, meshScaleMultiply[mi]);
            }
            int baseIndex = verts.Count;
            for (int v = 0; v < src.verts.Length; v++)
            {
                Vector3 pos = p + q * Vector3.Scale(src.verts[v], sc);
                // the engine turns a mesh particle's normals by its scale, not by the scale's inverse
                Vector3 nrm = v < src.normals.Length ? (q * Vector3.Scale(src.normals[v], sc)).normalized : Vector3.up;
                Vector4 t4 = v < src.tangents.Length ? src.tangents[v] : new Vector4(1f, 0f, 0f, 1f);
                Vector3 tw = q * new Vector3(t4.x, t4.y, t4.z);
                AddVertex(pos, nrm, new Vector4(tw.x, tw.y, tw.z, t4.w), v < src.uvs.Length ? src.uvs[v] : Vector2.zero, r);
                // the mesh's own vertex colour (materials read it as a mask, separate from the particle colour in UV1) and its
                // second UV set (the shader reads it from UV0 zw while the streams are on)
                if (v < src.colors.Length) cols[cols.Count - 1] = src.colors[v];
                if (v < src.uvs2.Length) { Vector4 u = uv0[uv0.Count - 1]; uv0[uv0.Count - 1] = new Vector4(u.x, u.y, src.uvs2[v].x, src.uvs2[v].y); }
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
            float w = Mathf.Max(Get(r, fs, 0, 1f) * 0.5f * sizeScale, 1e-4f), d = Mathf.Max(Get(r, fs, 2, 1f) * 0.5f * sizeScale, 1e-4f);
            float h = Get(r, fs, 1, 0f) * 0.5f * sizeScale;
            Vector3 rx = q * Vector3.right * w, rz = q * Vector3.forward * d;
            Vector3 nrm = q * Vector3.up;
            if (projecting)
            {
                // how deep it projects: the size's own depth, else as far as it is wide
                if (h < 1e-4f) h = Mathf.Min(w, d);
                Vector3 ax = q * Vector3.right / w, ay = nrm / h, az = q * Vector3.forward / d;
                Vector4 c;
                int fc = Field("color");
                c = Tint(new Vector4(Get(r, fc, 0, 1f), Get(r, fc, 1, 1f), Get(r, fc, 2, 1f), Get(r, fc, 3, 1f)));
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
        int fp = Field("pos"), fc = Field("color"), fr = Field("radius"), fe = Field("exponent");
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
            Vector4 lc = Tint(new Vector4(Get(r, fc, 0, 1f), Get(r, fc, 1, 1f), Get(r, fc, 2, 1f), Get(r, fc, 3, 1f)));
            float cr = Mathf.Max(lc.x, 0f), cg = Mathf.Max(lc.y, 0f), cb = Mathf.Max(lc.z, 0f);
            float m = Mathf.Max(cr, Mathf.Max(cg, cb));
            float alpha = Mathf.Clamp01(lc.w);
            if (m <= 1e-4f || alpha <= 0f) { l.enabled = false; continue; }
            Color lcol = new Color(cr / m, cg / m, cb / m);
            // Light.color is read in gamma space in a Linear project, as it is in Unreal's linear colour; a Gamma project reads it as given
            l.color = QualitySettings.activeColorSpace == ColorSpace.Linear ? lcol.gamma : lcol;
            l.intensity = m * lightScale * alpha;
            l.shadows = (LightShadows)Mathf.Clamp(lightShadows, 0, 2);
            l.shadowStrength = lightShadowStrength;
            l.renderMode = (LightRenderMode)Mathf.Clamp(lightRenderMode, 0, 2);
            float ex = fe >= 0 ? Get(r, fe, 0, 1f) : 1f;
            float reach = ex > 0.01f ? Mathf.Sqrt(1f - Mathf.Pow(0.1f, 1f / ex)) / 0.9486833f : 1f;
            l.range = Mathf.Max(0.01f, Get(r, fr, 0, 1f) * radiusScale * Mathf.Lerp(1f, Mathf.Clamp(reach, 0.25f, 1.1f), exponentToRange));
            l.enabled = true;
        }
    }
}

// The converted Niagara systems were made for a character's body or skin. Drop the body (or a bone) into Body, or type its
// name, and the whole system follows it: it is parented to it at zero, so it sits where the Unreal system sat on the rig.
[ExecuteAlways]
public class PorterBodyAnchor : MonoBehaviour
{
    public Transform body;
    public string bodyName = "";
    public Vector3 offset;
    public Vector3 eulerOffset;
    public bool keepWorldScale = true;

    void OnEnable()
    {
        Attach();
    }

    void OnValidate()
    {
        Attach();
    }

    void LateUpdate()
    {
        if (body == null && !string.IsNullOrEmpty(bodyName)) Attach();
    }

    [ContextMenu("Attach To Body")]
    public void Attach()
    {
        if (body == null && !string.IsNullOrEmpty(bodyName))
        {
            var found = GameObject.Find(bodyName);
            if (found != null) body = found.transform;
        }
        if (body == null || transform.parent == body) return;
        Vector3 s = transform.lossyScale;
        transform.SetParent(body, false);
        transform.localPosition = offset;
        transform.localRotation = Quaternion.Euler(eulerOffset);
        if (keepWorldScale && body.lossyScale != Vector3.zero)
            transform.localScale = new Vector3(s.x / body.lossyScale.x, s.y / body.lossyScale.y, s.z / body.lossyScale.z);
    }
}

#if UNITY_EDITOR
// A Particle System style inspector for the baked replay: playback bar, the Main module, then modules with on/off boxes in the
// Unity order, the Renderer, and the raw bake data under Data. It only shows and edits what the replay really controls.
[CustomEditor(typeof(PorterBakedParticleSystem), true)]
public class PorterBakedParticleSystemInspector : Editor
{
    static bool fMain = true, fEmission = true, fShape, fVel, fForce, fColorLife, fSizeLife, fOpacityLife, fRotLife, fNoise, fSim, fSpeedColor, fSpeedSize, fCustom, fSheet, fMeshEdit, fRenderer = true, fData;

    static readonly string[] SpriteFacing = { "Face the camera", "Face the particle's own direction", "Face the camera's position" };
    static readonly string[] SortModes = { "None (file order)", "View depth (far first)", "View distance (far first)", "Sort key, low first", "Sort key, high first" };
    static readonly string[] MeshFacing = { "Mesh rotation only", "X along the velocity", "X to the camera" };
    static readonly string[] RibbonFacing = { "Width across the view", "Width across the particles' facing", "Width along the particles' side vector" };
    static readonly string[] RibbonShape = { "Plane", "Crossed planes", "Tube" };
    static readonly string[] SheetModes = { "As baked", "Over lifetime", "Fixed frame", "Random per particle" };
    static readonly string[] ShadowModes = { "None", "Hard", "Soft" };
    static readonly string[] LightRenderModes = { "Auto", "Important", "Not important" };
    static readonly string[] CastModes = { "Off", "On", "Two sided", "Shadows only" };
    static readonly string[] ProbeModes = { "Off", "Blend probes", "Use proxy volume", "Custom provided" };
    static readonly string[] MotionModes = { "Camera motion only", "Per object motion", "Force no motion" };

    void SizeArray(string prop, int n, Vector3 fill)
    {
        var a = P(prop);
        if (a.arraySize == n) return;
        int old = a.arraySize;
        a.arraySize = n;
        for (int i = old; i < n; i++) a.GetArrayElementAtIndex(i).vector3Value = fill;
    }

    public override bool RequiresConstantRepaint()
    {
        var t = target as PorterBakedParticleSystem;
        return t != null && t.IsPlaying && !t.IsPaused;
    }

    SerializedProperty P(string n)
    {
        return serializedObject.FindProperty(n);
    }

    // a Particle System style module bar: an arrow, an optional on/off box and the title on a darker strip
    static bool Header(bool open, string title, SerializedProperty toggle)
    {
        Rect r = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
        Color strip = EditorGUIUtility.isProSkin ? new Color(0.20f, 0.20f, 0.20f) : new Color(0.74f, 0.74f, 0.74f);
        EditorGUI.DrawRect(new Rect(r.x, r.y + 1f, r.width, r.height - 1f), strip);
        var arrow = new Rect(r.x + 4f, r.y + 2f, 14f, 16f);
        var box = new Rect(r.x + 20f, r.y + 2f, 16f, 16f);
        float titleX = r.x + (toggle != null ? 40f : 20f);
        if (toggle != null) toggle.boolValue = EditorGUI.Toggle(box, toggle.boolValue);
        open = EditorGUI.Foldout(arrow, open, GUIContent.none, false);
        EditorGUI.LabelField(new Rect(titleX, r.y + 1f, r.width - titleX, 18f), title, EditorStyles.boldLabel);
        Event ev = Event.current;
        if (ev.type == EventType.MouseDown && ev.button == 0 && r.Contains(ev.mousePosition) && (toggle == null || !box.Contains(ev.mousePosition)))
        {
            open = !open;
            ev.Use();
        }
        return open;
    }

    static void BeginBody(SerializedProperty toggle)
    {
        GUILayout.Space(3f);
        EditorGUI.BeginDisabledGroup(toggle != null && !toggle.boolValue);
        EditorGUI.indentLevel++;
    }

    static void EndBody()
    {
        EditorGUI.indentLevel--;
        EditorGUI.EndDisabledGroup();
        GUILayout.Space(5f);
    }

    void Popup(string prop, string label, string[] names)
    {
        var p = P(prop);
        p.intValue = EditorGUILayout.Popup(label, Mathf.Clamp(p.intValue, 0, names.Length - 1), names);
    }

    void NonNegative(string prop, string label, string tip)
    {
        var p = P(prop);
        EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        if (p.floatValue < 0f) p.floatValue = 0f;
    }

    void Pair(string a, string b, string label, string tip)
    {
        Rect r = EditorGUILayout.GetControlRect();
        r = EditorGUI.PrefixLabel(r, new GUIContent(label, tip));
        float half = (r.width - 4f) * 0.5f;
        int indent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        EditorGUI.PropertyField(new Rect(r.x, r.y, half, r.height), P(a), GUIContent.none);
        EditorGUI.PropertyField(new Rect(r.x + half + 4f, r.y, half, r.height), P(b), GUIContent.none);
        EditorGUI.indentLevel = indent;
    }

    void Field(string prop, string label, string tip)
    {
        EditorGUILayout.PropertyField(P(prop), new GUIContent(label, tip));
    }

    public override void OnInspectorGUI()
    {
        var t = (PorterBakedParticleSystem)target;
        serializedObject.Update();
        string kind = t.kind;
        bool tr = t.Tracked;
        bool hasSize = kind != "Light";
        bool hasRot = kind == "Sprite" || kind == "Mesh" || kind == "Decal";

        EditorGUILayout.BeginHorizontal();
        string playLabel = !t.IsPlaying ? "Play" : (t.IsPaused ? "Resume" : "Pause");
        if (GUILayout.Button(playLabel)) { if (!t.IsPlaying) t.Play(); else if (t.IsPaused) t.Resume(); else t.Pause(); }
        if (GUILayout.Button("Restart")) t.Restart();
        if (GUILayout.Button("Stop")) t.Stop();
        EditorGUILayout.EndHorizontal();
        if (t.TotalTicks > 0)
        {
            EditorGUI.BeginChangeCheck();
            float now = EditorGUILayout.Slider("Playback Time", t.PlayTime, 0f, t.Duration);
            if (EditorGUI.EndChangeCheck()) t.Scrub(now);
            EditorGUILayout.LabelField("Particles", t.CurrentParticles + " (max " + t.PeakParticles + ")");
        }
        else
        {
            EditorGUILayout.HelpBox("No baked data. Assign it under Data, then press Play.", MessageType.Info);
        }
        var root = t.GetComponentInParent<PorterParticleController>();
        if (root != null && (root.masterSpeed != 1f || root.masterRate != 1f || root.masterSize != 1f || root.masterIntensity != 1f || root.masterOpacity != 1f || root.masterTint != Color.white))
            EditorGUILayout.HelpBox("The root controller scales this emitter. Change it on " + root.name + ".", MessageType.None);
        GUILayout.Space(2f);

        fMain = Header(fMain, "Particle System", null);
        if (fMain)
        {
            BeginBody(null);
            if (tr && t.TotalTicks > 0) EditorGUILayout.LabelField("Duration", t.Duration.ToString("0.00") + " s");
            Field("loop", "Looping", "");
            if (tr) Field("prewarm", "Prewarm", "Looping only.");
            NonNegative("startDelay", "Start Delay", "");
            if (tr)
            {
                NonNegative("lifetimeScale", "Start Lifetime", "Multiplier. 1 is as baked.");
                var lr = P("lifetimeRandom");
                lr.floatValue = EditorGUILayout.Slider(new GUIContent("Lifetime Random", "Each particle's life varies by up to this fraction."), lr.floatValue, 0f, 0.9f);
                Field("speedMultiplier", "Start Speed", "Multiplier. 1 is as baked.");
                if (hasSize)
                {
                    Field("start3DSize", "3D Start Size", "");
                    if (P("start3DSize").boolValue) Field("startSize3D", "Start Size", "");
                    else Pair("startSizeMin", "startSizeMax", "Start Size", "Random multiplier between the two. 1 is as baked.");
                }
                if (hasRot) Pair("startRotationMin", "startRotationMax", "Start Rotation", "Random angle (deg) between the two.");
                if (kind == "Sprite") Field("flipRotation", "Flip Rotation", "Share of sprites turned the other way.");
                Field("directionRotation", "Direction Rotation", "Degrees. Turns every path about its birth point.");
                NonNegative("directionSpread", "Direction Spread", "Degrees.");
            }
            EditorGUILayout.PropertyField(P("colorTint"), new GUIContent("Start Color"));
            NonNegative("colorIntensity", "Color Intensity", "Brightness multiplier. Unreal emissive goes above 1.");
            var op = P("opacity");
            op.floatValue = EditorGUILayout.Slider("Opacity", op.floatValue, 0f, 1f);
            if (hasSize) NonNegative("sizeScale", "Size Scale", "");
            if (tr)
            {
                Field("gravityModifier", "Gravity Modifier", "Multiplies Physics.gravity.");
                var ws = P("worldSpace");
                ws.boolValue = EditorGUILayout.Popup("Simulation Space", ws.boolValue ? 1 : 0, new[] { "Local", "World" }) == 1;
            }
            NonNegative("speed", "Simulation Speed", "");
            Field("playOnAwake", "Play On Awake", "");
            if (tr)
            {
                Field("maxParticles", "Max Particles", "0 for no limit.");
                if (P("maxParticles").intValue < 0) P("maxParticles").intValue = 0;
                EditorGUILayout.BeginHorizontal();
                Field("seed", "Random Seed", "");
                if (GUILayout.Button("Randomize", GUILayout.Width(72f))) P("seed").intValue = UnityEngine.Random.Range(1, 1000000);
                EditorGUILayout.EndHorizontal();
            }
            EndBody();
        }

        if (tr)
        {
            fEmission = Header(fEmission, "Emission", null);
            if (fEmission)
            {
                BeginBody(null);
                NonNegative("rateMultiplier", "Rate over Time", "Multiplier. Under 1 thins the particles, over 1 adds copies.");
                if (P("rateMultiplier").floatValue > 1f) NonNegative("copyJitter", "Copy Jitter (m)", "");
                EndBody();
            }

            var useShape = P("useShape");
            fShape = Header(fShape, "Shape", useShape);
            if (fShape)
            {
                BeginBody(useShape);
                Field("positionScale", "Scale", "");
                Field("shapeOffset", "Position (m)", "");
                NonNegative("shapeRandomize", "Randomize Position (m)", "");
                EndBody();
            }

            var useVel = P("useVelocity");
            fVel = Header(fVel, "Velocity over Lifetime", useVel);
            if (fVel) { BeginBody(useVel); Field("extraVelocity", "Linear (m/s)", ""); EndBody(); }

            var useForce = P("useForce");
            fForce = Header(fForce, "Force over Lifetime", useForce);
            if (fForce) { BeginBody(useForce); Field("extraAcceleration", "Force (m/s\u00b2)", ""); EndBody(); }

            var useColor = P("useColorOverLifetime");
            fColorLife = Header(fColorLife, "Color over Lifetime", useColor);
            if (fColorLife) { BeginBody(useColor); Field("colorOverLifetime", "Color", ""); EndBody(); }

            var useOpacity = P("useOpacityOverLifetime");
            fOpacityLife = Header(fOpacityLife, "Opacity over Lifetime", useOpacity);
            if (fOpacityLife) { BeginBody(useOpacity); Field("opacityOverLifetime", "Opacity", ""); EndBody(); }

            var useColorSpeed = P("useColorBySpeed");
            fSpeedColor = Header(fSpeedColor, "Color by Speed", useColorSpeed);
            if (fSpeedColor)
            {
                BeginBody(useColorSpeed);
                Field("colorBySpeed", "Color", "");
                Field("colorSpeedRange", "Speed Range (m/s)", "");
                EndBody();
            }

            if (hasSize)
            {
                var useSize = P("useSizeOverLifetime");
                fSizeLife = Header(fSizeLife, "Size over Lifetime", useSize);
                if (fSizeLife) { BeginBody(useSize); Field("sizeOverLifetime", "Size", ""); EndBody(); }

                var useSizeSpeed = P("useSizeBySpeed");
                fSpeedSize = Header(fSpeedSize, "Size by Speed", useSizeSpeed);
                if (fSpeedSize)
                {
                    BeginBody(useSizeSpeed);
                    Field("sizeBySpeed", "Size", "");
                    Field("sizeSpeedRange", "Speed Range (m/s)", "");
                    EndBody();
                }
            }

            if (hasRot)
            {
                var useRot = P("useRotation");
                fRotLife = Header(fRotLife, "Rotation over Lifetime", useRot);
                if (fRotLife)
                {
                    BeginBody(useRot);
                    Field("spinRate", "Angular Velocity (deg/s)", "");
                    if (kind == "Mesh") Field("spinAxis", "Axis", "");
                    EndBody();
                }
            }

            var useNoise = P("useNoise");
            fNoise = Header(fNoise, "Noise", useNoise);
            if (fNoise)
            {
                BeginBody(useNoise);
                NonNegative("noiseStrength", "Strength (m)", "");
                NonNegative("noiseFrequency", "Frequency", "");
                EndBody();
            }

            var useSim = P("useSimulation");
            fSim = Header(fSim, "Simulation", useSim);
            if (fSim)
            {
                BeginBody(useSim);
                Field("simulate", "Blend", "0 follows the recording. 1 moves each particle from its baked birth under the gravity and drag below. Scripted motion is lost at 1.");
                Field("simGravity", "Gravity (m/s\u00b2)", "");
                NonNegative("simDrag", "Drag (per second)", "");
                EndBody();
            }
        }

        if (kind == "Sprite")
        {
            var useSheet = P("useTextureSheet");
            fSheet = Header(fSheet, "Texture Sheet Animation", useSheet);
            if (fSheet)
            {
                BeginBody(useSheet);
                Field("subX", "Tiles X", "");
                Field("subY", "Tiles Y", "");
                Popup("sheetMode", "Frame", SheetModes);
                int sm = P("sheetMode").intValue;
                if (sm == 1)
                {
                    Field("sheetFrameOverTime", "Frame over Time", "");
                    Field("sheetCycles", "Cycles", "");
                }
                if (sm == 2) Field("sheetFixedFrame", "Frame", "");
                else Field("sheetStartFrame", "Start Frame", "");
                EndBody();
            }
        }

        if (t.dpComps != null && t.dpComps.Length > 0)
        {
            var useCustom = P("useCustomData");
            fCustom = Header(fCustom, "Custom Data", useCustom);
            if (fCustom)
            {
                BeginBody(useCustom);
                var mul = P("customDataMultiply");
                var add = P("customDataAdd");
                if (mul.arraySize != 8) { int o = mul.arraySize; mul.arraySize = 8; for (int i = o; i < 8; i++) mul.GetArrayElementAtIndex(i).floatValue = 1f; }
                if (add.arraySize != 8) add.arraySize = 8;
                EditorGUILayout.LabelField("Dynamic Parameters. Value = baked x Multiply + Add.", EditorStyles.miniLabel);
                for (int k = 0; k < t.dpComps.Length && k < 8; k++)
                {
                    Rect r = EditorGUILayout.GetControlRect();
                    r = EditorGUI.PrefixLabel(r, new GUIContent("Parameter " + t.dpComps[k], "Left: Multiply. Right: Add."));
                    float half = (r.width - 4f) * 0.5f;
                    int indent = EditorGUI.indentLevel;
                    EditorGUI.indentLevel = 0;
                    EditorGUI.PropertyField(new Rect(r.x, r.y, half, r.height), mul.GetArrayElementAtIndex(k), GUIContent.none);
                    EditorGUI.PropertyField(new Rect(r.x + half + 4f, r.y, half, r.height), add.GetArrayElementAtIndex(k), GUIContent.none);
                    EditorGUI.indentLevel = indent;
                }
                EndBody();
            }
        }

        if (kind == "Mesh" && t.meshes != null && t.meshes.Length > 0)
        {
            var useMeshEdit = P("useMeshEdits");
            fMeshEdit = Header(fMeshEdit, "Mesh Overrides", useMeshEdit);
            if (fMeshEdit)
            {
                BeginBody(useMeshEdit);
                SizeArray("meshScaleMultiply", t.meshes.Length, Vector3.one);
                SizeArray("meshRotationOffset", t.meshes.Length, Vector3.zero);
                for (int i = 0; i < t.meshes.Length; i++)
                {
                    EditorGUILayout.LabelField(t.meshes[i] != null ? t.meshes[i].name : "Mesh " + i, EditorStyles.miniBoldLabel);
                    EditorGUILayout.PropertyField(P("meshScaleMultiply").GetArrayElementAtIndex(i), new GUIContent("Scale"));
                    EditorGUILayout.PropertyField(P("meshRotationOffset").GetArrayElementAtIndex(i), new GUIContent("Rotation (deg)"));
                }
                EndBody();
            }
        }

        fRenderer = Header(fRenderer, "Renderer (" + kind + ")", null);
        if (fRenderer)
        {
            BeginBody(null);
            if (kind == "Light")
            {
                Field("lightScale", "Intensity", "Unity intensity per unit of Unreal brightness.");
                Field("radiusScale", "Range Scale", "");
                Field("exponentToRange", "Falloff Effect", "How much the baked light exponent changes the range. 0 ignores it.");
                Popup("lightShadows", "Shadows", ShadowModes);
                if (P("lightShadows").intValue > 0) Field("lightShadowStrength", "Shadow Strength", "");
                Popup("lightRenderMode", "Render Mode", LightRenderModes);
            }
            else
            {
                if (kind == "Mesh")
                {
                    EditorGUILayout.PropertyField(P("meshes"), new GUIContent("Meshes"), true);
                    EditorGUILayout.PropertyField(P("materials"), new GUIContent("Materials"), true);
                    Popup("facingMode", "Facing", MeshFacing);
                }
                else
                {
                    Field("material", "Material", "");
                    if (kind == "Sprite")
                    {
                        Popup("facingMode", "Facing", SpriteFacing);
                        Field("lengthwise", "Length Along Velocity", "");
                        Field("pivotX", "Pivot X", "");
                        Field("pivotY", "Pivot Y", "");
                    }
                    else if (kind == "Ribbon")
                    {
                        Popup("facingMode", "Facing", RibbonFacing);
                        Popup("ribbonShape", "Shape", RibbonShape);
                        if (P("ribbonShape").intValue > 0) Field("ribbonSides", P("ribbonShape").intValue == 2 ? "Tube Sides" : "Planes", "");
                    }
                    else if (kind == "Decal")
                    {
                        Field("projectDecals", "Project Onto Scene", "Needs the Porter decal shader and a depth texture; otherwise each decal is a flat quad.");
                        Field("decalShader", "Decal Shader", "");
                    }
                }
                if (kind == "Sprite" || kind == "Mesh") Popup("sortMode", "Sort Mode", SortModes);
                Popup("shadowCasting", "Cast Shadows", CastModes);
                Field("receiveShadows", "Receive Shadows", "");
                Field("sortingOrder", "Sorting Order", "");
                Popup("lightProbes", "Light Probes", ProbeModes);
                Popup("motionVectors", "Motion Vectors", MotionModes);
            }
            EndBody();
        }

        fData = Header(fData, "Data", null);
        if (fData)
        {
            BeginBody(null);
            Field("data", "Baked Replay", "");
            EndBody();
        }

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
