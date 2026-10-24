using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class PorterBlueprintSetup
{
    [Serializable]
    class MatSlot { public string slot, material; }

    [Serializable]
    class Comp
    {
        public string name, type, parent, mesh;
        public bool sky;
        public float[] loc, rot, scale, color;
        public float intensity;
        public List<MatSlot> materials;
    }

    [Serializable]
    class Manifest { public string blueprint, source; public List<Comp> components; }

    [Serializable]
    class TexEntry { public string name, prop; public int srgb; public bool normal; }

    [Serializable]
    class TexManifest { public List<TexEntry> textureList; }

    static int missing;
    static bool loud;
    static List<string> missingNames = new List<string>();

    public static GameObject BuildPrefab(string jsonPath, bool force, bool allowPartial)
    {
        missing = 0;
        loud = allowPartial;
        missingNames.Clear();
        TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath);
        if (asset == null) return null;
        Manifest d = JsonUtility.FromJson<Manifest>(asset.text);
        if (d == null || d.components == null || string.IsNullOrEmpty(d.blueprint)) return null;
        string folder = Path.GetDirectoryName(jsonPath).Replace('\\', '/');
        string prefabPath = folder + "/" + d.blueprint + ".prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
        {
            if (!force) return null;
            prefabPath = AssetDatabase.GenerateUniqueAssetPath(prefabPath);
        }

        // Post-process and sky-light components (and the empty groups that only held them) have no Unity equivalent, so they would only be
        // empty objects. Leave them out; the Unity camera's own post-processing and the scene's lighting do that job.
        d.components.RemoveAll(c => c.type != null && (c.type.Contains("PostProcess") || c.type.Contains("SkyLight") || c.type.StartsWith("ExponentialHeightFog") || c.type.StartsWith("SkyAtmosphere") || c.type.StartsWith("VolumetricCloud"))
            || (c.type == "SceneComponent" && c.name != null && c.name.StartsWith("PostProcess")));

        GameObject root = new GameObject(d.blueprint);
        Dictionary<string, Transform> made = new Dictionary<string, Transform>();
        Dictionary<string, Material> slotMaterials = new Dictionary<string, Material>();
        List<Material> used = new List<Material>();
        Light sun = null;
        foreach (Comp c in d.components)
        {
            GameObject go = new GameObject(c.name);
            made[c.name] = go.transform;
        }
        // which components have children (a mirrored mesh then goes on its own child so the mirror is not inherited)
        HashSet<string> hasChildren = new HashSet<string>();
        Dictionary<string, string> parentOf = new Dictionary<string, string>();
        foreach (Comp c in d.components)
        {
            string p = ResolveParent(c.parent, c.name, made);
            parentOf[c.name] = p;
            if (p != null) hasChildren.Add(p);
        }
        foreach (Comp c in d.components)
        {
            Transform t = made[c.name];
            string parentName = parentOf[c.name];
            Transform parent = parentName != null ? made[parentName] : root.transform;
            t.SetParent(parent, false);
            Place(t, c);
            if (!string.IsNullOrEmpty(c.mesh)) AddMesh(t.gameObject, c, folder, used, slotMaterials, hasChildren.Contains(c.name));
            else if (c.type != null && c.type.Contains("DirectionalLight"))
            {
                Light l = t.gameObject.AddComponent<Light>();
                l.type = LightType.Directional;
                if (c.color != null && c.color.Length >= 3) l.color = new Color(c.color[0], c.color[1], c.color[2]);
                if (sun == null) sun = l;
            }
            else if (c.type != null && c.type.Contains("PointLight"))
            {
                Light l = t.gameObject.AddComponent<Light>();
                l.type = LightType.Point;
                if (c.color != null && c.color.Length >= 3) l.color = new Color(c.color[0], c.color[1], c.color[2]);
            }
            else if (c.type != null && c.type.Contains("SpotLight"))
            {
                Light l = t.gameObject.AddComponent<Light>();
                l.type = LightType.Spot;
                if (c.color != null && c.color.Length >= 3) l.color = new Color(c.color[0], c.color[1], c.color[2]);
            }
            else if (c.type != null && c.type != "SceneComponent" && !c.type.Contains("PostProcess") && !c.type.Contains("SkyLight"))
            {
                Debug.LogWarning("Porter: " + c.name + " (" + c.type + ") has no Unity equivalent here, so it is an empty object");
            }
        }

        // The sky follows the camera and the camera's far clip is raised to reach the clouds, so the sky works without setting anything up.
        PorterSkyRig rig = root.AddComponent<PorterSkyRig>();
        rig.Recompute();

        TextAsset day = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "/" + d.blueprint + ".day.json");
        if (day != null)
        {
            TimeOfDayController controller = root.AddComponent<TimeOfDayController>();
            controller.dayData = day;
            controller.sun = sun;
            foreach (Comp rc in d.components)
            {
                if (rc.name == null || rc.name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') != "SunRoot" || rc.rot == null || rc.rot.Length < 3) continue;
                if (Mathf.Abs(rc.rot[0]) + Mathf.Abs(rc.rot[1]) + Mathf.Abs(rc.rot[2]) < 0.01f) break;
                Vector3 noon = RotatorToQuaternion(rc.rot[0], rc.rot[1], rc.rot[2]) * Vector3.up;
                controller.sunHeight = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(noon.y, -1f, 1f)) * Mathf.Rad2Deg, 10f, 90f);
                break;
            }
            PorterSkyLighting lighting = root.AddComponent<PorterSkyLighting>();
            lighting.controller = controller;
            lighting.skyRig = rig;
            TimeOfDayController.Day parsed = JsonUtility.FromJson<TimeOfDayController.Day>(day.text);
            if (parsed != null && parsed.slots != null)
            {
                foreach (TimeOfDayController.Group g in parsed.slots)
                {
                    Material m;
                    if (!slotMaterials.TryGetValue(g.name, out m) || m == null) m = Pick(g.name, used);
                    if (m == null) continue;
                    controller.slotBindings.Add(new TimeOfDayController.SlotBinding { slot = g.name, material = m });
                }
            }
        }
        else
        {
            Debug.LogWarning("Porter: " + d.blueprint + ".day.json was not found next to the manifest, so the prefab has no Time of Day controller");
        }

        if (missing > 0 && !allowPartial)
        {
            UnityEngine.Object.DestroyImmediate(root);
            string wait = "PorterBlueprintWait:" + jsonPath;
            if (!SessionState.GetBool(wait, false))
            {
                SessionState.SetBool(wait, true);
                Debug.Log("Porter: " + d.blueprint + " is waiting for " + string.Join(", ", missingNames) + " to import. If they are not coming, use Tools/Porter Build Blueprint Prefabs to build it anyway");
            }
            return null;
        }
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Debug.Log("Porter: saved " + prefabPath);
        return prefab;
    }

    // Unreal is left-handed, Z up, centimetres, with X forward. Unity is left-handed, Y up, metres. UE (x, y, z) becomes Unity (x, z, y) / 100.
    // Swapping two axes is a reflection, so a rotation cannot just have its angles reordered: it is converted as R' = M R M^-1 (M swaps Y and Z),
    // which is built from the rotator's real axes below. Scale only swaps its Y and Z.
    // The exported meshes arrive in Unity with their local X mirrored (the same thing the shader converter's x-flip handles), so a mesh's X scale
    // is negated: R' S' (flip x) v is then exactly M (R S v).
    public const bool MeshArrivesMirroredX = true;

    static Vector3 SwapYZ(Vector3 v) { return new Vector3(v.x, v.z, v.y); }

    public static Quaternion RotatorToQuaternion(float pitch, float yaw, float roll)
    {
        float p = pitch * Mathf.Deg2Rad, y = yaw * Mathf.Deg2Rad, r = roll * Mathf.Deg2Rad;
        float cp = Mathf.Cos(p), sp = Mathf.Sin(p), cy = Mathf.Cos(y), sy = Mathf.Sin(y), cr = Mathf.Cos(r), sr = Mathf.Sin(r);
        // the object's forward, right and up axes in Unreal's world (FRotationMatrix)
        Vector3 forward = new Vector3(cp * cy, cp * sy, sp);
        Vector3 up = new Vector3(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
        // Unity's local Y is Unreal's Z and Unity's local Z is Unreal's Y
        Vector3 unityUp = SwapYZ(up);
        Vector3 unityForward = SwapYZ(new Vector3(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp));
        if (unityForward.sqrMagnitude < 1e-12f || unityUp.sqrMagnitude < 1e-12f) return Quaternion.identity;
        return Quaternion.LookRotation(unityForward, unityUp);
    }

    static void Place(Transform t, Comp c)
    {
        if (c.loc != null && c.loc.Length >= 3) t.localPosition = new Vector3(c.loc[0], c.loc[2], c.loc[1]) * 0.01f;
        if (c.rot != null && c.rot.Length >= 3) t.localRotation = RotatorToQuaternion(c.rot[0], c.rot[1], c.rot[2]);
        if (c.scale != null && c.scale.Length >= 3) t.localScale = new Vector3(c.scale[0], c.scale[2], c.scale[1]);
    }

    // A parent is named as Unreal wrote it, which can differ from the component's own name only by its trailing number or "_GEN_VARIABLE"
    // (DefaultSceneRoot vs DefaultSceneRoot1), so match exactly first and then ignoring that.
    static string Key(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        if (n.EndsWith("_GEN_VARIABLE")) n = n.Substring(0, n.Length - "_GEN_VARIABLE".Length);
        return n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }

    static string ResolveParent(string parent, string self, Dictionary<string, Transform> made)
    {
        if (string.IsNullOrEmpty(parent) || parent == self) return null;
        if (made.ContainsKey(parent)) return parent;
        string key = Key(parent);
        string found = null;
        foreach (string n in made.Keys)
        {
            if (n == self || Key(n) != key) continue;
            if (found != null) return null;
            found = n;
        }
        return found;
    }

    static void AddMesh(GameObject go, Comp c, string folder, List<Material> used, Dictionary<string, Material> slotMaterials, bool hasChildren)
    {
        if (MeshArrivesMirroredX)
        {
            if (hasChildren)
            {
                GameObject holder = new GameObject("Mesh");
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
        string meshName = NameOf(c.mesh);
        string meshPath = FindPath(meshName, "Mesh", folder);
        Mesh mesh = meshPath == null ? null : AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        MeshFilter filter = go.AddComponent<MeshFilter>();
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        if (mesh != null) filter.sharedMesh = mesh;
        else
        {
            missing++;
            missingNames.Add(meshName);
            if (loud) Debug.LogWarning("Porter: mesh " + meshName + " was not found in the project (it should be the .fbx in the Meshes folder), so " + c.name + " draws nothing until you set its mesh");
        }
        if (c.materials == null) return;
        Material[] slots = new Material[c.materials.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            Material m = EnsureMaterial(c.materials[i].material, folder);
            if (m == null && !string.IsNullOrEmpty(c.materials[i].material))
            {
                missing++;
                missingNames.Add(NameOf(c.materials[i].material));
            }
            if (loud && m == null && !string.IsNullOrEmpty(c.materials[i].material))
                Debug.LogWarning("Porter: no shader named " + NameOf(c.materials[i].material) + " was found in the project, so " + c.name + " slot " + i + " uses the default material");
            slots[i] = m;
            if (m != null && !used.Contains(m)) used.Add(m);
            if (m != null && !string.IsNullOrEmpty(c.materials[i].slot) && !slotMaterials.ContainsKey(c.materials[i].slot)) slotMaterials[c.materials[i].slot] = m;
        }
        renderer.sharedMaterials = slots;
    }

    static Material Pick(string slot, List<Material> materials)
    {
        string s = slot.ToLowerInvariant();
        string[] words = s.Contains("moon") || s.Contains("sun") ? new[] { "moon", "sun" } : new[] { "sky", "dome" };
        foreach (Material m in materials)
            if (m.name.ToLowerInvariant().Contains(s)) return m;
        foreach (string w in words)
            foreach (Material m in materials)
                if (m.name.ToLowerInvariant().Contains(w)) return m;
        return null;
    }

    static string NameOf(string objectPath)
    {
        if (string.IsNullOrEmpty(objectPath)) return "";
        string name = objectPath.Substring(objectPath.LastIndexOf('/') + 1);
        int dot = name.IndexOf('.');
        return dot >= 0 ? name.Substring(0, dot) : name;
    }

    static string FindPath(string name, string type, string folder)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string found = null;
        foreach (string guid in AssetDatabase.FindAssets(name + " t:" + type))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.StartsWith(folder + "/")) return p;
            if (found == null) found = p;
        }
        return found;
    }

    public static Material MaterialByName(string name, string folder)
    {
        return EnsureMaterial(name, folder);
    }

    static Material EnsureMaterial(string objectPath, string folder)
    {
        string name = NameOf(objectPath);
        if (name.Length == 0) return null;
        string materialPath = FindPath(name, "Material", folder);
        if (materialPath != null) return AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        string shaderPath = FindPath(name, "Shader", folder);
        if (shaderPath == null) return null;
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        if (shader == null) return null;
        string dir = Path.GetDirectoryName(shaderPath).Replace('\\', '/');
        Material material = new Material(shader);
        AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".mat"));
        ApplyTextures(material, name, dir);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void ApplyTextures(Material material, string name, string dir)
    {
        TextAsset list = null;
        foreach (string guid in AssetDatabase.FindAssets(name + " t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith("/" + name + ".textures.json")) continue;
            list = AssetDatabase.LoadAssetAtPath<TextAsset>(p);
            if (p.StartsWith(dir + "/")) break;
        }
        if (list == null)
        {
            Debug.LogWarning("Porter: " + name + ".textures.json was not found, so " + name + " has no textures assigned");
            return;
        }
        TexManifest manifest = JsonUtility.FromJson<TexManifest>(list.text);
        if (manifest == null || manifest.textureList == null) return;
        foreach (TexEntry e in manifest.textureList)
        {
            string texPath = FindPath(e.name, "Texture2D", dir);
            if (texPath == null)
            {
                Debug.LogWarning("Porter: texture not found in project: " + e.name);
                continue;
            }
            TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;
                TextureImporterType want = e.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (importer.textureType != want) { importer.textureType = want; changed = true; }
                if (!e.normal && e.srgb >= 0 && importer.sRGBTexture != (e.srgb == 1)) { importer.sRGBTexture = e.srgb == 1; changed = true; }
                if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; changed = true; }
                if (changed) importer.SaveAndReimport();
            }
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (texture != null && material.HasProperty(e.prop)) material.SetTexture(e.prop, texture);
        }
    }
}

public class PorterBlueprintAuto : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string p in imported)
        {
            if (p.EndsWith(".blueprint.json") || p.EndsWith(".propspec.json") || p.EndsWith(".fbx") || p.EndsWith(".shader") || p.EndsWith(".wav"))
            {
                Schedule();
                return;
            }
        }
    }

    [InitializeOnLoadMethod]
    static void Schedule()
    {
        EditorApplication.delayCall += Scan;
    }

    [MenuItem("Tools/Porter Build Blueprint Prefabs")]
    static void BuildAll()
    {
        int made = 0;
        foreach (string guid in AssetDatabase.FindAssets("blueprint t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".blueprint.json")) continue;
            if (PorterBlueprintSetup.BuildPrefab(p, false, true) != null) made++;
        }
        foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".propspec.json") || p.Contains("/Particle Conversions/")) continue;
            if (PorterPropBuilder.Build(p, false, true) != null) made++;
        }
        Debug.Log("Porter: built " + made + " new blueprint prefab(s)");
    }

    static void Scan()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Scan;
            return;
        }
        foreach (string guid in AssetDatabase.FindAssets("blueprint t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".blueprint.json")) continue;
            try
            {
                PorterBlueprintSetup.BuildPrefab(p, false, false);
            }
            catch (Exception ex)
            {
                Debug.LogError("Porter: could not build " + p + ": " + ex.Message);
            }
        }
        foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".propspec.json") || p.Contains("/Particle Conversions/")) continue;
            try
            {
                PorterPropBuilder.Build(p, false, false);
            }
            catch (Exception ex)
            {
                Debug.LogError("Porter: could not build " + p + ": " + ex.Message);
            }
        }
    }
}
