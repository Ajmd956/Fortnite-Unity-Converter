#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class PorterParticleSupport
{
    [Serializable]
    class TexEntry { public string name, prop; public int srgb; public bool normal; }

    [Serializable]
    class TexManifest { public List<TexEntry> textureList; }

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
            if (Path.GetFileNameWithoutExtension(p) != name) continue;
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

public class PorterParticleAuto : AssetPostprocessor
{
    const string Marker = "/Particle Conversions/";

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string p in imported)
        {
            if (p.Contains(Marker) && (p.EndsWith(".propspec.json") || p.EndsWith(".fbx") || p.EndsWith(".shader")))
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

    [MenuItem("Tools/Porter Build Particle Prefabs")]
    static void BuildAll()
    {
        int made = 0;
        foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".propspec.json") || !p.Contains(Marker)) continue;
            if (PorterParticleBuilder.Build(p, false, true) != null) made++;
        }
        Debug.Log("Porter: built " + made + " new particle prefab(s)");
    }

    static void Scan()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Scan;
            return;
        }
        foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".propspec.json") || !p.Contains(Marker)) continue;
            try
            {
                PorterParticleBuilder.Build(p, false, false);
            }
            catch (Exception ex)
            {
                Debug.LogError("Porter: could not build " + p + ": " + ex.Message);
            }
        }
    }
}
#endif
