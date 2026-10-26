#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static partial class PorterPropBuilder
{
    const float CmToM = 0.01f;
    const float LightIntensityScale = 1f;

    static List<string> log;
    static int missing;
    static bool loud;
    static List<string> missingNames = new List<string>();
    static string specFolder;
    static List<bool> hasKids;
    static string currentBlueprint = "";

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
        currentBlueprint = bpName;
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
        var mesh = FindMesh(meshName);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        if (mesh == null) Miss("mesh", meshName, meshWhy);
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
        // The machine's screen is driven by gameplay: _S_Cooldown 1 shows the red battery (off), 0 shows the blue activated logo.
        // Props start activated, matching the blue hologram particles that are converted.
        foreach (var m in mats)
        {
            if (m != null && m.HasProperty("_S_Cooldown") && m.GetFloat("_S_Cooldown") != 0f)
            {
                m.SetFloat("_S_Cooldown", 0f);
                EditorUtility.SetDirty(m);
                log.Add("STATE " + m.name + ": _S_Cooldown set to 0 so the machine screen starts in its activated (blue) state; set it to 1 for the off/cooldown (red) state");
            }
        }
        if (mesh != null && mesh.subMeshCount != slots.Count)
            log.Add("CHECK mesh '" + meshName + "' has " + mesh.subMeshCount + " submeshes but the blueprint lists " + slots.Count + " material slots");
    }

    static string meshWhy = "";

    static Mesh MeshIn(string path, string name)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(path);
        Mesh first = null;
        for (int i = 0; i < all.Length; i++)
        {
            var m = all[i] as Mesh;
            if (m == null) continue;
            if (m.name == name) return m;
            if (first == null) first = m;
        }
        return first;
    }

    static Mesh FindMesh(string name)
    {
        meshWhy = "";
        if (string.IsNullOrEmpty(name))
        {
            meshWhy = "the blueprint names no mesh for it";
            return null;
        }
        var found = FindAsset<Mesh>(name);
        if (found != null) return found;
        string[] exts = { ".fbx", ".obj", ".dae" };
        var candidates = new List<string>();
        foreach (string ext in exts) candidates.Add(specFolder + "/Meshes/" + name + ext);
        foreach (string guid in AssetDatabase.FindAssets(name))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(p) != name) continue;
            string e = Path.GetExtension(p).ToLowerInvariant();
            if (Array.IndexOf(exts, e) >= 0 && !candidates.Contains(p)) candidates.Add(p);
        }
        string existing = null;
        foreach (string c in candidates)
        {
            if (!File.Exists(c)) continue;
            existing = c;
            AssetDatabase.ImportAsset(c, ImportAssetOptions.ForceSynchronousImport);
            var m = MeshIn(c, name);
            if (m != null) return m;
        }
        if (existing != null)
        {
            meshWhy = "the file " + existing + " exists, but Unity found no mesh inside it after importing it (open it in the Project window and check the Model import settings and the Console for import errors)";
            return null;
        }
        foreach (string e in new[] { ".glb", ".gltf" })
        {
            string g = specFolder + "/Meshes/" + name + e;
            if (File.Exists(g))
            {
                meshWhy = g + " is a " + e + " file, which Unity cannot import; the .glb to .fbx conversion failed when the mesh was exported, so check the converter log for 'fbx conversion failed'";
                return null;
            }
        }
        meshWhy = "no " + name + ".fbx was found under " + specFolder + "/Meshes or anywhere else in the project";
        return null;
    }

    static void Miss(string what, string name, string hint)
    {
        missing++;
        missingNames.Add(name);
        if (loud) Debug.LogWarning("Porter: " + what + " " + name + " was not found in the project (" + hint + "), so its slot stays empty");
        log.Add("MISSING " + what + " '" + name + "': " + hint);
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
        if (go.GetComponent<PorterBodyAnchor>() == null) go.AddComponent<PorterBodyAnchor>();
        foreach (var eo in L(D(n["system"])["emitters"]))
        {
            BuildReplayEmitter(go, D(eo));
            BuildEmitter(go, D(eo), awake);
        }
        // one control for the whole effect; added last so the starting values it remembers are the finished ones
        var controller = go.GetComponent<PorterParticleController>();
        if (controller == null) controller = go.AddComponent<PorterParticleController>();
        controller.Collect();
        controller.Rebase();
    }

    static void BuildEmitter(GameObject parent, Dictionary<string, object> e, bool awake)
    {
        // renderers played from a baked replay are not Particle System renderers
        var all = L(e["renderers"]);
        var rl = new List<object>();
        for (int i = 0; i < all.Count; i++)
        {
            var ri = D(all[i]);
            if (ri.ContainsKey("baked")) continue;
            string rkind = ri.ContainsKey("type") ? S(ri["type"]) : "";
            if (rkind != "Sprite" && rkind != "Mesh" && rkind != "Light" && rkind != "Ribbon")
            {
                log.Add("NOT APPLIED " + S(e["name"]) + ": " + rkind + " renderer has no Particle System equivalent");
                continue;
            }
            rl.Add(all[i]);
        }
        if (all.Count > 0 && rl.Count == 0) return;
        if (rl.Count != all.Count)
        {
            e = new Dictionary<string, object>(e);
            e["renderers"] = rl;
        }
        var picks = new List<int>();
        for (int i = 0; i < rl.Count; i++)
        {
            string t = S(D(rl[i])["type"]);
            if (t == "Mesh" || t == "Sprite" || t == "Ribbon" || t == "Light") picks.Add(i);
        }
        if (picks.Count < 2)
        {
            BuildEmitterOne(parent, e, awake, -1);
            return;
        }
        string baseName = S(e["name"]);
        int seed = 17;
        for (int i = 0; i < baseName.Length; i++) seed = seed * 31 + baseName[i];
        seed = Mathf.Abs(seed % 1000003) + 1;
        for (int k = 0; k < picks.Count; k++)
        {
            var copy = new Dictionary<string, object>(e);
            copy["renderers"] = new List<object> { rl[picks[k]] };
            if (k > 0) copy["name"] = baseName + "_Renderer" + (k + 1);
            BuildEmitterOne(parent, copy, awake, seed);
        }
        log.Add("UNVERIFIED " + baseName + ": Unity draws one renderer per Particle System, so its " + picks.Count + " renderers became " + picks.Count + " separate objects (a Light renderer becomes a point light) with identical settings and the same random seed; per-particle random values stay in step only as far as Unity's seeded randomness does");
    }

    static void BuildEmitterOne(GameObject parent, Dictionary<string, object> e, bool awake, int sharedSeed)
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
        if (sharedSeed >= 0)
        {
            ps.useAutoRandomSeed = false;
            ps.randomSeed = (uint)sharedSeed;
        }
        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.minParticleSize = 0f;
        pr.maxParticleSize = 555f;
        var main = ps.main;
        var loop = D(e["loop"]);

        float dmin, dmax;
        ScalarRange(loop["duration"], 1.0, out dmin, out dmax);
        main.duration = Mathf.Max(0.05f, dmin);
        main.loop = S(loop["behavior"]) != "Once";
        main.playOnAwake = awake;
        bool worldSpace = e.ContainsKey("local") && !B(e["local"], true);
        main.simulationSpace = worldSpace ? ParticleSystemSimulationSpace.World : ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = ParticleCap(e);
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

        BuildShape(ps, mod, name, hasPos, posMin, posMax, e);
        BuildVelocity(ps, mod, lmin, lmax, name);
        BuildForces(ps, mod, name);
        BuildMotionFit(ps, mod, name);

        bool isMesh = rtype == "Mesh";
        bool isRibbon = rtype == "Ribbon";
        if (isMesh) BuildMeshParticle(ps, pr, mod, D(rends[0]), name);
        else if (isRibbon) BuildRibbonParticle(ps, pr, mod, D(rends[0]), name);
        else BuildSpriteParticle(ps, pr, mod, rends.Count > 0 ? D(rends[0]) : null, name);
        if (!isRibbon) BuildRotation(ps, mod, isMesh, name);
        if (!isRibbon) BuildRotationFit(ps, mod, name);
        if (rends.Count > 0 && D(rends[0]).ContainsKey("castShadows") && B(D(rends[0])["castShadows"], false))
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        if (e.ContainsKey("unhandledModules") && L(e["unhandledModules"]).Count > 0)
        {
            var names = new List<string>();
            foreach (var u in L(e["unhandledModules"])) names.Add(S(u));
            log.Add("NOT APPLIED " + name + ": Niagara modules with no Unity mapping yet: " + string.Join(", ", names.ToArray()));
        }
        if (mod.ContainsKey("subUV") && mod["subUV"] != null && rtype != "Sprite")
        {
            var su = D(mod["subUV"]);
            log.Add("NOT APPLIED " + name + ": SubUV animation (" + S(su["frames"]) + " frames, mode " + S(su["mode"]) + "); only sprite emitters get sheet animation");
        }

        bool streamed = !isRibbon && BuildColorStreams(ps, pr, mod, name, rends.Count > 0 ? D(rends[0]) : null);
        if (!streamed && mod.ContainsKey("scaleColor") && mod["scaleColor"] != null)
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

    static int ParticleCap(Dictionary<string, object> e)
    {
        int alive = (int)N(e.ContainsKey("maxAlive") ? e["maxAlive"] : null, 0);
        int burst = 0;
        foreach (var so in L(e["spawn"]))
        {
            var s = D(so);
            if (S(s["type"]) != "Burst") continue;
            float a, b;
            ScalarRange(s["amount"], 1, out a, out b);
            burst += Mathf.RoundToInt(Mathf.Max(a, b));
        }
        int peak = Mathf.Max(alive, burst);
        if (peak <= 0) return 2000;
        return Mathf.Clamp(Mathf.CeilToInt(peak * 1.5f) + 16, 64, 50000);
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
        bool fromReplay = false;
        if (r.ContainsKey("streams") && r["streams"] != null)
        {
            var st = D(r["streams"]);
            if (st.ContainsKey("radius") && st["radius"] != null && L(st["radius"]).Count > 0)
            {
                var radii = L(st["radius"]);
                float radiusSum = 0f;
                foreach (var rv in radii) radiusSum += (float)N(rv, 0);
                float meanRadius = radiusSum / radii.Count;
                if (meanRadius > 0f)
                {
                    l.range = meanRadius * (float)N(r["radiusScale"], 1) * CmToM;
                    fromReplay = true;
                }
            }
            if (st.ContainsKey("color") && st["color"] != null && L(st["color"]).Count > 0)
            {
                float sumR = 0f, sumG = 0f, sumB = 0f;
                foreach (var co in L(st["color"]))
                {
                    var cd = D(co);
                    sumR += Mathf.Max(0f, (float)N(cd["r"], 0));
                    sumG += Mathf.Max(0f, (float)N(cd["g"], 0));
                    sumB += Mathf.Max(0f, (float)N(cd["b"], 0));
                }
                float sumMax = Mathf.Max(sumR, Mathf.Max(sumG, sumB));
                if (sumMax > 0f)
                {
                    c = new Color(sumR / sumMax, sumG / sumMax, sumB / sumMax);
                    fromReplay = true;
                }
            }
        }
        l.color = c.gamma;
        l.intensity = 1f;
        if (fromReplay) log.Add("APPROX light emitter " + go.name + ": range " + l.range.ToString("0.##") + " m and colour are the replayed light's averages over its life, intensity 1, and the light stays on for as long as the system plays");
        else log.Add("APPROX light emitter " + go.name + ": colour from first colour-scale key, intensity 1, range " + l.range.ToString("0.##") + " m (no replayed radius or colour for this light)");
    }

    static bool ReplayRange(Dictionary<string, object> e, out Vector3 lo, out Vector3 hi)
    {
        lo = hi = Vector3.zero;
        if (e == null || !e.ContainsKey("replayPosition") || e["replayPosition"] == null) return false;
        var r = D(e["replayPosition"]);
        if (r == null || !r.ContainsKey("min") || !r.ContainsKey("max")) return false;
        if ((int)N(r.ContainsKey("births") ? r["births"] : null, 0) < 6) return false;
        lo = V3(r["min"]);
        hi = V3(r["max"]);
        return true;
    }

    static void ShapeSupport(Dictionary<string, object> s, string prim, out Vector3 lo, out Vector3 hi)
    {
        lo = hi = Vector3.zero;
        float a, b;
        if (prim == "Box")
        {
            Vector3 size = VecMid(s.ContainsKey("boxSize") ? s["boxSize"] : null, new Vector3(100f, 100f, 100f));
            hi = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
            lo = -hi;
        }
        else if (prim == "Plane")
        {
            Vector3 size = VecMid(s.ContainsKey("planeSize") ? s["planeSize"] : null, new Vector3(100f, 100f, 0f));
            hi = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), 0f) * 0.5f;
            lo = -hi;
        }
        else if (prim == "Cylinder")
        {
            ScalarRange(s.ContainsKey("cylinderRadius") ? s["cylinderRadius"] : null, 100, out a, out b);
            float h1, h2, m1, m2;
            ScalarRange(s.ContainsKey("cylinderHeight") ? s["cylinderHeight"] : null, 100, out h1, out h2);
            ScalarRange(s.ContainsKey("heightMidpoint") ? s["heightMidpoint"] : null, 0.5, out m1, out m2);
            float r = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            float h = Mathf.Max(Mathf.Abs(h1), Mathf.Abs(h2));
            float mid = (m1 + m2) * 0.5f;
            lo = new Vector3(-r, -r, -mid * h);
            hi = new Vector3(r, r, (1f - mid) * h);
        }
        else if (prim == "Ring")
        {
            ScalarRange(s.ContainsKey("ringRadius") ? s["ringRadius"] : null, 100, out a, out b);
            float r = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            lo = new Vector3(-r, -r, 0f);
            hi = new Vector3(r, r, 0f);
        }
        else
        {
            ScalarRange(s.ContainsKey("sphereRadius") ? s["sphereRadius"] : null, 100, out a, out b);
            float r = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            lo = new Vector3(-r, -r, -r);
            hi = new Vector3(r, r, r);
        }
    }

    static void ApplyBox(ParticleSystem.ShapeModule shape, Vector3 lo, Vector3 hi)
    {
        Vector3 c = P((lo + hi) * 0.5f) * CmToM;
        Vector3 e = P(hi - lo) * CmToM;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.rotation = Vector3.zero;
        shape.scale = new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z));
        shape.position = c;
    }

    static string Cm3(Vector3 v)
    {
        return "(" + v.x.ToString("0.#") + ", " + v.y.ToString("0.#") + ", " + v.z.ToString("0.#") + ")";
    }

    static void BuildShape(ParticleSystem ps, Dictionary<string, object> mod, string name, bool hasPos, Vector3 posMin, Vector3 posMax, Dictionary<string, object> emitter)
    {
        var shape = ps.shape;
        Vector3 mid = (posMin + posMax) * 0.5f;
        bool hasShape = mod.ContainsKey("shape") && mod["shape"] != null;
        string vtype = mod.ContainsKey("velocityType") ? S(mod["velocityType"]) : "";
        Vector3 rLo, rHi;
        bool hasReplay = ReplayRange(emitter, out rLo, out rHi);
        if (vtype == "InCone" || vtype == "FromPoint")
        {
            if (!hasShape && (!hasPos || (posMax - posMin).sqrMagnitude <= 0f))
            {
                BuildDirectionalVelocity(ps, mod, name, vtype, mid);
                return;
            }
            log.Add("NOT APPLIED " + name + ": " + vtype + " velocity, because a shape or position range already drives the particle shape in Unity (its shape module sets position and direction together)");
        }
        if (hasShape)
        {
            var s = D(mod["shape"]);
            string prim = S(s["primitive"]);
            float a, b;
            shape.enabled = true;
            Vector3 offUE = s.ContainsKey("offset") && s["offset"] != null ? VecMid(s["offset"], Vector3.zero) : Vector3.zero;
            Vector3 off = P(offUE) * CmToM;
            Vector3 scaleUE = s.ContainsKey("scale") && s["scale"] != null ? VecMid(s["scale"], Vector3.one) : Vector3.one;
            Vector3 sLo, sHi;
            ShapeSupport(s, prim, out sLo, out sHi);
            Vector3 q1 = Vector3.Scale(sLo, scaleUE), q2 = Vector3.Scale(sHi, scaleUE);
            sLo = Vector3.Min(q1, q2) + offUE + (hasPos ? posMin : Vector3.zero);
            sHi = Vector3.Max(q1, q2) + offUE + (hasPos ? posMax : Vector3.zero);
            bool known = prim == "Box" || prim == "Plane" || prim == "Cylinder" || prim == "Ring" || prim == "Sphere";
            bool posSpread = hasPos && (posMax - posMin).sqrMagnitude > 0f;
            bool wider = false;
            if (hasReplay && known)
            {
                Vector3 span = sHi - sLo;
                float tolX = Mathf.Max(10f, span.x * 0.25f), tolY = Mathf.Max(10f, span.y * 0.25f), tolZ = Mathf.Max(10f, span.z * 0.25f);
                wider = rLo.x < sLo.x - tolX || rHi.x > sHi.x + tolX || rLo.y < sLo.y - tolY || rHi.y > sHi.y + tolY || rLo.z < sLo.z - tolZ || rHi.z > sHi.z + tolZ;
            }
            bool asBox = prim == "Box" || (prim == "Plane" && (posSpread || wider)) || !known || wider;
            if (asBox)
            {
                Vector3 lo = known ? sLo : (hasPos ? posMin : Vector3.zero), hi = known ? sHi : (hasPos ? posMax : Vector3.zero);
                if (hasReplay)
                {
                    lo = Vector3.Min(lo, rLo);
                    hi = Vector3.Max(hi, rHi);
                }
                ApplyBox(shape, lo, hi);
                if (prim != "Box")
                    log.Add("APPROX " + name + ": " + prim + " spawn shape drawn as a box covering the Unreal spawn spread " + Cm3(lo) + " to " + Cm3(hi) + " cm" + (!known ? " (the shape type is not supported)" : ""));
                if (posSpread && prim == "Box")
                    log.Add("APPROX " + name + ": box size and position range are added together, so the box is as wide as both; Unity spreads particles evenly over it");
            }
            else if (prim == "Cylinder")
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
                shape.scale = new Vector3(Mathf.Abs(scaleUE.x), Mathf.Abs(scaleUE.y), Mathf.Abs(scaleUE.z));
                float m1, m2;
                ScalarRange(s.ContainsKey("heightMidpoint") ? s["heightMidpoint"] : null, 0.5, out m1, out m2);
                float midpoint = (m1 + m2) * 0.5f;
                shape.position = P(mid) * CmToM + off + new Vector3(0f, -h1 * midpoint * CmToM * Mathf.Abs(scaleUE.z), 0f);
                log.Add("DEFAULT " + name + ": cylinder surface thickness not exported, volume-filled");
            }
            else if (prim == "Ring")
            {
                ScalarRange(s["ringRadius"], 100, out a, out b);
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = a * CmToM;
                shape.radiusThickness = 0f;
                shape.arc = 360f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                shape.scale = new Vector3(Mathf.Abs(scaleUE.x), Mathf.Abs(scaleUE.y), 1f);
                shape.position = P(mid) * CmToM + off;
            }
            else if (prim == "Plane")
            {
                Vector3 size = VecMid(s.ContainsKey("planeSize") ? s["planeSize"] : null, new Vector3(100f, 100f, 0f));
                shape.shapeType = ParticleSystemShapeType.Rectangle;
                shape.scale = new Vector3(Mathf.Abs(size.x * scaleUE.x), Mathf.Abs(size.y * scaleUE.y), 1f) * CmToM;
                shape.rotation = new Vector3(-90f, 0f, 0f);
                shape.position = P(mid) * CmToM + off;
            }
            else
            {
                ScalarRange(s.ContainsKey("sphereRadius") ? s["sphereRadius"] : null, 100, out a, out b);
                float rmax = Mathf.Max(a, b), rmin = Mathf.Min(a, b);
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = Mathf.Max(rmax, 0.01f) * CmToM;
                shape.radiusThickness = rmax > 0f ? Mathf.Clamp01(1f - rmin / rmax) : 1f;
                Vector3 sc = P(scaleUE);
                shape.scale = new Vector3(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z));
                shape.position = P(mid) * CmToM + off;
                log.Add("APPROX " + name + ": sphere radius range mapped to Unity radius thickness");
            }
            if (!asBox && posSpread) log.Add("APPROX " + name + ": initial position range collapsed to its midpoint because the " + prim + " shape is also used");
            log.Add("SHAPE " + name + ": Unity " + shape.shapeType + " scale " + shape.scale.ToString("0.###") + " m at " + shape.position.ToString("0.###") + " m; Unreal static spread " + Cm3(sLo) + " to " + Cm3(sHi) + " cm" + (hasReplay ? ", replayed spawn spread " + Cm3(rLo) + " to " + Cm3(rHi) + " cm" : ", no replay spread available"));
            return;
        }
        if (hasPos || hasReplay)
        {
            Vector3 lo = hasPos ? posMin : rLo, hi = hasPos ? posMax : rHi;
            if (hasPos && hasReplay)
            {
                lo = Vector3.Min(lo, rLo);
                hi = Vector3.Max(hi, rHi);
            }
            shape.enabled = true;
            ApplyBox(shape, lo, hi);
            log.Add("SHAPE " + name + ": Unity Box scale " + shape.scale.ToString("0.###") + " m at " + shape.position.ToString("0.###") + " m from the spawn spread " + Cm3(lo) + " to " + Cm3(hi) + " cm");
        }
        else shape.enabled = false;
    }

    static Vector3 VecMid(object dist, Vector3 def)
    {
        var d = dist as Dictionary<string, object>;
        if (d == null) return def;
        Vector3 a, b;
        VecRange(d, out a, out b);
        return (a + b) * 0.5f;
    }

    static void BuildDirectionalVelocity(ParticleSystem ps, Dictionary<string, object> mod, string name, string vtype, Vector3 mid)
    {
        string key = vtype == "InCone" ? "cone" : "point";
        if (!mod.ContainsKey(key) || mod[key] == null) return;
        var d = D(mod[key]);
        float s1, s2, k1, k2;
        ScalarRange(d.ContainsKey("speed") ? d["speed"] : null, vtype == "InCone" ? 500 : 100, out s1, out s2);
        ScalarRange(d.ContainsKey("scale") ? d["scale"] : null, 1, out k1, out k2);
        float k = (k1 + k2) * 0.5f;
        var main = ps.main;
        main.startSpeed = new ParticleSystem.MinMaxCurve(s1 * k * CmToM, s2 * k * CmToM);
        var shape = ps.shape;
        shape.enabled = true;
        shape.radius = 0.0001f;
        shape.radiusThickness = 1f;
        shape.position = P(mid) * CmToM;
        if (vtype == "InCone")
        {
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = (float)N(d.ContainsKey("angle") ? d["angle"] : null, 45) * 0.5f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            if (d.ContainsKey("rotation") && d["rotation"] != null && VecMid(d["rotation"], Vector3.zero).sqrMagnitude > 0.0001f)
                log.Add("NOT APPLIED " + name + ": cone rotation " + VecMid(d["rotation"], Vector3.zero).ToString());
        }
        else
        {
            shape.shapeType = ParticleSystemShapeType.Sphere;
            if (d.ContainsKey("origin") && d["origin"] != null) shape.position = P(V3(d["origin"])) * CmToM;
        }
        log.Add("APPROX " + name + ": " + vtype + " velocity mapped to a Unity " + (vtype == "InCone" ? "cone" : "sphere") + " shape with start speed (drag is not applied to it)");
    }

    static void BuildForces(ParticleSystem ps, Dictionary<string, object> mod, string name)
    {
        Vector3 fmin = Vector3.zero, fmax = Vector3.zero;
        bool any = false;
        foreach (string key in new string[] { "gravity", "acceleration" })
        {
            if (!mod.ContainsKey(key) || mod[key] == null) continue;
            Vector3 a, b;
            VecRange(mod[key], out a, out b);
            fmin += P(a) * CmToM;
            fmax += P(b) * CmToM;
            any = true;
        }
        if (!any) return;
        var f = ps.forceOverLifetime;
        f.enabled = true;
        f.space = ParticleSystemSimulationSpace.Local;
        f.x = new ParticleSystem.MinMaxCurve(Mathf.Min(fmin.x, fmax.x), Mathf.Max(fmin.x, fmax.x));
        f.y = new ParticleSystem.MinMaxCurve(Mathf.Min(fmin.y, fmax.y), Mathf.Max(fmin.y, fmax.y));
        f.z = new ParticleSystem.MinMaxCurve(Mathf.Min(fmin.z, fmax.z), Mathf.Max(fmin.z, fmax.z));
        log.Add("UNVERIFIED " + name + ": gravity/acceleration applied as a Unity force over lifetime in local space (" + fmin.ToString() + " to " + fmax.ToString() + " m/s^2)");
    }

    static void BuildRotation(ParticleSystem ps, Dictionary<string, object> mod, bool isMesh, string name)
    {
        if (isMesh) return;
        var main = ps.main;
        float a, b;
        if (mod.ContainsKey("rotation") && mod["rotation"] != null)
        {
            ScalarRange(mod["rotation"], 0, out a, out b);
            main.startRotation = new ParticleSystem.MinMaxCurve(a * Mathf.Deg2Rad, b * Mathf.Deg2Rad);
        }
        if (mod.ContainsKey("rotationRate") && mod["rotationRate"] != null)
        {
            ScalarRange(mod["rotationRate"], 0, out a, out b);
            var rol = ps.rotationOverLifetime;
            rol.enabled = true;
            rol.z = new ParticleSystem.MinMaxCurve(a * Mathf.Deg2Rad, b * Mathf.Deg2Rad);
            log.Add("UNVERIFIED " + name + ": sprite rotation rate " + a.ToString("0.###") + " to " + b.ToString("0.###") + " deg/s; the spin direction against Unreal's is not confirmed");
        }
    }

    static void BuildVelocity(ParticleSystem ps, Dictionary<string, object> mod, float lmin, float lmax, string name)
    {
        if (!mod.ContainsKey("velocity") || mod["velocity"] == null) return;
        Vector3 vmin, vmax;
        VecRange(mod["velocity"], out vmin, out vmax);
        Vector3 a = P(vmin) * CmToM, b = P(vmax) * CmToM;
        if (mod.ContainsKey("velocityScale") && mod["velocityScale"] != null)
        {
            float k1, k2;
            ScalarRange(mod["velocityScale"], 1, out k1, out k2);
            float k = (k1 + k2) * 0.5f;
            a *= k;
            b *= k;
        }
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

    static Vector3 Triple(object o)
    {
        var t = L(o);
        return new Vector3((float)N(t[0], 0), (float)N(t[1], 0), (float)N(t[2], 0));
    }

    static void BuildMotionFit(ParticleSystem ps, Dictionary<string, object> mod, string name)
    {
        if (!mod.ContainsKey("velocityFit") || mod["velocityFit"] == null) return;
        var vf = D(mod["velocityFit"]);
        float life = Mathf.Max(0.05f, (float)N(vf.ContainsKey("life") ? vf["life"] : null, 1.0));
        if (vf.ContainsKey("speed") && vf["speed"] != null)
        {
            var sp = L(vf["speed"]);
            var c = new AnimationCurve();
            for (int i = 0; i < sp.Count; i++) c.AddKey(sp.Count > 1 ? i / (float)(sp.Count - 1) : 0f, (float)N(sp[i], 1));
            for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
            var vol = ps.velocityOverLifetime;
            vol.enabled = true;
            vol.space = ParticleSystemSimulationSpace.Local;
            vol.speedModifier = new ParticleSystem.MinMaxCurve(1f, c);
            log.Add("FIT " + name + ": speed over lifetime from the replay (x" + ((float)N(sp[sp.Count - 1], 1)).ToString("0.##") + " by the end of life) as Velocity over Lifetime's Speed Modifier");
        }
        if (vf.ContainsKey("added") && vf["added"] != null)
        {
            var ad = L(vf["added"]);
            int n = ad.Count;
            if (n >= 3)
            {
                var vel = new Vector3[n];
                for (int i = 0; i < n; i++) vel[i] = P(Triple(ad[i])) * CmToM;
                var cx = new AnimationCurve();
                var cy = new AnimationCurve();
                var cz = new AnimationCurve();
                float step = 1f / (n - 1);
                for (int i = 0; i < n; i++)
                {
                    int lo = Mathf.Max(i - 1, 0), hi = Mathf.Min(i + 1, n - 1);
                    Vector3 acc = (vel[hi] - vel[lo]) / ((hi - lo) * step * life);
                    cx.AddKey(i * step, acc.x);
                    cy.AddKey(i * step, acc.y);
                    cz.AddKey(i * step, acc.z);
                }
                for (int i = 0; i < n; i++) { cx.SmoothTangents(i, 0f); cy.SmoothTangents(i, 0f); cz.SmoothTangents(i, 0f); }
                var f = ps.forceOverLifetime;
                f.enabled = true;
                f.space = ParticleSystemSimulationSpace.Local;
                f.x = new ParticleSystem.MinMaxCurve(1f, cx);
                f.y = new ParticleSystem.MinMaxCurve(1f, cy);
                f.z = new ParticleSystem.MinMaxCurve(1f, cz);
                log.Add("FIT " + name + ": gravity and other forces from the replay (the velocity every particle gains, " + vel[n - 1].ToString("0.##") + " m/s by the end of life) as Force over Lifetime");
            }
        }
    }

    static void BuildRotationFit(ParticleSystem ps, Dictionary<string, object> mod, string name)
    {
        if (!mod.ContainsKey("rotationFit") || mod["rotationFit"] == null) return;
        var rf = D(mod["rotationFit"]);
        var delta = L(rf["delta"]);
        int n = delta.Count;
        if (n < 3) return;
        float life = Mathf.Max(0.05f, (float)N(rf.ContainsKey("life") ? rf["life"] : null, 1.0));
        var c = new AnimationCurve();
        float step = 1f / (n - 1);
        for (int i = 0; i < n; i++)
        {
            int lo = Mathf.Max(i - 1, 0), hi = Mathf.Min(i + 1, n - 1);
            float w = ((float)N(delta[hi], 0) - (float)N(delta[lo], 0)) / ((hi - lo) * step * life);
            c.AddKey(i * step, w * Mathf.Deg2Rad);
        }
        for (int i = 0; i < n; i++) c.SmoothTangents(i, 0f);
        var rol = ps.rotationOverLifetime;
        rol.enabled = true;
        rol.separateAxes = false;
        rol.z = new ParticleSystem.MinMaxCurve(1f, c);
        log.Add("FIT " + name + ": sprite rotation over its life from the replay (" + ((float)N(delta[n - 1], 0)).ToString("0.#") + " degrees turned by the end) as Rotation over Lifetime; the spin direction against Unreal's is not confirmed");
    }

    static void BuildRibbonParticle(ParticleSystem ps, ParticleSystemRenderer pr, Dictionary<string, object> mod, Dictionary<string, object> r, string name)
    {
        var main = ps.main;
        main.startSize = 1f;
        pr.renderMode = ParticleSystemRenderMode.None;
        var tr = ps.trails;
        tr.enabled = true;
        tr.mode = ParticleSystemTrailMode.Ribbon;
        tr.ribbonCount = 1;
        tr.ratio = 1f;
        tr.lifetime = 1f;
        tr.minVertexDistance = 0.02f;
        tr.worldSpace = false;
        tr.dieWithParticles = true;
        tr.textureMode = ParticleSystemTrailTextureMode.Stretch;
        tr.sizeAffectsWidth = false;
        tr.sizeAffectsLifetime = false;
        tr.inheritParticleColor = true;
        if (mod.ContainsKey("ribbonWidth") && mod["ribbonWidth"] != null)
        {
            var w = L(mod["ribbonWidth"]);
            var c = new AnimationCurve();
            for (int i = 0; i < w.Count; i++) c.AddKey(w.Count > 1 ? i / (float)(w.Count - 1) : 0f, Mathf.Max(0f, (float)N(w[i], 10)) * CmToM);
            for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
            tr.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, c);
        }
        else tr.widthOverTrail = new ParticleSystem.MinMaxCurve(0.1f);
        string matName = r.ContainsKey("material") ? S(r["material"]) : "";
        var mat = FindMaterial(matName);
        if (mat == null && !string.IsNullOrEmpty(matName)) Miss("material", matName, "its converted shader");
        pr.trailMaterial = mat;
        log.Add("APPROX " + name + ": ribbon renderer built as the Trails module in Ribbon mode (one ribbon joining the particles in birth order, width over the trail from the replay, stretched UVs); Niagara's ribbon IDs, UV tiling and twist are not carried");
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
        string sortMode = r.ContainsKey("sortMode") ? S(r["sortMode"]) : "";
        if (sortMode == "ViewDepth" || sortMode == "ViewDistance") pr.sortMode = ParticleSystemSortMode.Distance;
        if (S(r["alignment"]) == "VelocityAligned")
        {
            pr.renderMode = ParticleSystemRenderMode.Stretch;
            pr.velocityScale = 0f;
            pr.lengthScale = 1f;
            log.Add("APPROX " + name + ": velocity-aligned sprite mapped to Stretched Billboard (check width/length visually)");
        }
        else
        {
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            // UE sprites keep their own facing: a fixed plane stays a fixed plane, so parallax shaders (BumpOffset portals) show real depth.
            string sprFacing = r.ContainsKey("facing") ? S(r["facing"]) : "FaceCamera";
            if (sprFacing == "CustomFacingVector") pr.alignment = ParticleSystemRenderSpace.Local;
            else if (sprFacing == "FaceCameraPosition") pr.alignment = ParticleSystemRenderSpace.Facing;
            else pr.alignment = ParticleSystemRenderSpace.View;
            log.Add("FACING " + name + ": sprite facing " + sprFacing + " -> Unity render alignment " + pr.alignment);
        }
        var sub = L(r["subImage"]);
        int tilesX = Mathf.Max(1, Mathf.RoundToInt((float)N(sub[0], 1)));
        int tilesY = Mathf.Max(1, Mathf.RoundToInt((float)N(sub[1], 1)));
        if (tilesX > 1 || tilesY > 1)
        {
            // Without this the whole atlas shows on one quad. Frame 0 unless a SubUV module animates it over the lifetime.
            var tsa = ps.textureSheetAnimation;
            tsa.enabled = true;
            tsa.mode = ParticleSystemAnimationMode.Grid;
            tsa.numTilesX = tilesX;
            tsa.numTilesY = tilesY;
            tsa.animation = ParticleSystemAnimationType.WholeSheet;
            tsa.cycleCount = 1;
            bool animated = mod.ContainsKey("subUV") && mod["subUV"] != null;
            float sheet = tilesX * tilesY;
            float used = sheet;
            bool randomFrame = false;
            if (animated)
            {
                var su = D(mod["subUV"]);
                used = Mathf.Clamp((float)N(su.ContainsKey("frames") ? su["frames"] : null, sheet), 1f, sheet);
                randomFrame = su.ContainsKey("mode") && S(su["mode"]).IndexOf("Random", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            float span = used / sheet;
            if (animated && randomFrame)
            {
                tsa.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
                tsa.startFrame = new ParticleSystem.MinMaxCurve(0f, Mathf.Max(0f, span - 1f / sheet));
            }
            else
            {
                tsa.frameOverTime = animated ? new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, span)) : new ParticleSystem.MinMaxCurve(0f);
                tsa.startFrame = new ParticleSystem.MinMaxCurve(0f);
            }
            log.Add("UNVERIFIED " + name + ": sub-image grid " + tilesX + "x" + tilesY + " mapped to Texture Sheet Animation, " + (!animated ? "frame 0 only" : randomFrame ? "a random frame of the first " + used + " each particle" : "the first " + used + " frames play once over the lifetime") + "; the converted material must use the particle UVs for this to show");
        }
        var pv = L(r["pivot"]);
        float pvx = (float)N(pv[0], 0.5), pvy = (float)N(pv[1], 0.5);
        if (Mathf.Abs(pvx - 0.5f) > 0.001f || Mathf.Abs(pvy - 0.5f) > 0.001f)
        {
            pr.pivot = new Vector3(pvx - 0.5f, 0.5f - pvy, 0f);
            log.Add("UNVERIFIED " + name + ": sprite pivot (" + pvx + ", " + pvy + ") set as the renderer's Pivot " + pr.pivot + "; if the sprite sits on the wrong side of its position, flip the sign of the Pivot");
        }
    }

    static void BuildMeshParticle(ParticleSystem ps, ParticleSystemRenderer pr, Dictionary<string, object> mod, Dictionary<string, object> r, string name)
    {
        string meshName = S(r["mesh"]);
        var mesh = FindMesh(meshName);
        if (mesh == null) Miss("mesh", meshName, meshWhy);
        string facing = r.ContainsKey("facing") ? S(r["facing"]) : "Default";
        bool faced = facing == "CameraPosition" || facing == "CameraPlane" || facing == "Velocity";
        bool negativeX = PorterBlueprintSetup.MeshArrivesMirroredX;
        bool mirroredMeshes = false;
        if (negativeX && faced)
        {
            pr.flip = new Vector3(1f, 0f, 0f);
            negativeX = false;
        }
        if (negativeX && mesh != null)
        {
            var mirrored = MirroredMesh(mesh, meshName);
            if (mirrored != null)
            {
                mesh = mirrored;
                negativeX = false;
                mirroredMeshes = true;
            }
            else log.Add("CHECK " + name + ": could not build a mirrored copy of mesh '" + meshName + "', using a negative X particle size (the mesh may draw inside out)");
        }
        pr.renderMode = ParticleSystemRenderMode.Mesh;
        pr.mesh = mesh;
        var meshSet = new List<Mesh>();
        if (mesh != null) meshSet.Add(mesh);
        int wanted = meshSet.Count;
        if (r.ContainsKey("meshes") && r["meshes"] != null)
        {
            foreach (var mo in L(r["meshes"]))
            {
                string extraName = S(mo);
                if (string.IsNullOrEmpty(extraName) || extraName == meshName) continue;
                var extra = FindMesh(extraName);
                if (extra == null)
                {
                    Miss("mesh", extraName, meshWhy);
                    continue;
                }
                if (mirroredMeshes)
                {
                    var extraMirrored = MirroredMesh(extra, extraName);
                    if (extraMirrored != null) extra = extraMirrored;
                    else log.Add("CHECK " + name + ": could not build a mirrored copy of mesh '" + extraName + "', it may draw inside out");
                }
                if (meshSet.Contains(extra)) continue;
                wanted++;
                if (meshSet.Count < 4) meshSet.Add(extra);
            }
        }
        if (meshSet.Count > 1)
        {
            pr.SetMeshes(meshSet.ToArray());
            log.Add("APPROX " + name + ": the renderer lists " + meshSet.Count + " meshes; Unity picks one at random for each particle, Unreal picks by the particle's mesh index");
        }
        if (wanted > 4) log.Add("NOT APPLIED " + name + ": the renderer lists " + wanted + " meshes but a Unity renderer holds 4, the first 4 are used");
        var mats = L(r["materials"]);
        if (mats.Count > 0)
        {
            var arr = new Material[mats.Count];
            for (int i = 0; i < arr.Length; i++)
            {
                string mn = S(mats[i]);
                arr[i] = FindMaterial(mn);
                if (arr[i] == null && !string.IsNullOrEmpty(mn)) Miss("material", mn, "its converted shader");
            }
            pr.sharedMaterials = arr;
        }
        var rot = V3(r["meshRotation"]);
        Quaternion q = PorterBlueprintSetup.RotatorToQuaternion(rot.x, rot.y, rot.z);
        Vector3 orient = Vector3.zero;
        bool orientRange = false;
        bool hasOrient = false;
        if (mod.ContainsKey("meshOrientation"))
            hasOrient = SafeVecMid(mod["meshOrientation"], out orient, out orientRange) && orient.sqrMagnitude > 1e-6f;
        if (faced)
        {
            pr.alignment = facing == "CameraPosition" ? ParticleSystemRenderSpace.Facing : facing == "CameraPlane" ? ParticleSystemRenderSpace.View : ParticleSystemRenderSpace.Velocity;
            q = Quaternion.Euler(0f, -90f, 0f) * q;
            log.Add("UNVERIFIED " + name + ": mesh facing mode " + facing + " mapped to Unity render alignment " + pr.alignment + ", with the mesh turned -90 degrees about Y and Flip X = 1 (matches the setup that worked by hand for the Reboot logo: View, Flip X 1, rotation Y about 100)");
            if (hasOrient)
                log.Add("NOT APPLIED " + name + ": initial mesh orientation (roll, pitch, yaw) " + orient.ToString() + ", because the facing mode " + facing + " sets the orientation");
        }
        else
        {
            pr.alignment = ParticleSystemRenderSpace.Local;
            if (hasOrient)
            {
                q = PorterBlueprintSetup.RotatorToQuaternion(orient.y, orient.z, orient.x) * q;
                log.Add("UNVERIFIED " + name + ": initial mesh orientation (roll, pitch, yaw) " + orient.ToString() + " applied before the renderer mesh rotation" + (orientRange ? "; it is a random range in Unreal, the middle of the range is used" : ""));
            }
        }
        Vector3 euler = q.eulerAngles * Mathf.Deg2Rad;
        var main = ps.main;
        main.startRotation3D = true;
        main.startRotationX = euler.x;
        main.startRotationY = euler.y;
        main.startRotationZ = euler.z;
        Vector3 ms = r.ContainsKey("meshScale") && r["meshScale"] != null ? P(V3(r["meshScale"])) : Vector3.one;
        Vector3 modScale = Vector3.one;
        bool scaleRange = false;
        if (mod.ContainsKey("meshScale") && SafeVecMid(mod["meshScale"], out modScale, out scaleRange)
            && Mathf.Abs(modScale.x) > 1e-4f && Mathf.Abs(modScale.y) > 1e-4f && Mathf.Abs(modScale.z) > 1e-4f)
        {
            ms = Vector3.Scale(ms, P(modScale));
            if (scaleRange) log.Add("APPROX " + name + ": initial mesh scale is a random range in Unreal, the middle " + modScale.ToString() + " is used");
        }
        main.startSize3D = true;
        main.startSizeX = (negativeX ? -1f : 1f) * ms.x;
        main.startSizeY = ms.y;
        main.startSizeZ = ms.z;
        if (mod.ContainsKey("scaleMeshSize") && mod["scaleMeshSize"] != null)
        {
            var values = L(D(mod["scaleMeshSize"])["values"]);
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            bool perAxis = false;
            for (int vi = 0; vi < values.Count && !perAxis; vi++)
            {
                var vd = D(values[vi]);
                if (vd.ContainsKey("Y") && vd.ContainsKey("Z") && (Math.Abs(N(vd["Y"], 1) - N(vd["X"], 1)) > 0.02 || Math.Abs(N(vd["Z"], 1) - N(vd["X"], 1)) > 0.02)) perAxis = true;
            }
            if (perAxis)
            {
                sol.separateAxes = true;
                sol.x = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "X"));
                sol.y = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "Z"));
                sol.z = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "Y"));
                log.Add("UNVERIFIED " + name + ": mesh scale changes differently on each axis over its life, applied as Unity's separate-axis size over lifetime with Unreal's Y and Z swapped like the other axes");
            }
            else
            {
                sol.separateAxes = false;
                sol.size = new ParticleSystem.MinMaxCurve(1f, CurveFrom(values, "X"));
            }
        }
        if (r.ContainsKey("lockedAxis") && r["lockedAxis"] != null)
            log.Add("NOT APPLIED " + name + ": mesh locked axis " + V3(r["lockedAxis"]).ToString() + " (Unity has no locked axis for mesh particles)");
        if (r.ContainsKey("pivotOffset") && V3(r["pivotOffset"]).sqrMagnitude > 0.0001f)
            log.Add("NOT APPLIED " + name + ": mesh renderer pivot offset " + V3(r["pivotOffset"]).ToString());
        if (mod.ContainsKey("dynamicParams") && mod["dynamicParams"] != null)
            log.Add("NOT APPLIED " + name + ": dynamic material parameters (shader keeps its default values)");
        log.Add("UNVERIFIED " + name + ": mesh particle orientation uses the same axis mapping as the sky dome meshes");
    }

    static Mesh MirroredMesh(Mesh src, string meshName)
    {
        try
        {
            string dir = specFolder + "/ParticleMeshes";
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(specFolder, "ParticleMeshes");
            string path = dir + "/" + Safe(meshName) + "_mirrored.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (src.vertexCount == 0 || src.vertices.Length == 0) return null;
            var m = existing != null ? existing : new Mesh();
            m.Clear();
            m.name = Safe(meshName) + "_mirrored";
            m.indexFormat = src.indexFormat;
            var v = src.vertices;
            if (v.Length == 0 || v.Length != src.vertexCount) return null;
            for (int i = 0; i < v.Length; i++) v[i].x = -v[i].x;
            m.vertices = v;
            var n = src.normals;
            if (n.Length == v.Length)
            {
                for (int i = 0; i < n.Length; i++) n[i].x = -n[i].x;
                m.normals = n;
            }
            var t = src.tangents;
            if (t.Length == v.Length)
            {
                for (int i = 0; i < t.Length; i++) t[i] = new Vector4(-t[i].x, t[i].y, t[i].z, -t[i].w);
                m.tangents = t;
            }
            m.uv = src.uv;
            m.uv2 = src.uv2;
            var c = src.colors;
            if (c.Length == v.Length) m.colors = c;
            m.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = src.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int tmp = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = tmp;
                }
                m.SetTriangles(tris, s);
            }
            m.RecalculateBounds();
            if (existing == null) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
            return m;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        return sb.ToString();
    }

    static Color StreamCol(object o)
    {
        var d = D(o);
        return new Color((float)N(d["r"], 1), (float)N(d["g"], 1), (float)N(d["b"], 1), (float)N(d["a"], 1));
    }

    static AnimationCurve CurveOf(List<object> values, out float mid)
    {
        var c = new AnimationCurve();
        int n = values.Count;
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float v = (float)N(values[i], 0);
            sum += v;
            c.AddKey(new Keyframe(n > 1 ? i / (float)(n - 1) : 0f, v));
        }
        LinearTangents(c);
        mid = n > 0 ? sum / n : 0f;
        return c;
    }

    static ParticleSystem.MinMaxCurve DataCurve(Dictionary<string, object> c, string mode, out float mid)
    {
        if (mode == "curve")
            return new ParticleSystem.MinMaxCurve(1f, CurveOf(L(c["values"]), out mid));
        if (mode == "range")
        {
            float a = (float)N(c["min"], 0), b = (float)N(c["max"], 0);
            mid = (a + b) * 0.5f;
            return new ParticleSystem.MinMaxCurve(a, b);
        }
        float m1, m2;
        var lo = CurveOf(L(c["min"]), out m1);
        var hi = CurveOf(L(c["max"]), out m2);
        mid = (m1 + m2) * 0.5f;
        return new ParticleSystem.MinMaxCurve(1f, lo, hi);
    }

    static bool BuildColorStreams(ParticleSystem ps, ParticleSystemRenderer pr, Dictionary<string, object> mod, string name, Dictionary<string, object> rend)
    {
        var shared = pr.sharedMaterials;
        if (shared == null || shared.Length == 0) return false;
        foreach (var m in shared)
            if (m == null || !m.HasProperty("_PorterParticleStreams")) return false;
        var st = rend != null && rend.ContainsKey("streams") && rend["streams"] != null ? D(rend["streams"]) : null;
        List<object> streamColor = st != null && st.ContainsKey("color") && st["color"] != null ? L(st["color"]) : null;
        if (streamColor != null && streamColor.Count < 2) streamColor = null;

        Color cmin = Color.white, cmax = Color.white;
        if (mod.ContainsKey("color") && mod["color"] != null) ColorRange(mod["color"], out cmin, out cmax);
        Color start = (cmin + cmax) * 0.5f;
        if (Mathf.Abs(cmin.r - cmax.r) + Mathf.Abs(cmin.g - cmax.g) + Mathf.Abs(cmin.b - cmax.b) + Mathf.Abs(cmin.a - cmax.a) > 0.002f)
            log.Add("APPROX " + name + ": random start colour range averaged");
        List<object> values = mod.ContainsKey("scaleColor") && mod["scaleColor"] != null ? L(D(mod["scaleColor"])["values"]) : null;
        int n = values == null ? 0 : values.Count;
        if (streamColor != null)
        {
            n = streamColor.Count;
            start = Color.white;
        }
        var cr = new AnimationCurve();
        var cg = new AnimationCurve();
        var cb = new AnimationCurve();
        var ca = new AnimationCurve();
        int steps = n > 1 ? n : 1;
        float maxV = 0f;
        for (int i = 0; i < steps; i++)
        {
            float t = steps > 1 ? i / (float)(steps - 1) : 0f;
            Color c = streamColor != null ? StreamCol(streamColor[i]) : (n > 0 ? Col(values[i]) : Color.white);
            cr.AddKey(new Keyframe(t, start.r * c.r));
            cg.AddKey(new Keyframe(t, start.g * c.g));
            cb.AddKey(new Keyframe(t, start.b * c.b));
            ca.AddKey(new Keyframe(t, start.a * c.a));
            maxV = Mathf.Max(maxV, start.r * c.r, start.g * c.g, start.b * c.b);
        }
        LinearTangents(cr);
        LinearTangents(cg);
        LinearTangents(cb);
        LinearTangents(ca);

        var main = ps.main;
        main.startColor = new ParticleSystem.MinMaxGradient(Color.white);
        var col = ps.colorOverLifetime;
        col.enabled = false;
        var cd = ps.customData;
        cd.enabled = true;
        cd.SetMode(ParticleSystemCustomData.Custom1, ParticleSystemCustomDataMode.Vector);
        cd.SetVectorComponentCount(ParticleSystemCustomData.Custom1, 4);
        cd.SetVector(ParticleSystemCustomData.Custom1, 0, new ParticleSystem.MinMaxCurve(1f, cr));
        cd.SetVector(ParticleSystemCustomData.Custom1, 1, new ParticleSystem.MinMaxCurve(1f, cg));
        cd.SetVector(ParticleSystemCustomData.Custom1, 2, new ParticleSystem.MinMaxCurve(1f, cb));
        cd.SetVector(ParticleSystemCustomData.Custom1, 3, new ParticleSystem.MinMaxCurve(1f, ca));
        var dpList = st != null && st.ContainsKey("dp") && st["dp"] != null ? L(st["dp"]) : new List<object>();
        var slotCurves = new ParticleSystem.MinMaxCurve[4];
        for (int q = 0; q < 4; q++) slotCurves[q] = new ParticleSystem.MinMaxCurve(0f);
        int slot = 0;
        var dpBase = new Dictionary<int, Vector4>();
        var dpSlot = new Dictionary<int, Vector4>();
        foreach (var dpo in dpList)
        {
            var dp = D(dpo);
            int idx = (int)N(dp["index"], 0);
            var comps = L(dp["comps"]);
            var bv = Vector4.zero;
            var sv = new Vector4(-1f, -1f, -1f, -1f);
            for (int k = 0; k < 4 && k < comps.Count; k++)
            {
                var cc = D(comps[k]);
                string mode = S(cc["mode"]);
                if (mode == "const")
                {
                    bv[k] = (float)N(cc["value"], 0);
                    continue;
                }
                float mid;
                var curve = DataCurve(cc, mode, out mid);
                if (slot >= 4)
                {
                    bv[k] = mid;
                    log.Add("NOT APPLIED " + name + ": " + S(dp["attr"]) + " component " + "xyzw"[k] + " has no free Custom Data slot, held at " + mid.ToString("0.###"));
                    continue;
                }
                slotCurves[slot] = curve;
                sv[k] = slot;
                slot++;
            }
            dpBase[idx] = bv;
            dpSlot[idx] = sv;
        }
        bool wantAge = false, wantRand = false;
        foreach (var sm in shared)
        {
            if (sm.HasProperty("_PorterAgeSlot")) wantAge = true;
            if (sm.HasProperty("_PorterRandSlot")) wantRand = true;
        }
        float ageSlot = -1f, randSlot = -1f;
        if (wantAge)
        {
            if (slot < 4)
            {
                slotCurves[slot] = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
                ageSlot = slot;
                slot++;
            }
            else log.Add("NOT APPLIED " + name + ": the shader reads the particle age (ParticleRelativeTime) but all 4 Custom Data slots are taken by dynamic parameters, so it reads 0");
        }
        if (wantRand)
        {
            if (slot < 4)
            {
                slotCurves[slot] = new ParticleSystem.MinMaxCurve(0f, 1f);
                randSlot = slot;
                slot++;
            }
            else log.Add("NOT APPLIED " + name + ": the shader reads the particle random number (ParticleRandom) but all 4 Custom Data slots are taken, so it reads 0");
        }
        if (slot > 0)
        {
            cd.SetMode(ParticleSystemCustomData.Custom2, ParticleSystemCustomDataMode.Vector);
            cd.SetVectorComponentCount(ParticleSystemCustomData.Custom2, 4);
            for (int q = 0; q < 4; q++) cd.SetVector(ParticleSystemCustomData.Custom2, q, slotCurves[q]);
        }
        var streams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position,
            ParticleSystemVertexStream.Normal,
            ParticleSystemVertexStream.Color,
            ParticleSystemVertexStream.UV,
            ParticleSystemVertexStream.UV2,
            ParticleSystemVertexStream.Custom1XYZW,
            ParticleSystemVertexStream.Tangent
        };
        if (slot > 0) streams.Insert(streams.Count - 1, ParticleSystemVertexStream.Custom2XYZW);
        pr.SetActiveVertexStreams(streams);
        var copies = new Material[shared.Length];
        for (int i = 0; i < shared.Length; i++) copies[i] = StreamCopy(shared[i], name);
        foreach (var kv in dpBase)
        {
            foreach (var cm in copies)
            {
                string bp = "_DP_Dynamic_Parameter_" + kv.Key;
                string sp = "_PorterDPSlot" + kv.Key;
                if (cm.HasProperty(bp)) cm.SetVector(bp, kv.Value);
                var sv2 = dpSlot[kv.Key];
                if (cm.HasProperty(sp)) cm.SetVector(sp, sv2);
                else if (sv2.x >= 0f || sv2.y >= 0f || sv2.z >= 0f || sv2.w >= 0f)
                    log.Add("CHECK " + name + ": material " + cm.name + " has no " + sp + ", convert its shader again with this version so Dynamic Parameter " + kv.Key + " follows the particle");
                EditorUtility.SetDirty(cm);
            }
        }
        foreach (var cm in copies)
        {
            if (cm.HasProperty("_PorterAgeSlot")) cm.SetFloat("_PorterAgeSlot", ageSlot);
            if (cm.HasProperty("_PorterRandSlot")) cm.SetFloat("_PorterRandSlot", randSlot);
            EditorUtility.SetDirty(cm);
        }
        if (slot > 0) log.Add("STREAMS " + name + ": " + slot + " per-particle value(s) sent to the shader in Custom Data 2 (dynamic parameters" + (ageSlot >= 0f ? ", particle age" : "") + (randSlot >= 0f ? ", particle random" : "") + "), the rest set as fixed values on the material copy");
        pr.sharedMaterials = copies;
        log.Add("STREAMS " + name + ": particle colour (start colour x colour scale, up to " + maxV.ToString("0.###") + ", not clamped) is sent to the shader in Custom Data 1, so mesh vertex colours stay separate; materials copied per emitter with _PorterParticleStreams = 1");
        return true;
    }

    static void LinearTangents(AnimationCurve c)
    {
        int n = c.length;
        for (int i = 0; i < n; i++)
        {
            var k = c[i];
            float inT = i > 0 ? (k.value - c[i - 1].value) / Mathf.Max(1e-6f, k.time - c[i - 1].time) : 0f;
            float outT = i < n - 1 ? (c[i + 1].value - k.value) / Mathf.Max(1e-6f, c[i + 1].time - k.time) : 0f;
            if (i == 0) inT = outT;
            if (i == n - 1) outT = inT;
            k.inTangent = inT;
            k.outTangent = outT;
            c.MoveKey(i, k);
        }
    }

    static Material StreamCopy(Material src, string emitter)
    {
        string srcPath = AssetDatabase.GetAssetPath(src);
        string dir = string.IsNullOrEmpty(srcPath) ? specFolder : Path.GetDirectoryName(srcPath).Replace('\\', '/');
        string path = dir + "/" + Safe(src.name) + "_" + Safe(currentBlueprint) + "_" + Safe(emitter) + ".mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        Material inst;
        if (existing != null)
        {
            EditorUtility.CopySerialized(src, existing);
            existing.name = Path.GetFileNameWithoutExtension(path);
            inst = existing;
        }
        else
        {
            inst = new Material(src);
            AssetDatabase.CreateAsset(inst, path);
        }
        inst.SetFloat("_PorterParticleStreams", 1f);
        EditorUtility.SetDirty(inst);
        return inst;
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

    static bool SafeVecMid(object dist, out Vector3 mid, out bool ranged)
    {
        mid = Vector3.zero;
        ranged = false;
        if (!(dist is Dictionary<string, object>)) return false;
        try
        {
            Vector3 a, b;
            VecRange(dist, out a, out b);
            mid = (a + b) * 0.5f;
            ranged = (a - b).sqrMagnitude > 1e-6f;
            return true;
        }
        catch (Exception)
        {
            return false;
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
