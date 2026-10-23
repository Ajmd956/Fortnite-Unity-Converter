#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the baked-replay players of a converted particle system (see PorterParticleReplay.cs).
public static partial class PorterPropBuilder
{
    // Unity light intensity per unit of Unreal light colour brightness. Unreal and Unity light units differ:
    // this is a starting value to match by eye against Unreal, not a measured one.
    const float ReplayLightScale = 1f;

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
            var player = go.AddComponent<PorterParticleReplay>();
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
            if (kind == "Light")
            {
                player.lightScale = ReplayLightScale;
                player.radiusScale = (float)N(b.ContainsKey("radiusScale") ? b["radiusScale"] : null, 1.0);
                log.Add("UNVERIFIED " + emName + ": baked light brightness uses " + ReplayLightScale.ToString("0.##") + " Unity intensity per unit of Unreal colour brightness, range = LightRadius x RadiusScale; match it by eye, PorterParticleReplay.lightScale on the prefab is the one number to change");
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
                var matNames = b.ContainsKey("materials") ? Strings(b["materials"]) : new string[0];
                var mats = new List<Material>();
                foreach (string mn in matNames)
                {
                    var mat = FindMaterial(mn);
                    if (mat == null)
                    {
                        log.Add("MISSING " + emName + ": material " + mn + " for the baked Mesh renderer was not found, it draws with the default material");
                        missing++;
                        continue;
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
                var mat = FindMaterial(S(b.ContainsKey("material") ? b["material"] : null));
                if (mat == null)
                {
                    log.Add("MISSING " + emName + ": material " + S(b.ContainsKey("material") ? b["material"] : null) + " for the baked " + kind + " renderer was not found, it draws with the default material");
                    missing++;
                }
                else
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
