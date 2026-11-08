#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static partial class PorterPropBuilder
{
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
}
#endif
