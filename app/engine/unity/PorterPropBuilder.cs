#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PorterPropBuilder
{
    const float CmToM = 0.01f;
    const float LightIntensityScale = 1f;

    static List<string> log;
    static int missing;
    static bool loud;
    static List<string> missingNames = new List<string>();
    static string specFolder;
    static List<bool> hasKids;

    [MenuItem("Tools/Porter Build Prop Blueprint Prefabs")]
    static void Menu()
    {
        int made = 0;
        foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".propspec.json")) continue;
            if (Build(path, true, true) != null) made++;
        }
        Debug.Log("Porter: built " + made + " prop blueprint prefab(s)");
    }

    public static GameObject Build(string specPath, bool force, bool allowPartial)
    {
        log = new List<string>();
        missing = 0;
        loud = allowPartial;
        missingNames.Clear();
        TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(specPath);
        if (asset == null) return null;
        var spec = D(PorterMiniJson.Parse(asset.text));
        string bpName = S(spec["blueprint"]);
        specFolder = Path.GetDirectoryName(specPath).Replace('\\', '/');
        string prefabPath = specFolder + "/" + bpName + ".prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
        {
            if (!force) return null;
            prefabPath = AssetDatabase.GenerateUniqueAssetPath(prefabPath);
        }
        var nodes = L(spec["nodes"]);
        hasKids = new List<bool>();
        for (int i = 0; i < nodes.Count; i++) hasKids.Add(false);
        for (int i = 0; i < nodes.Count; i++)
        {
            int pi = (int)N(D(nodes[i])["parent"], -1);
            if (pi >= 0) hasKids[pi] = true;
        }
        var objs = new List<GameObject>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = D(nodes[i]);
            int parent = (int)N(n["parent"], -1);
            var go = new GameObject(S(n["name"]));
            if (parent >= 0) go.transform.SetParent(objs[parent].transform, false);
            ApplyTransform(go.transform, n);
            objs.Add(go);
            string kind = S(n["kind"]);
            if (kind == "mesh") BuildMesh(go, n, hasKids[i]);
            else if (kind == "light") BuildLight(go, n);
            else if (kind == "audio") BuildAudio(go, n);
            else if (kind == "particles") BuildParticles(go, n);
        }
        if (missing > 0 && !allowPartial)
        {
            UnityEngine.Object.DestroyImmediate(objs[0]);
            string wait = "PorterPropWait:" + specPath;
            if (!SessionState.GetBool(wait, false))
            {
                SessionState.SetBool(wait, true);
                Debug.Log("Porter: " + bpName + " is waiting for " + string.Join(", ", missingNames) + " to import. If they are not coming, use Tools/Porter Build Prop Blueprint Prefabs to build it anyway");
            }
            return null;
        }
        var prefab = PrefabUtility.SaveAsPrefabAsset(objs[0], prefabPath);
        UnityEngine.Object.DestroyImmediate(objs[0]);
        AssetDatabase.SaveAssets();
        var sb = new StringBuilder();
        sb.AppendLine("Porter: saved " + prefabPath);
        foreach (var l in log) sb.AppendLine(l);
        if (spec.ContainsKey("report"))
            foreach (var r in L(spec["report"])) sb.AppendLine("[extractor] " + S(r));
        Debug.Log(sb.ToString());
        return prefab;
    }

    static Vector3 P(Vector3 ue)
    {
        return new Vector3(ue.x, ue.z, ue.y);
    }

    static void ApplyTransform(Transform t, Dictionary<string, object> n)
    {
        var loc = V3(n["loc"]);
        var rot = V3(n["rot"]);
        var scl = V3(n["scale"]);
        t.localPosition = P(loc) * CmToM;
        t.localRotation = PorterBlueprintSetup.RotatorToQuaternion(rot.x, rot.y, rot.z);
        t.localScale = P(scl);
    }

    static void BuildMesh(GameObject go, Dictionary<string, object> n, bool hasChildren)
    {
        if (PorterBlueprintSetup.MeshArrivesMirroredX)
        {
            if (hasChildren)
            {
                var holder = new GameObject("Mesh");
                holder.transform.SetParent(go.transform, false);
                holder.transform.localScale = new Vector3(-1f, 1f, 1f);
                go = holder;
            }
            else
            {
                Vector3 ls = go.transform.localScale;
                go.transform.localScale = new Vector3(-ls.x, ls.y, ls.z);
            }
        }
        string meshName = S(n["mesh"]);
        var mesh = FindAsset<Mesh>(meshName);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        if (mesh == null) Miss("mesh", meshName, "the .fbx in the Meshes folder");
        else mf.sharedMesh = mesh;
        var slots = L(n["slots"]);
        var mats = new Material[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            string mn = S(D(slots[i])["material"]);
            mats[i] = FindMaterial(mn);
            if (mats[i] == null && !string.IsNullOrEmpty(mn)) Miss("material", mn, "its converted shader");
        }
        mr.sharedMaterials = mats;
        if (mesh != null && mesh.subMeshCount != slots.Count)
            log.Add("CHECK mesh '" + meshName + "' has " + mesh.subMeshCount + " submeshes but the blueprint lists " + slots.Count + " material slots");
    }

    static void Miss(string what, string name, string hint)
    {
        missing++;
        missingNames.Add(name);
        if (loud) Debug.LogWarning("Porter: " + what + " " + name + " was not found in the project (" + hint + "), so its slot stays empty");
        log.Add("MISSING " + what + " '" + name + "'");
    }

    static void BuildLight(GameObject go, Dictionary<string, object> n)
    {
        if (S(n["lightType"]) == "Spot")
        {
            var lg = new GameObject("Light");
            lg.transform.SetParent(go.transform, false);
            lg.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            go = lg;
        }
        var l = go.AddComponent<Light>();
        var c = L(n["color"]);
        l.color = new Color((float)N(c[0], 255) / 255f, (float)N(c[1], 255) / 255f, (float)N(c[2], 255) / 255f);
        l.type = S(n["lightType"]) == "Spot" ? LightType.Spot : LightType.Point;
        l.intensity = (float)N(n["intensity"], 1) * LightIntensityScale;
        l.range = (float)N(n["radius"], 1000) * CmToM;
        if (l.type == LightType.Spot) l.spotAngle = (float)N(n["outer"], 45) * 2f;
        l.shadows = B(n.ContainsKey("shadows") ? n["shadows"] : null, false) ? LightShadows.Soft : LightShadows.None;
    }

    static void BuildAudio(GameObject go, Dictionary<string, object> n)
    {
        if (!n.ContainsKey("cue") || n["cue"] == null) return;
        var cue = D(n["cue"]);
        bool awake = B(n["playOnAwake"], false);
        float cueVol = (float)N(cue["volumeMultiplier"], 1);
        var cueAtt = cue.ContainsKey("attenuation") ? D(cue["attenuation"]) : null;
        foreach (var wo in L(cue["waves"]))
        {
            var w = D(wo);
            string waveName = S(w["wave"]);
            var child = new GameObject(waveName);
            child.transform.SetParent(go.transform, false);
            var src = child.AddComponent<AudioSource>();
            var clip = FindAsset<AudioClip>(waveName);
            if (clip == null) Miss("sound", waveName, "the .wav in the Audio folder");
            src.clip = clip;
            src.playOnAwake = awake;
            src.loop = B(w["loop"], false);
            src.volume = Mathf.Clamp01(cueVol * (float)N(w["volume"], 1));
            src.pitch = (float)N(w["pitch"], 1);
            src.spatialBlend = 1f;
            var att = w.ContainsKey("attenuation") && w["attenuation"] != null ? D(w["attenuation"]) : cueAtt;
            if (att != null) ApplyAttenuation(src, att);
        }
    }

    static void ApplyAttenuation(AudioSource src, Dictionary<string, object> att)
    {
        float extent = (float)N(att["extent"], 0) * CmToM;
        float falloff = (float)N(att["falloff"], 0) * CmToM;
        string algo = S(att["algorithm"]);
        src.minDistance = Mathf.Max(0.01f, extent);
        src.maxDistance = Mathf.Max(src.minDistance + 0.01f, extent + falloff);
        if (algo == "Logarithmic") src.rolloffMode = AudioRolloffMode.Logarithmic;
        else if (algo == "Custom")
        {
            var keys = L(att["curve"]);
            var curve = new AnimationCurve();
            for (int i = 0; i < keys.Count; i++)
            {
                var k = L(keys[i]);
                curve.AddKey(new Keyframe((float)N(k[0], 0), (float)N(k[1], 0), 0f, 0f));
            }
            for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
            src.rolloffMode = AudioRolloffMode.Custom;
            src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
        }
        else src.rolloffMode = AudioRolloffMode.Linear;
    }

    static void BuildParticles(GameObject go, Dictionary<string, object> n)
    {
        if (!n.ContainsKey("system") || n["system"] == null) return;
        bool awake = B(n["playOnAwake"], true);
        foreach (var eo in L(D(n["system"])["emitters"]))
            BuildEmitter(go, D(eo), awake);
    }

    static void BuildEmitter(GameObject parent, Dictionary<string, object> e, bool awake)
    {
        string name = S(e["name"]);
        var mod = D(e["modules"]);
        var rends = L(e["renderers"]);
        string rtype = rends.Count > 0 ? S(D(rends[0])["type"]) : "";
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);

        Vector3 posMin = Vector3.zero, posMax = Vector3.zero;
        bool hasPos = mod.ContainsKey("position") && mod["position"] != null;
        if (hasPos) VecRange(mod["position"], out posMin, out posMax);

        if (rtype == "Light")
        {
            BuildLightEmitter(go, mod, D(rends[0]), awake, hasPos ? (posMin + posMax) * 0.5f : Vector3.zero);
            return;
        }

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var pr = go.GetComponent<ParticleSystemRenderer>();
        var main = ps.main;
        var loop = D(e["loop"]);

        float dmin, dmax;
        ScalarRange(loop["duration"], 1.0, out dmin, out dmax);
        main.duration = Mathf.Max(0.05f, dmin);
        main.loop = S(loop["behavior"]) != "Once";
        main.playOnAwake = awake;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 2000;
        main.startSpeed = 0f;
        if (B(loop["delayEnabled"], false) && loop["delay"] != null)
        {
            float a, b;
            ScalarRange(loop["delay"], 0, out a, out b);
            main.startDelay = new ParticleSystem.MinMaxCurve(a, b);
        }

        float lmin, lmax;
        ScalarRange(mod.ContainsKey("lifetime") ? mod["lifetime"] : null, 1.0, out lmin, out lmax);
        main.startLifetime = new ParticleSystem.MinMaxCurve(lmin, lmax);

        Color cmin = Color.white, cmax = Color.white;
        if (mod.ContainsKey("color") && mod["color"] != null) ColorRange(mod["color"], out cmin, out cmax);
        main.startColor = new ParticleSystem.MinMaxGradient(cmin.gamma, cmax.gamma);

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        var bursts = new List<ParticleSystem.Burst>();
        foreach (var so in L(e["spawn"]))
        {
            var s = D(so);
            string st = S(s["type"]);
            if (st == "Burst")
            {
                float a, b;
                ScalarRange(s["amount"], 1, out a, out b);
                bursts.Add(new ParticleSystem.Burst((float)N(s["time"], 0), (short)Mathf.RoundToInt(a), (short)Mathf.RoundToInt(b)));
            }
            else if (st == "Rate")
            {
                float a, b;
                ScalarRange(s["rate"], 0, out a, out b);
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(a, b);
            }
            else log.Add("UNSUPPORTED spawn type " + st + " in " + name);
        }
        emission.SetBursts(bursts.ToArray());

        BuildShape(ps, mod, name, hasPos, posMin, posMax);
        BuildVelocity(ps, mod, lmin, lmax, name);

        bool isMesh = rtype == "Mesh";
        if (isMesh) BuildMeshParticle(ps, pr, mod, D(rends[0]), name);
        else BuildSpriteParticle(ps, pr, mod, rends.Count > 0 ? D(rends[0]) : null, name);

        if (mod.ContainsKey("scaleColor") && mod["scaleColor"] != null)
        {
            var values = L(D(mod["scaleColor"])["values"]);
            float maxScale;
            var g = BuildGradient(values, out maxScale);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(g);
            if (maxScale > 1.001f) log.Add("APPROX " + name + ": colour scale up to " + maxScale.ToString("0.###") + " normalised to 1 (particle vertex colours are LDR)");
        }
    }

    static void BuildLightEmitter(GameObject go, Dictionary<string, object> mod, Dictionary<string, object> r, bool awake, Vector3 uePos)
    {
        go.transform.localPosition = P(uePos) * CmToM;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.enabled = awake;
        l.shadows = LightShadows.None;
        l.range = (float)N(r["radiusScale"], 1);
        Color c = Color.white;
        if (mod.ContainsKey("scaleColor") && mod["scaleColor"] != null)
        {
            var v = L(D(mod["scaleColor"])["values"]);
            var first = D(v[0]);
            float rr = (float)N(first["R"], 1), gg = (float)N(first["G"], 1), bb = (float)N(first["B"], 1);
            float m = Mathf.Max(rr, Mathf.Max(gg, bb));
            if (m > 0f) c = new Color(rr / m, gg / m, bb / m);
        }
        l.color = c.gamma;
        l.intensity = 1f;
        log.Add("APPROX light emitter " + go.name + ": colour from first colour-scale key, intensity 1, range " + l.range.ToString("0.##") + " m (UE per-particle radius/intensity not exported)");
    }

    static void BuildShape(ParticleSystem ps, Dictionary<string, object> mod, string name, bool hasPos, Vector3 posMin, Vector3 posMax)
    {
        var shape = ps.shape;
        Vector3 mid = (posMin + posMax) * 0.5f;
        if (mod.ContainsKey("shape") && mod["shape"] != null)
        {
            var s = D(mod["shape"]);
            string prim = S(s["primitive"]);
            float a, b;
            shape.enabled = true;
            if (prim == "Cylinder")
            {
                ScalarRange(s["cylinderRadius"], 100, out a, out b);
                float h1, h2;
                ScalarRange(s["cylinderHeight"], 100, out h1, out h2);
                shape.shapeType = ParticleSystemShapeType.ConeVolume;
                shape.angle = 0f;
                shape.radius = a * CmToM;
                shape.radiusThickness = 1f;
                shape.length = h1 * CmToM;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                shape.position = P(mid) * CmToM + new Vector3(0f, -h1 * 0.5f * CmToM, 0f);
                log.Add("DEFAULT " + name + ": cylinder height midpoint and surface thickness not exported, centred and volume-filled");
            }
            else if (prim == "Ring")
            {
                ScalarRange(s["ringRadius"], 100, out a, out b);
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = a * CmToM;
                shape.radiusThickness = 0f;
                shape.arc = 360f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                shape.position = P(mid) * CmToM;
            }
            else
            {
                shape.enabled = false;
                log.Add("UNSUPPORTED shape " + prim + " in " + name);
            }
            if (hasPos && (posMax - posMin).sqrMagnitude > 0f) log.Add("APPROX " + name + ": initial position range collapsed to its midpoint because a shape is also used");
            return;
        }
        if (hasPos)
        {
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.position = P(mid) * CmToM;
            Vector3 ext = P(posMax - posMin);
            shape.scale = new Vector3(Mathf.Abs(ext.x), Mathf.Abs(ext.y), Mathf.Abs(ext.z)) * CmToM;
        }
        else shape.enabled = false;
    }

    static void BuildVelocity(ParticleSystem ps, Dictionary<string, object> mod, float lmin, float lmax, string name)
    {
        if (!mod.ContainsKey("velocity") || mod["velocity"] == null) return;
        Vector3 vmin, vmax;
        VecRange(mod["velocity"], out vmin, out vmax);
        Vector3 a = P(vmin) * CmToM, b = P(vmax) * CmToM;
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.Local;
        float drag = 0f;
        if (mod.ContainsKey("drag") && mod["drag"] != null)
        {
            float d1, d2;
            ScalarRange(mod["drag"], 0, out d1, out d2);
            drag = (d1 + d2) * 0.5f;
        }
        if (drag > 0f)
        {
            float life = (lmin + lmax) * 0.5f;
            vel.x = DragCurve(a.x, b.x, drag, life);
            vel.y = DragCurve(a.y, b.y, drag, life);
            vel.z = DragCurve(a.z, b.z, drag, life);
            log.Add("UNVERIFIED " + name + ": drag " + drag.ToString("0.###") + " applied as exponential velocity decay exp(-drag*age); Niagara's exact stateless drag formula is not confirmed");
        }
        else
        {
            vel.x = new ParticleSystem.MinMaxCurve(Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x));
            vel.y = new ParticleSystem.MinMaxCurve(Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y));
            vel.z = new ParticleSystem.MinMaxCurve(Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z));
        }
    }

    static ParticleSystem.MinMaxCurve DragCurve(float v0a, float v0b, float drag, float life)
    {
        var ca = new AnimationCurve();
        var cb = new AnimationCurve();
        const int steps = 16;
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float f = Mathf.Exp(-drag * life * t);
            ca.AddKey(t, v0a * f);
            cb.AddKey(t, v0b * f);
        }
        return new ParticleSystem.MinMaxCurve(1f, ca, cb);
    }

    static void BuildSpriteParticle(ParticleSystem ps, ParticleSystemRenderer pr, Dictionary<string, object> mod, Dictionary<string, object> r, string name)
    {
        Vector3 smin = new Vector3(100, 100, 0), smax = new Vector3(100, 100, 0);
        if (mod.ContainsKey("spriteSize") && mod["spriteSize"] != null) VecRange(mod["spriteSize"], out smin, out smax);
        var main = ps.main;
        main.startSize3D = true;
        main.startSizeX = new ParticleSystem.MinMaxCurve(smin.x * CmToM, smax.x * CmToM);
        main.startSizeY = new ParticleSystem.MinMaxCurve(smin.y * CmToM, smax.y * CmToM);
        main.startSizeZ = new ParticleSystem.MinMaxCurve(CmToM);
        if (mod.ContainsKey("scaleSpriteSize") && mod["scaleSpriteSize"] != null)
        {
            var values = L(D(mod["scaleSpriteSize"])["values"]);
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.separateAxes = true;
            sol.x = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "X"));
            sol.y = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "Y"));
            sol.z = new ParticleSystem.MinMaxCurve(1f);
        }
        if (r == null) return;
        string matName = S(r["material"]);
        var mat = FindMaterial(matName);
        if (mat == null && !string.IsNullOrEmpty(matName)) Miss("material", matName, "its converted shader");
        pr.sharedMaterial = mat;
        if (S(r["alignment"]) == "VelocityAligned")
        {
            pr.renderMode = ParticleSystemRenderMode.Stretch;
            pr.velocityScale = 0f;
            pr.lengthScale = 1f;
            log.Add("APPROX " + name + ": velocity-aligned sprite mapped to Stretched Billboard (check width/length visually)");
        }
        else pr.renderMode = ParticleSystemRenderMode.Billboard;
        var sub = L(r["subImage"]);
        if (N(sub[0], 1) > 1 || N(sub[1], 1) > 1) log.Add("NOT APPLIED " + name + ": sprite sub-image grid " + N(sub[0], 1) + "x" + N(sub[1], 1) + " (frame selection is driven by the material)");
        var pv = L(r["pivot"]);
        if (Mathf.Abs((float)N(pv[0], 0.5) - 0.5f) > 0.001f || Mathf.Abs((float)N(pv[1], 0.5) - 0.5f) > 0.001f)
            log.Add("NOT APPLIED " + name + ": sprite pivot (" + N(pv[0], 0.5) + ", " + N(pv[1], 0.5) + ") (Niagara/Unity pivot sign convention not verified)");
    }

    static void BuildMeshParticle(ParticleSystem ps, ParticleSystemRenderer pr, Dictionary<string, object> mod, Dictionary<string, object> r, string name)
    {
        string meshName = S(r["mesh"]);
        var mesh = FindAsset<Mesh>(meshName);
        if (mesh == null) Miss("mesh", meshName, "the .fbx in the Meshes folder");
        pr.renderMode = ParticleSystemRenderMode.Mesh;
        pr.mesh = mesh;
        pr.alignment = ParticleSystemRenderSpace.Local;
        var mats = L(r["materials"]);
        if (mats.Count > 0)
        {
            string mn = S(mats[0]);
            var mat = FindMaterial(mn);
            if (mat == null && !string.IsNullOrEmpty(mn)) Miss("material", mn, "its converted shader");
            pr.sharedMaterial = mat;
        }
        var rot = V3(r["meshRotation"]);
        Vector3 euler = PorterBlueprintSetup.RotatorToQuaternion(rot.x, rot.y, rot.z).eulerAngles * Mathf.Deg2Rad;
        var main = ps.main;
        main.startRotation3D = true;
        main.startRotationX = euler.x;
        main.startRotationY = euler.y;
        main.startRotationZ = euler.z;
        Vector3 ms = r.ContainsKey("meshScale") && r["meshScale"] != null ? P(V3(r["meshScale"])) : Vector3.one;
        main.startSize3D = true;
        main.startSizeX = (PorterBlueprintSetup.MeshArrivesMirroredX ? -1f : 1f) * ms.x;
        main.startSizeY = ms.y;
        main.startSizeZ = ms.z;
        if (mod.ContainsKey("scaleMeshSize") && mod["scaleMeshSize"] != null)
        {
            var values = L(D(mod["scaleMeshSize"])["values"]);
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.separateAxes = false;
            sol.size = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "X"));
        }
        log.Add("NOT APPLIED " + name + ": mesh renderer axis lock and dynamic material parameters");
        log.Add("UNVERIFIED " + name + ": mesh particle orientation uses the same axis mapping as the sky dome meshes");
    }

    static AnimationCurve CurveFrom(List<object> values, string comp)
    {
        var c = new AnimationCurve();
        int n = values.Count;
        for (int i = 0; i < n; i++)
        {
            float t = n > 1 ? i / (float)(n - 1) : 0f;
            c.AddKey(t, (float)N(D(values[i])[comp], 1));
        }
        for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
        return c;
    }

    static Gradient BuildGradient(List<object> values, out float maxScale)
    {
        int n = values.Count;
        maxScale = 0f;
        for (int i = 0; i < n; i++)
        {
            var c = D(values[i]);
            maxScale = Mathf.Max(maxScale, (float)N(c["R"], 1), (float)N(c["G"], 1), (float)N(c["B"], 1));
        }
        float norm = maxScale > 1f ? maxScale : 1f;
        int m = Mathf.Min(8, Mathf.Max(n, 2));
        var ck = new GradientColorKey[m];
        var ak = new GradientAlphaKey[m];
        for (int i = 0; i < m; i++)
        {
            int idx = n > 1 ? Mathf.RoundToInt(i * (n - 1) / (float)(m - 1)) : 0;
            float t = n > 1 ? idx / (float)(n - 1) : (float)i / (m - 1);
            var c = D(values[Mathf.Min(idx, n - 1)]);
            var col = new Color((float)N(c["R"], 1) / norm, (float)N(c["G"], 1) / norm, (float)N(c["B"], 1) / norm);
            ck[i] = new GradientColorKey(col.gamma, t);
            ak[i] = new GradientAlphaKey((float)N(c["A"], 1), t);
        }
        var g = new Gradient();
        g.SetKeys(ck, ak);
        return g;
    }

    static void ScalarRange(object dist, double def, out float mn, out float mx)
    {
        var d = dist as Dictionary<string, object>;
        mn = mx = (float)def;
        if (d == null) return;
        if (d.ContainsKey("min")) { mn = (float)N(d["min"], def); mx = (float)N(d["max"], def); }
        else if (d.ContainsKey("values"))
        {
            var v = L(d["values"]);
            if (v.Count > 0) { mn = (float)N(v[0], def); mx = (float)N(v[v.Count - 1], def); }
        }
    }

    static void VecRange(object dist, out Vector3 mn, out Vector3 mx)
    {
        var d = D(dist);
        mn = mx = Vector3.zero;
        if (d.ContainsKey("min")) { mn = V3(d["min"]); mx = V3(d["max"]); }
        else if (d.ContainsKey("values"))
        {
            var v = L(d["values"]);
            mn = V3(v[0]);
            mx = V3(v[v.Count - 1]);
        }
    }

    static void ColorRange(object dist, out Color mn, out Color mx)
    {
        var d = D(dist);
        var v = L(d["values"]);
        mn = Col(v[0]);
        mx = Col(v[v.Count - 1]);
    }

    static Color Col(object o)
    {
        var d = D(o);
        return new Color((float)N(d["R"], 1), (float)N(d["G"], 1), (float)N(d["B"], 1), (float)N(d["A"], 1));
    }

    static Vector3 V3(object o)
    {
        var l = o as List<object>;
        if (l != null) return new Vector3((float)N(l[0], 0), (float)N(l[1], 0), l.Count > 2 ? (float)N(l[2], 0) : 0f);
        var d = D(o);
        return new Vector3((float)N(d.ContainsKey("X") ? d["X"] : null, 0), (float)N(d.ContainsKey("Y") ? d["Y"] : null, 0), (float)N(d.ContainsKey("Z") ? d["Z"] : null, 0));
    }

    static T FindAsset<T>(string name) where T : UnityEngine.Object
    {
        if (string.IsNullOrEmpty(name)) return null;
        string[] guids = AssetDatabase.FindAssets(name + " t:" + typeof(T).Name);
        T fallback = null;
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            bool sameFile = Path.GetFileNameWithoutExtension(path) == name;
            var all = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int j = 0; j < all.Length; j++)
            {
                var t = all[j] as T;
                if (t == null) continue;
                if (t.name == name) return t;
                if (sameFile && fallback == null) fallback = t;
            }
        }
        return fallback;
    }

    static Material FindMaterial(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return PorterBlueprintSetup.MaterialByName(name, specFolder) ?? FindAsset<Material>(name);
    }

    static Dictionary<string, object> D(object o)
    {
        return o as Dictionary<string, object>;
    }

    static List<object> L(object o)
    {
        return o as List<object>;
    }

    static string S(object o)
    {
        return o == null ? "" : o.ToString();
    }

    static double N(object o, double def)
    {
        if (o is double) return (double)o;
        return def;
    }

    static bool B(object o, bool def)
    {
        if (o is bool) return (bool)o;
        return def;
    }
}

static class PorterMiniJson
{
    public static object Parse(string s)
    {
        int i = 0;
        return Val(s, ref i);
    }

    static void Ws(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    static object Val(string s, ref int i)
    {
        Ws(s, ref i);
        char c = s[i];
        if (c == '{')
        {
            var d = new Dictionary<string, object>();
            i++;
            Ws(s, ref i);
            if (s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string k = Str(s, ref i);
                Ws(s, ref i);
                i++;
                d[k] = Val(s, ref i);
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                i++;
                break;
            }
            return d;
        }
        if (c == '[')
        {
            var l = new List<object>();
            i++;
            Ws(s, ref i);
            if (s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Val(s, ref i));
                Ws(s, ref i);
                if (s[i] == ',') { i++; continue; }
                i++;
                break;
            }
            return l;
        }
        if (c == '"') return Str(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
    }

    static string Str(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;
        while (s[i] != '"')
        {
            if (s[i] == '\\')
            {
                i++;
                char e = s[i];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                else sb.Append(e);
            }
            else sb.Append(s[i]);
            i++;
        }
        i++;
        return sb.ToString();
    }
}
#endif
