using System;
using System.Diagnostics;
using System.IO;
using CUE4Parse.FileProvider;

namespace FortnitePorting.MaterialPorter;

public class GameContext
{
    public static string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaterialPorter");

    public IFileProvider Provider { get; set; } = null!;
    public string BuildVersion { get; set; } = "unknown";
    public bool Mounted => Provider != null;
}

public static class Timing
{
    public static void Log(string what, Stopwatch sw)
    {
    }
}
