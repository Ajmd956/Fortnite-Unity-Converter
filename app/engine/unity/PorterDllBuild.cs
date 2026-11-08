#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;

public static class PorterDllBuild
{
    static readonly StringBuilder Report = new StringBuilder();
    static bool failed;
    static bool runtimeDone;
    static bool editorDone;
    static string projectRoot;
    static string outDir;
    static AssemblyBuilder editorBuilder;

    public static void Run()
    {
        try
        {
            projectRoot = Directory.GetCurrentDirectory();
            string src = Path.Combine(projectRoot, "PorterDllSource");
            outDir = Path.Combine(projectRoot, "PorterDllOut");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            string runtimeDll = Path.Combine(outDir, "PorterRuntime.dll");
            string editorDll = Path.Combine(outDir, "PorterEditor.dll");
            string[] runtimeFiles = Directory.GetFiles(Path.Combine(src, "Runtime"), "*.cs");
            string[] editorFiles = Directory.GetFiles(Path.Combine(src, "Editor"), "*.cs");

            var runtime = new AssemblyBuilder(runtimeDll, runtimeFiles);
            runtime.buildTarget = BuildTarget.StandaloneWindows64;
            runtime.buildTargetGroup = BuildTargetGroup.Standalone;
            runtime.flags = AssemblyBuilderFlags.None;
            runtime.buildFinished += (path, messages) =>
            {
                Collect("runtime", messages);
                runtimeDone = true;
                if (failed || !File.Exists(runtimeDll)) { Finish(); return; }
                editorBuilder = new AssemblyBuilder(editorDll, editorFiles);
                editorBuilder.buildTarget = BuildTarget.StandaloneWindows64;
                editorBuilder.buildTargetGroup = BuildTargetGroup.Standalone;
                editorBuilder.flags = AssemblyBuilderFlags.EditorAssembly;
                editorBuilder.additionalReferences = new[] { runtimeDll };
                editorBuilder.buildFinished += (p, m) =>
                {
                    Collect("editor", m);
                    editorDone = true;
                    Finish();
                };
                if (!editorBuilder.Build()) { Report.AppendLine("ERROR the editor assembly could not start"); failed = true; Finish(); }
            };
            if (!runtime.Build()) { Report.AppendLine("ERROR the runtime assembly could not start"); failed = true; Finish(); }
        }
        catch (Exception ex)
        {
            Report.AppendLine("ERROR " + ex.GetType().Name + ": " + ex.Message);
            failed = true;
            Finish();
        }
    }

    static void Collect(string which, CompilerMessage[] messages)
    {
        foreach (CompilerMessage m in messages)
        {
            if (m.type == CompilerMessageType.Error)
            {
                failed = true;
                Report.AppendLine("ERROR " + which + ": " + m.message);
            }
        }
    }

    static void Finish()
    {
        bool ok = !failed && runtimeDone && editorDone
            && File.Exists(Path.Combine(outDir, "PorterRuntime.dll")) && File.Exists(Path.Combine(outDir, "PorterEditor.dll"));
        File.WriteAllText(Path.Combine(projectRoot, "PorterDllBuild.txt"), (ok ? "OK\n" : "FAILED\n") + Report, new UTF8Encoding(false));
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
#endif
