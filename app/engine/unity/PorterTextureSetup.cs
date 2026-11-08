using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class PorterTextureSetup : EditorWindow
{
    [System.Serializable]
    class Entry
    {
        public string name;
        public string prop;
        public int srgb;
        public bool normal;
        public int width;
        public int height;
        public bool nearest;
        public bool stack;
    }

    [System.Serializable]
    class Manifest
    {
        public string shader;
        public List<Entry> textureList;
    }

    TextAsset manifestAsset;
    Material target;

    [MenuItem("Tools/Porter Texture Setup")]
    static void Open()
    {
        GetWindow<PorterTextureSetup>("Porter Texture Setup");
    }

    void OnGUI()
    {
        manifestAsset = (TextAsset)EditorGUILayout.ObjectField("Textures JSON", manifestAsset, typeof(TextAsset), false);
        target = (Material)EditorGUILayout.ObjectField("Material", target, typeof(Material), false);
        if (GUILayout.Button("Apply") && manifestAsset != null)
        {
            Apply();
        }
    }

    void Apply()
    {
        Manifest m = JsonUtility.FromJson<Manifest>(manifestAsset.text);
        foreach (Entry e in m.textureList)
        {
            string path = null;
            foreach (string guid in AssetDatabase.FindAssets(e.name + " t:Texture2D"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) == e.name)
                {
                    path = p;
                    break;
                }
            }
            if (path == null)
            {
                Debug.LogWarning("Porter: texture not found in project: " + e.name);
                continue;
            }
            TextureImporter imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = e.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (!e.normal && e.srgb >= 0)
            {
                imp.sRGBTexture = e.srgb == 1;
            }
            imp.mipmapEnabled = !e.stack;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = e.nearest ? FilterMode.Point : FilterMode.Trilinear;
            imp.anisoLevel = 8;
            imp.crunchedCompression = false;
            imp.textureCompression = (long)e.width * e.height <= 512L * 512L
                ? TextureImporterCompression.Uncompressed
                : TextureImporterCompression.CompressedHQ;
            int side = e.stack ? 16384 : Mathf.Max(e.width, e.height);
            if (side > 0)
            {
                imp.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(side), 32, 16384);
            }
            imp.SaveAndReimport();
            if (target != null)
            {
                target.SetTexture(e.prop, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            }
        }
        if (target != null)
        {
            EditorUtility.SetDirty(target);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("Porter: texture setup finished for " + m.shader);
    }
}

public class PorterTextureAuto : AssetPostprocessor
{
    [System.Serializable]
    class E
    {
        public string name;
        public int srgb;
        public bool normal;
        public int width;
        public int height;
        public bool nearest;
        public bool stack;
    }

    [System.Serializable]
    class M
    {
        public List<E> textureList;
    }

    static Dictionary<string, E> map;
    static double loadedAt = -100.0;
    static bool warnedColorSpace;

    static void Load()
    {
        map = new Dictionary<string, E>();
        loadedAt = EditorApplication.timeSinceStartup;
        foreach (string guid in AssetDatabase.FindAssets("textures t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith("textures.json"))
            {
                continue;
            }
            M m = null;
            try
            {
                m = JsonUtility.FromJson<M>(File.ReadAllText(p));
            }
            catch (System.Exception)
            {
            }
            if (m == null || m.textureList == null)
            {
                continue;
            }
            foreach (E e in m.textureList)
            {
                if (!string.IsNullOrEmpty(e.name))
                {
                    map[e.name] = e;
                }
            }
        }
    }

    static E Find(string name)
    {
        if (map == null || (!map.ContainsKey(name) && EditorApplication.timeSinceStartup - loadedAt > 3.0))
        {
            Load();
        }
        E e;
        return map.TryGetValue(name, out e) ? e : null;
    }

    static bool Apply(TextureImporter imp, E e)
    {
        bool changed = false;
        TextureImporterType type = e.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (imp.textureType != type)
        {
            imp.textureType = type;
            changed = true;
        }
        if (!e.normal && e.srgb >= 0 && imp.sRGBTexture != (e.srgb == 1))
        {
            imp.sRGBTexture = e.srgb == 1;
            changed = true;
        }
        if (imp.mipmapEnabled == e.stack)
        {
            imp.mipmapEnabled = !e.stack;
            changed = true;
        }
        if (imp.wrapMode != TextureWrapMode.Repeat)
        {
            imp.wrapMode = TextureWrapMode.Repeat;
            changed = true;
        }
        FilterMode wantFilter = e.nearest ? FilterMode.Point : FilterMode.Trilinear;
        if (imp.filterMode != wantFilter)
        {
            imp.filterMode = wantFilter;
            changed = true;
        }
        if (imp.anisoLevel != 8)
        {
            imp.anisoLevel = 8;
            changed = true;
        }
        if (imp.crunchedCompression)
        {
            imp.crunchedCompression = false;
            changed = true;
        }
        TextureImporterCompression comp = (long)e.width * e.height <= 512L * 512L
            ? TextureImporterCompression.Uncompressed
            : TextureImporterCompression.CompressedHQ;
        if (imp.textureCompression != comp)
        {
            imp.textureCompression = comp;
            changed = true;
        }
        int side = e.stack ? 16384 : Mathf.Max(e.width, e.height);
        if (side > 0)
        {
            int max = Mathf.Clamp(Mathf.NextPowerOfTwo(side), 32, 16384);
            if (imp.maxTextureSize != max)
            {
                imp.maxTextureSize = max;
                changed = true;
            }
        }
        return changed;
    }

    static void WarnColorSpace()
    {
        if (warnedColorSpace || PlayerSettings.colorSpace == ColorSpace.Linear)
        {
            return;
        }
        warnedColorSpace = true;
        Debug.LogWarning("Porter: this project uses Gamma color space. Ported Unreal shaders are written for Linear (Edit > Project Settings > Player > Other Settings > Color Space); their colors and textures will look wrong in Gamma.");
    }

    void OnPreprocessTexture()
    {
        E e = Find(Path.GetFileNameWithoutExtension(assetPath));
        if (e == null)
        {
            return;
        }
        Apply((TextureImporter)assetImporter, e);
        WarnColorSpace();
    }

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string p in imported)
        {
            if (p.EndsWith("textures.json"))
            {
                map = null;
                EditorApplication.delayCall += ApplyToProject;
                return;
            }
        }
    }

    [MenuItem("Tools/Porter Apply Texture Settings")]
    static void ApplyToProject()
    {
        Load();
        WarnColorSpace();
        int count = 0;
        foreach (KeyValuePair<string, E> kv in map)
        {
            foreach (string guid in AssetDatabase.FindAssets(kv.Key + " t:Texture2D"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) != kv.Key)
                {
                    continue;
                }
                TextureImporter imp = AssetImporter.GetAtPath(p) as TextureImporter;
                if (imp != null && Apply(imp, kv.Value))
                {
                    imp.SaveAndReimport();
                    count++;
                }
            }
        }
        if (count > 0)
        {
            Debug.Log("Porter: fixed import settings on " + count + " texture(s)");
        }
    }
}
