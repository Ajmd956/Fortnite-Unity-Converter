#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PorterParticleHeadless
{
    const string Root = "Assets/Particle Conversions";

    public static void Run()
    {
        var report = new StringBuilder();
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset", new[] { Root }))
                Build(AssetDatabase.GUIDToAssetPath(guid), report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            AuditShaders(report);
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
        }
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "PorterBlueprintBuild.txt"), report.ToString(), new UTF8Encoding(false));
    }

    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const int AuditLimit = 60;

    static string Member(object o, string name)
    {
        Type t = o.GetType();
        PropertyInfo p = t.GetProperty(name, Any);
        if (p != null)
        {
            object v = p.GetValue(o, null);
            return v == null ? "" : v.ToString();
        }
        FieldInfo f = t.GetField(name, Any);
        if (f != null)
        {
            object v = f.GetValue(o);
            return v == null ? "" : v.ToString();
        }
        return "";
    }

    static void AuditShaders(StringBuilder report)
    {
        MethodInfo hasError = typeof(ShaderUtil).GetMethod("ShaderHasError", Any, null, new[] { typeof(Shader) }, null);
        MethodInfo messages = typeof(ShaderUtil).GetMethod("GetShaderMessages", Any, null, new[] { typeof(Shader) }, null);
        var broken = new HashSet<string>();
        var repair = new List<KeyValuePair<Material, string>>();
        int lines = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { Root }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                broken.Add(path);
                if (lines++ < AuditLimit) report.AppendLine("SHADER ERROR " + path + ": Unity could not load this file as a shader");
                continue;
            }
            if (hasError == null || !(bool)hasError.Invoke(null, new object[] { shader })) continue;
            broken.Add(path);
            var found = new List<string>();
            Array all = messages != null ? messages.Invoke(null, new object[] { shader }) as Array : null;
            if (all != null)
            {
                foreach (object m in all)
                {
                    string severity = Member(m, "severity");
                    if (severity.Length > 0 && severity.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    found.Add(Member(m, "message") + " (line " + Member(m, "line") + ")");
                    if (found.Count >= 3) break;
                }
            }
            if (lines++ < AuditLimit) report.AppendLine("SHADER ERROR " + path + ": " + (found.Count > 0 ? string.Join("; ", found.ToArray()) : "it does not compile, see the Unity log"));
        }
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { Root }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;
            string why = null;
            if (mat.shader == null) why = "it has no shader";
            else if (mat.shader.name == "Hidden/InternalErrorShader") why = "its shader is missing or failed to compile";
            else if (broken.Contains(AssetDatabase.GetAssetPath(mat.shader))) why = "its shader " + mat.shader.name + " does not compile";
            if (why == null) continue;
            if (lines++ < AuditLimit) report.AppendLine("PINK " + path + ": " + why);
            repair.Add(new KeyValuePair<Material, string>(mat, why));
        }
        if (lines > AuditLimit) report.AppendLine("PINK " + (lines - AuditLimit) + " more shader or material problem(s) not listed");
        // every pink material is moved to the fallback shader; the lines above still say what was wrong
        int repaired = 0;
        foreach (var r in repair)
        {
            string done = null;
            try { done = PorterParticleBuilder.RepairMaterial(r.Key, r.Value); }
            catch (Exception ex) { report.AppendLine("FALLBACK " + AssetDatabase.GetAssetPath(r.Key) + ": could not repair, " + ex.Message); continue; }
            if (done == null) continue;
            repaired++;
            if (repaired <= AuditLimit) report.AppendLine(done);
        }
        if (repaired > AuditLimit) report.AppendLine("FALLBACK " + (repaired - AuditLimit) + " more material(s) repaired, not listed");
        if (repair.Count > 0 && repaired == 0) report.AppendLine("FALLBACK the fallback shader is not in the project, " + repair.Count + " pink material(s) were left as they are");
        if (repaired > 0) AssetDatabase.SaveAssets();
    }

    static void Build(string path, StringBuilder report)
    {
        const string suffix = ".propspec.json";
        if (!path.EndsWith(suffix)) return;
        string folder = Path.GetDirectoryName(path).Replace('\\', '/');
        string name = Path.GetFileName(path);
        name = name.Substring(0, name.Length - suffix.Length);
        string prefab = folder + "/" + name + ".prefab";
        try
        {
            bool existed = AssetDatabase.LoadAssetAtPath<GameObject>(prefab) != null;
            GameObject made = PorterParticleBuilder.Build(path, existed, true);
            if (existed && made != null)
            {
                string fresh = AssetDatabase.GetAssetPath(made);
                if (!string.IsNullOrEmpty(fresh) && fresh != prefab)
                {
                    File.Copy(Path.GetFullPath(fresh), Path.GetFullPath(prefab), true);
                    AssetDatabase.DeleteAsset(fresh);
                    AssetDatabase.ImportAsset(prefab, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine("FAILED " + path + ": " + ex.Message);
            return;
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefab) != null) report.AppendLine("BUILT " + prefab);
        else report.AppendLine("MISSING " + prefab);
    }
}
#endif
