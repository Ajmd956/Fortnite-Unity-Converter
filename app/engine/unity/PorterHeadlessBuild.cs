#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PorterHeadlessBuild
{
    const string Root = "Assets/Blueprint Conversions";

    public static void Run()
    {
        var report = new StringBuilder();
        try
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string guid in AssetDatabase.FindAssets("blueprint t:TextAsset", new[] { Root }))
                Build(AssetDatabase.GUIDToAssetPath(guid), ".blueprint.json", report);
            foreach (string guid in AssetDatabase.FindAssets("propspec t:TextAsset", new[] { Root }))
                Build(AssetDatabase.GUIDToAssetPath(guid), ".propspec.json", report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
        }
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "PorterBlueprintBuild.txt"), report.ToString(), new UTF8Encoding(false));
    }

    static GameObject Make(string path, string suffix, bool force)
    {
        if (suffix == ".blueprint.json") return PorterBlueprintSetup.BuildPrefab(path, force, true);
        return PorterPropBuilder.Build(path, force, true);
    }

    static void Build(string path, string suffix, StringBuilder report)
    {
        if (!path.EndsWith(suffix)) return;
        string folder = Path.GetDirectoryName(path).Replace('\\', '/');
        string name = Path.GetFileName(path);
        name = name.Substring(0, name.Length - suffix.Length);
        string prefab = folder + "/" + name + ".prefab";
        try
        {
            bool existed = AssetDatabase.LoadAssetAtPath<GameObject>(prefab) != null;
            GameObject made = Make(path, suffix, existed);
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
