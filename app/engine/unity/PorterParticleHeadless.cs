#if UNITY_EDITOR
using System;
using System.IO;
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
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
        }
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "PorterBlueprintBuild.txt"), report.ToString(), new UTF8Encoding(false));
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
