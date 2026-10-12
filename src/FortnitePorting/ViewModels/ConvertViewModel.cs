using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FortnitePorting.Framework;
using FortnitePorting.Models.Information;
using FortnitePorting.Services;
using Serilog;

namespace FortnitePorting.ViewModels;

public record ConvertedShader(string Name, string ShaderPath, string Directory);

public partial class ConvertViewModel : ViewModelBase
{
    public ObservableCollection<string> Queue { get; } = [];
    public ObservableCollection<ConvertedShader> Converted { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConvertCommand))] private bool _isRunning;
    [ObservableProperty] private string _status = "Add shaders from the Files tab, then press Convert.";
    [ObservableProperty] private string _output = string.Empty;
    [ObservableProperty] private string _shaderName = string.Empty;
    [ObservableProperty] private string _materialPaths = string.Empty;

    partial void OnMaterialPathsChanged(string value) => ConvertCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task PasteMaterials()
    {
        var text = await App.Clipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;
        MaterialPaths = string.IsNullOrWhiteSpace(MaterialPaths) ? text.Trim() : MaterialPaths.TrimEnd() + "\n" + text.Trim();
    }

    [RelayCommand]
    private void ClearMaterials() => MaterialPaths = string.Empty;

    [RelayCommand]
    private void OpenCacheFolder()
    {
        Directory.CreateDirectory(CacheFolder);
        App.Launch(CacheFolder);
    }


    private Process? _current;
    private bool _stopped;

    public static string RootFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fortnite Unity Converter");
    public static string ConversionsFolder => Path.Combine(RootFolder, "Fortnite Conversions");
    public static string CacheFolder => Path.Combine(RootFolder, "Cache");

    /// <summary>Adds game files (as the Files tab lists them) to the queue.</summary>
    public void AddToQueue(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (!Queue.Contains(path, StringComparer.OrdinalIgnoreCase))
                Queue.Add(path);
        }

        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveFromQueue(string path)
    {
        Queue.Remove(path);
        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearQueue()
    {
        Queue.Clear();
        ConvertCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearOutput() => Output = string.Empty;

    [RelayCommand]
    private void OpenConversionsFolder()
    {
        Directory.CreateDirectory(ConversionsFolder);
        App.Launch(ConversionsFolder);
    }

    [RelayCommand]
    private void OpenShader(ConvertedShader shader)
    {
        if (File.Exists(shader.ShaderPath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{shader.ShaderPath}\"") { UseShellExecute = true });
        else
            App.Launch(shader.Directory);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
        _stopped = true;
        Status = "Stopping...";
        try { _current?.Kill(true); } catch { /* already finished */ }
    }

    private bool CanConvert() => !IsRunning && (Queue.Count > 0 || !string.IsNullOrWhiteSpace(MaterialPaths));

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task Convert()
    {
        var engine = FindEngine();
        if (engine is null)
        {
            Info.Dialog("Conversion engine missing",
                "FortniteUnityPorter was not found next to the app. Reinstall the app, or check that antivirus did not remove the Engine folder.");
            return;
        }

        if (!ConversionBridge.IsRunning)
        {
            Info.Dialog("Can't reach the API",
                "The game files are still loading or failed to load. Wait for the loading bar in the sidebar to finish, then try again.");
            return;
        }

        AddToQueue(MaterialPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        MaterialPaths = string.Empty;
        var materials = Queue.ToList();
        IsRunning = true;
        _stopped = false;
        Converted.Clear();
        Directory.CreateDirectory(ConversionsFolder);
        Directory.CreateDirectory(CacheFolder);

        var made = new List<ConvertedShader>();
        try
        {
            for (var i = 0; i < materials.Count && !_stopped; i++)
            {
                Status = $"Converting {i + 1} of {materials.Count}";
                AppendOutput($"\n[{i + 1}/{materials.Count}] {materials[i]}\n");

                var args = new List<string>(engine.Value.Pre)
                {
                    "--bridge", ConversionBridge.Address,
                    "--material", materials[i],
                    "--out", ConversionsFolder,
                    "--cache", CacheFolder
                };

                if (materials.Count == 1 && !string.IsNullOrWhiteSpace(ShaderName))
                {
                    args.Add("--name");
                    args.Add(ShaderName.Trim());
                }

                var output = await RunEngineAsync(engine.Value.Command, args);
                var match = Regex.Match(output, @"wrote (.+?)\.shader");
                if (!match.Success) continue;

                var name = match.Groups[1].Value;
                var shader = Path.Combine(ConversionsFolder, name, name + ".shader");
                if (!File.Exists(shader)) continue;

                var item = new ConvertedShader(name, shader, Path.GetDirectoryName(shader)!);
                made.Add(item);
                Dispatcher.UIThread.Post(() => Converted.Add(item));
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Conversion failed");
            AppendOutput($"\nConversion failed: {e.Message}\n");
        }
        finally
        {
            IsRunning = false;
            Status = $"Finished: {made.Count} of {materials.Count} converted";
            AppendOutput($"\nfinished: {made.Count} of {materials.Count} converted\n");
        }

        if (made.Count > 0)
        {
            Queue.Clear();
            Info.Dialog(made.Count == 1 ? "Shader converted" : $"{made.Count} shaders converted",
                made.Count < materials.Count
                    ? $"{made.Count} of {materials.Count} converted. The rest are explained in the output panel."
                    : "Open shader takes you to the shader file, with its Textures folder right beside it.",
                buttons:
                [
                    new DialogButton { Text = "Open shader", IsPrimary = true, Action = () => OpenShader(made[0]) },
                    new DialogButton { Text = "Close" }
                ]);
        }
        else if (!_stopped)
        {
            Info.Dialog("Nothing was converted", "None of the selected files could be converted. The output panel explains why.");
        }

        ConvertCommand.NotifyCanExecuteChanged();
    }

    internal static (string Command, string[] Pre)? FindEngine()
    {
        if (Dependencies.EngineFile.Exists) return (Dependencies.EngineFile.FullName, []);

        var dir = Path.Combine(AppContext.BaseDirectory, "Engine");
        var exe = Path.Combine(dir, "FortniteUnityPorter.exe");
        if (File.Exists(exe)) return (exe, []);

        var script = Path.Combine(dir, "FortniteUnityPorter.py");
        if (File.Exists(script)) return (OperatingSystem.IsWindows() ? "python" : "python3", [script]);

        return null;
    }

    private async Task<string> RunEngineAsync(string command, List<string> args)
    {
        var info = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        var unity = UnityLocator.Find(AppSettings.Application.UnityEditorPath);
        if (unity.Length > 0) info.Environment["UNITY_EDITOR"] = unity;

        var captured = new StringBuilder();
        void OnData(string? text)
        {
            if (text is null) return;
            lock (captured) captured.AppendLine(text);
            AppendOutput(text + "\n");
        }

        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => OnData(e.Data);
            process.ErrorDataReceived += (_, e) => OnData(e.Data);
            process.Start();
            _current = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
        }
        catch (Exception e)
        {
            AppendOutput($"could not start the engine: {e.Message}\n");
        }
        finally
        {
            _current = null;
        }

        lock (captured) return captured.ToString();
    }

    private void AppendOutput(string text) => Dispatcher.UIThread.Post(() => Output += text);
}
