using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FortnitePorting.ViewModels;

namespace FortnitePorting.Services;

// Finds the Unity Editor the shader check compiles with: the chosen one, else the preferred version, else the newest.
public static class UnityLocator
{
    private const string Preferred = "2022.3.22f1";

    public static string Find(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen)) return chosen;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ConvertViewModel.RootFolder
        }.Where(root => !string.IsNullOrEmpty(root)).Distinct();

        var found = new List<(string Version, string Exe)>();
        foreach (var root in roots)
        {
            var single = Path.Combine(root, "Unity", "Editor", "Unity.exe");
            if (File.Exists(single)) found.Add(("0", single));

            foreach (var hub in new[] { Path.Combine(root, "Unity", "Hub", "Editor"), Path.Combine(root, "Unity Hub", "Editor") })
            {
                if (!Directory.Exists(hub)) continue;
                foreach (var dir in Directory.EnumerateDirectories(hub))
                {
                    var exe = Path.Combine(dir, "Editor", "Unity.exe");
                    if (File.Exists(exe)) found.Add((Path.GetFileName(dir), exe));
                }
            }
        }

        if (found.Count == 0) return string.Empty;
        var wanted = found.FirstOrDefault(entry => entry.Version == Preferred);
        if (wanted.Exe is not null) return wanted.Exe;
        return found.OrderByDescending(entry => Key(entry.Version), Comparer<long>.Default).First().Exe;
    }

    private static long Key(string version)
    {
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)([a-z])(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;
        long n(int i) => long.Parse(match.Groups[i].Value);
        return n(1) * 10_000_000_000 + n(2) * 100_000_000 + n(3) * 100_000 + char.ToLowerInvariant(match.Groups[4].Value[0]) * 1000 + n(5);
    }
}
