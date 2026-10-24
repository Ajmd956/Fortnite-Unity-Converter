#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the baked-replay players of a converted particle system (see PorterBakedParticleSystem.cs).
public static partial class PorterPropBuilder
{
    // Unity light intensity per unit of Unreal light colour brightness. Unreal and Unity light units differ:
    // this is a starting value to match by eye against Unreal, not a measured one.
    const float ReplayLightScale = 1f;

    // ---- pink fallback ------------------------------------------------------------------------------------------------
    // A material whose shader is missing or does not compile draws pink. It is moved to Porter/ParticleFallback (unlit, the base
    // texture times the tint) so the effect still shows. The old material's texture and colour are read from the saved asset,
    // which still holds them when the shader is gone.

    static Shader fallbackShader;

    public static Shader FallbackShader()
    {
        if (fallbackShader != null) return fallbackShader;
        fallbackShader = Shader.Find("Porter/ParticleFallback");
        if (fallbackShader != null) return fallbackShader;
        foreach (string guid in AssetDatabase.FindAssets("PorterParticleFallback t:Shader"))
        {
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
            if (sh != null && sh.name == "Porter/ParticleFallback") { fallbackShader = sh; break; }
        }
        return fallbackShader;
    }

    static readonly string[] FallbackTextures = { "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseTexture", "_Texture", "_DiffuseTexture", "_Diffuse", "_Albedo", "_Tex", "_ParticleTexture" };
    static readonly string[] FallbackColors = { "_Color", "_BaseColor", "_TintColor", "_Tint", "_ColorTint", "_EmissiveColor", "_Emissive", "_EmissionColor" };

    static bool Contains(string text, string part)
    {
        return text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Moves one material to the fallback shader and carries over its main texture, colour and blend. Returns the log line, or null when
    // the fallback shader is not in the project (nothing was changed).
    public static string RepairMaterial(Material mat, string why)
    {
        Shader fb = FallbackShader();
        if (mat == null || fb == null || mat.shader == fb) return null;
        var names = new List<string>();
        var texes = new List<Texture>();
        var colorNames = new List<string>();
        var colors = new List<Color>();
        float dst = -1f;
        var so = new SerializedObject(mat);
        var envs = so.FindProperty("m_SavedProperties.m_TexEnvs");
        for (int i = 0; envs != null && i < envs.arraySize; i++)
        {
            var e = envs.GetArrayElementAtIndex(i);
            names.Add(e.FindPropertyRelative("first").stringValue);
            texes.Add(e.FindPropertyRelative("second.m_Texture").objectReferenceValue as Texture);
        }
        var cols = so.FindProperty("m_SavedProperties.m_Colors");
        for (int i = 0; cols != null && i < cols.arraySize; i++)
        {
            var e = cols.GetArrayElementAtIndex(i);
            colorNames.Add(e.FindPropertyRelative("first").stringValue);
            colors.Add(e.FindPropertyRelative("second").colorValue);
        }
        var floats = so.FindProperty("m_SavedProperties.m_Floats");
        for (int i = 0; floats != null && i < floats.arraySize; i++)
        {
            var e = floats.GetArrayElementAtIndex(i);
            if (e.FindPropertyRelative("first").stringValue == "_DstBlend") dst = e.FindPropertyRelative("second").floatValue;
        }
        Texture tex = null;
        string texName = "";
        foreach (string want in FallbackTextures)
        {
            int k = names.IndexOf(want);
            if (k >= 0 && texes[k] != null) { tex = texes[k]; texName = want; break; }
        }
        for (int k = 0; tex == null && k < names.Count; k++)
            if (texes[k] != null && !Contains(names[k], "normal") && !Contains(names[k], "mask") && !Contains(names[k], "noise")) { tex = texes[k]; texName = names[k]; }
        Color tint = Color.white;
        string tintName = "";
        foreach (string want in FallbackColors)
        {
            int k = colorNames.IndexOf(want);
            if (k >= 0) { tint = colors[k]; tintName = want; break; }
        }
        bool additive = Math.Abs(dst - 1f) < 0.01f || (dst < 0f && (Contains(mat.name, "add") || Contains(mat.name, "glow")));
        string oldName = mat.shader != null ? mat.shader.name : "none";
        mat.shader = fb;
        if (tex != null) mat.SetTexture("_MainTex", tex);
        mat.SetColor("_Color", tint);
        mat.SetFloat("_DstBlend", additive ? 1f : 10f);
        mat.renderQueue = 3000;
        EditorUtility.SetDirty(mat);
        return "FALLBACK " + AssetDatabase.GetAssetPath(mat) + ": " + why + " (was " + oldName + "); now Porter/ParticleFallback with "
            + (tex != null ? "texture " + tex.name + " from " + texName : "no texture") + ", "
            + (tintName.Length > 0 ? "colour from " + tintName : "white tint") + ", " + (additive ? "additive" : "alpha") + " blend";
    }

    // The material FindMaterial could not find: a stand-in that shows the effect, not the default material
    static Material FallbackStandIn(string name)
    {
        Shader fb = FallbackShader();
        if (fb == null) return null;
        string dir = string.IsNullOrEmpty(specFolder) ? "Assets" : specFolder;
        string path = dir + "/" + Safe(string.IsNullOrEmpty(name) ? "Missing" : name) + "_Fallback.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(fb);
            mat.SetFloat("_DstBlend", 1f);
            mat.renderQueue = 3000;
            AssetDatabase.CreateAsset(mat, path);
        }
        return mat;
    }


    static void BuildReplayEmitter(GameObject parent, Dictionary<string, object> e)
    {
        if (!e.ContainsKey("baked") || e["baked"] == null) return;
        string emName = S(e["name"]);
        bool loops = true;
        if (e.ContainsKey("loop") && e["loop"] != null) loops = S(D(e["loop"])["behavior"]) != "Once";
        foreach (var bo in L(e["baked"]))
        {
            var b = D(bo);
            string kind = S(b["kind"]);
            string file = S(b["file"]);
            string path = specFolder + "/" + file;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var data = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (data == null)
            {
                log.Add("MISSING " + emName + ": baked replay file " + file + " was not found in " + specFolder + ", the " + kind + " renderer is not built");
                missing++;
                continue;
            }
            var go = new GameObject(emName + "_" + kind + (b.ContainsKey("srcIndex") ? "_" + (int)N(b["srcIndex"], 0) : ""));
            go.transform.SetParent(parent.transform, false);
            var player = go.AddComponent<PorterBakedParticleSystem>();
            player.data = data;
            player.kind = kind;
            player.loop = loops;
            player.playOnAwake = true;
            player.fieldNames = Strings(b["fieldNames"]);
            player.fieldOffsets = Ints(b["fieldOffsets"]);
            player.fieldWidths = Ints(b["fieldWidths"]);
            var comps = b.ContainsKey("dpComps") ? Ints(b["dpComps"]) : new int[0];
            if (comps.Length > 8)
            {
                var kept = new int[8];
                Array.Copy(comps, kept, 8);
                log.Add("NOT APPLIED " + emName + ": the " + kind + " renderer reads " + comps.Length + " Dynamic Parameter values, the replay carries 8 (two vertex streams); components " + string.Join(", ", ToStrings(comps, 8)) + " keep the shader's default");
                comps = kept;
            }
            player.dpComps = comps;
            // what the renderer itself sets (its facing, pivot, flipbook, ribbon shape), as the bake wrote it
            player.facingMode = (int)N(b.ContainsKey("facingMode") ? b["facingMode"] : null, 0);
            player.lengthwise = B(b.ContainsKey("lengthwise") ? b["lengthwise"] : null, false);
            player.pivotX = (float)N(b.ContainsKey("pivotX") ? b["pivotX"] : null, 0.5);
            player.pivotY = (float)N(b.ContainsKey("pivotY") ? b["pivotY"] : null, 0.5);
            player.subX = Mathf.Max(1, (int)N(b.ContainsKey("subX") ? b["subX"] : null, 1));
            player.subY = Mathf.Max(1, (int)N(b.ContainsKey("subY") ? b["subY"] : null, 1));
            player.ribbonShape = (int)N(b.ContainsKey("shape") ? b["shape"] : null, 0);
            player.ribbonSides = Mathf.Max(1, (int)N(b.ContainsKey("sides") ? b["sides"] : null, 2));
            player.sortMode = (int)N(b.ContainsKey("sortMode") ? b["sortMode"] : null, 0);
            player.inverseSquared = B(b.ContainsKey("inverseSquared") ? b["inverseSquared"] : null, false);
            player.cullMin = (float)N(b.ContainsKey("cullMin") ? b["cullMin"] : null, 0.0);
            player.cullMax = (float)N(b.ContainsKey("cullMax") ? b["cullMax"] : null, 0.0);
            if (B(b.ContainsKey("castShadows") ? b["castShadows"] : null, false)) player.shadowCasting = 1;
            if (kind == "Light")
            {
                player.lightScale = ReplayLightScale;
                player.radiusScale = (float)N(b.ContainsKey("radiusScale") ? b["radiusScale"] : null, 1.0);
                log.Add("UNVERIFIED " + emName + ": baked light brightness uses " + ReplayLightScale.ToString("0.##") + " Unity intensity per unit of Unreal colour brightness, range = LightRadius x RadiusScale; match it by eye, PorterBakedParticleSystem.lightScale on the prefab is the one number to change");
            }
            else if (kind == "Mesh")
            {
                var names = b.ContainsKey("meshes") ? Strings(b["meshes"]) : new string[0];
                player.meshes = new Mesh[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var m = FindMesh(names[i]);
                    if (m != null)
                    {
                        var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(m)) as ModelImporter;
                        if (imp != null && !imp.isReadable)
                        {
                            imp.isReadable = true;
                            imp.SaveAndReimport();
                            m = FindMesh(names[i]);
                        }
                    }
                    if (m == null) Miss("mesh", names[i], meshWhy);
                    player.meshes[i] = m;
                }
                if (b.ContainsKey("meshEntries") && b["meshEntries"] != null)
                {
                    var entries = L(b["meshEntries"]);
                    player.meshRotations = new Quaternion[entries.Count];
                    player.meshScales = new Vector3[entries.Count];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var en = D(entries[i]);
                        var rq = L(en["rotation"]);
                        var sq = L(en["scale"]);
                        player.meshRotations[i] = new Quaternion((float)N(rq[0], 0), (float)N(rq[1], 0), (float)N(rq[2], 0), (float)N(rq[3], 1));
                        player.meshScales[i] = new Vector3((float)N(sq[0], 1), (float)N(sq[1], 1), (float)N(sq[2], 1));
                        var pv = L(en["pivot"]);
                        if (pv != null && pv.Count >= 3 && (Math.Abs(N(pv[0], 0)) + Math.Abs(N(pv[1], 0)) + Math.Abs(N(pv[2], 0))) > 1e-6)
                            log.Add("NOT APPLIED " + emName + ": mesh " + i + " has a pivot offset, the baked mesh turns about its own origin");
                    }
                }
                var matNames = b.ContainsKey("materials") ? Strings(b["materials"]) : new string[0];
                var mats = new List<Material>();
                foreach (string mn in matNames)
                {
                    var mat = FindMaterial(mn);
                    if (mat == null)
                    {
                        mat = FallbackStandIn(mn);
                        log.Add("MISSING " + emName + ": material " + mn + " for the baked Mesh renderer was not found, it draws with " + (mat != null ? "Porter/ParticleFallback" : "the default material"));
                        missing++;
                        if (mat == null) { mats.Add(null); continue; }
                    }
                    var inst = StreamCopy(mat, emName + "_" + kind);
                    for (int i = 0; i < 4; i++)
                    {
                        var v = new Vector4(-1f, -1f, -1f, -1f);
                        for (int k = 0; k < comps.Length; k++)
                            if (comps[k] / 4 == i) v[comps[k] % 4] = k;
                        inst.SetVector("_PorterDPSlot" + i, v);
                    }
                    if (comps.Length < 8) { player.ageSlot = comps.Length; inst.SetFloat("_PorterAgeSlot", comps.Length); }
                    if (comps.Length < 7) { player.randSlot = comps.Length + 1; inst.SetFloat("_PorterRandSlot", comps.Length + 1); }
                    EditorUtility.SetDirty(inst);
                    mats.Add(inst);
                }
                player.materials = mats.ToArray();
                if (mats.Count > 0) player.material = mats[0];
                log.Add("UNVERIFIED " + emName + ": mesh renderer played from the replay (rotation, scale, colour and Dynamic Parameters per particle); not compared against Unreal in Unity");
            }
            else
            {
                string matWanted = S(b.ContainsKey("material") ? b["material"] : null);
                var mat = FindMaterial(matWanted);
                if (mat == null)
                {
                    mat = FallbackStandIn(matWanted);
                    log.Add("MISSING " + emName + ": material " + matWanted + " for the baked " + kind + " renderer was not found, it draws with " + (mat != null ? "Porter/ParticleFallback" : "the default material"));
                    missing++;
                }
                if (mat != null)
                {
                    var inst = StreamCopy(mat, emName + "_" + kind);
                    // slot k of the replay mesh carries Dynamic Parameter component comps[k]; the shader reads _PorterDPSlot<i>.<c> as the slot of DP i component c
                    for (int i = 0; i < 4; i++)
                    {
                        var v = new Vector4(-1f, -1f, -1f, -1f);
                        for (int k = 0; k < comps.Length; k++)
                            if (comps[k] / 4 == i) v[comps[k] % 4] = k;
                        inst.SetVector("_PorterDPSlot" + i, v);
                    }
                    // the particle's age and random number ride in the next free slots, for materials that read them
                    if (comps.Length < 8) { player.ageSlot = comps.Length; inst.SetFloat("_PorterAgeSlot", comps.Length); }
                    if (comps.Length < 7) { player.randSlot = comps.Length + 1; inst.SetFloat("_PorterRandSlot", comps.Length + 1); }
                    else log.Add("NOT APPLIED " + emName + ": no free slot for the particle random number, a material that reads it gets 0");
                    EditorUtility.SetDirty(inst);
                    player.material = inst;
                }
                if (kind == "Ribbon") log.Add("UNVERIFIED " + emName + ": ribbon strung in link order (smoothed through its points), width across " + (player.facingMode == 2 ? "the particles' side vector" : player.facingMode == 1 ? "the particles' facing" : "the view") + ", " + (player.ribbonShape == 2 ? "a tube" : player.ribbonShape == 1 ? "several planes" : "a plane") + ", U along it by the renderer's UV settings; not compared against Unreal in Unity");
                if (kind == "Decal") log.Add("APPROX " + emName + ": each decal is projected onto the scene by Porter/DecalProject (texture and tint of the decal material, depth texture needed); the decal material's own graph is not applied. Without the shader it is a flat quad");
            }
            EditorUtility.SetDirty(go);
            log.Add("REPLAY " + emName + ": " + kind + " played from " + file + " (" + (int)N(b["frames"], 0) + " ticks, up to " + (int)N(b["maxPerFrame"], 0) + " particles at once)");
        }
    }

    static string[] Strings(object o)
    {
        var l = L(o);
        var r = new string[l == null ? 0 : l.Count];
        for (int i = 0; i < r.Length; i++) r[i] = S(l[i]);
        return r;
    }

    static int[] Ints(object o)
    {
        var l = L(o);
        var r = new int[l == null ? 0 : l.Count];
        for (int i = 0; i < r.Length; i++) r[i] = (int)N(l[i], 0);
        return r;
    }

    static string[] ToStrings(int[] a, int from)
    {
        var r = new List<string>();
        for (int i = from; i < a.Length; i++) r.Add(a[i].ToString());
        return r.ToArray();
    }
}
#endif
