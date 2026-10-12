using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FortnitePorting.Framework;
using FortnitePorting.Views;

namespace FortnitePorting.ViewModels;

public record ShaderRow(string Name, string Directory, string ShaderPath, int Textures, bool HasCache, string CachePath, DateTime Modified)
{
    public string Meta => $"{Textures} textures · {(HasCache ? "cached" : "no cache")} · {Ago(Modified)}";

    private static string Ago(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int) span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int) span.TotalHours} h ago";
        return $"{(int) span.TotalDays} d ago";
    }
}

// "My Shaders": everything that was converted, newest first.
public partial class ShadersViewModel : ViewModelBase
{
    public ObservableCollection<ShaderRow> Shaders { get; } = [];

    [ObservableProperty] private bool _isEmpty = true;

    public override async Task OnViewOpened() => await Refresh();

    [RelayCommand]
    private async Task Refresh()
    {
        var rows = await Task.Run(Scan);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Shaders.Clear();
            foreach (var row in rows) Shaders.Add(row);
            IsEmpty = Shaders.Count == 0;
        });
    }

    private static List<ShaderRow> Scan()
    {
        var root = new DirectoryInfo(ConvertViewModel.ConversionsFolder);
        var rows = new List<ShaderRow>();
        if (!root.Exists) return rows;

        foreach (var dir in root.EnumerateDirectories())
        {
            var shader = new FileInfo(Path.Combine(dir.FullName, dir.Name + ".shader"));
            if (!shader.Exists) continue;

            var textures = Path.Combine(dir.FullName, "Textures");
            var textureCount = Directory.Exists(textures) ? Directory.GetFileSystemEntries(textures).Length : 0;
            var cache = Path.Combine(ConvertViewModel.CacheFolder, dir.Name);
            rows.Add(new ShaderRow(dir.Name, dir.FullName, shader.FullName, textureCount, Directory.Exists(cache), cache, shader.LastWriteTime));
        }

        return rows.OrderByDescending(row => row.Modified).ToList();
    }

    [RelayCommand]
    private async Task Check(ShaderRow row)
    {
        Navigation.App.Open<CheckView>();
        await CheckVM.RunCheck([row.ShaderPath]);
    }

    [RelayCommand]
    private void OpenFolder(ShaderRow row) => App.Launch(row.Directory);

    [RelayCommand]
    private void OpenCache(ShaderRow row)
    {
        var folder = row.HasCache ? row.CachePath : ConvertViewModel.CacheFolder;
        Directory.CreateDirectory(folder);
        App.Launch(folder);
    }
}
