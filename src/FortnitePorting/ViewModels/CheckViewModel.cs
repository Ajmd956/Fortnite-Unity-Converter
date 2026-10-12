using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FortnitePorting.Framework;
using FortnitePorting.Services;

namespace FortnitePorting.ViewModels;

// "Check Shaders": scan .shader files for common problems and fix the safe ones.
public partial class CheckViewModel : ViewModelBase
{
    public ObservableCollection<string> Paths { get; } = [];

    [ObservableProperty] private bool _fixProblems = true;
    [ObservableProperty] private bool _compileInUnity = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCommand))] private bool _isRunning;
    [ObservableProperty] private string _status = "Drop .shader files or folders here, or use Add files / Add folder.";
    [ObservableProperty] private string _output = string.Empty;

    private Process? _current;

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                Paths.Add(path);
        }

        CheckCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var files = await App.Lifetime.MainWindow!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Add shaders",
            FileTypeFilter = [new FilePickerFileType("Shader files") { Patterns = ["*.shader"] }]
        });
        AddPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var folders = await App.Lifetime.MainWindow!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Add a folder of shaders"
        });
        AddPaths(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
    }

    [RelayCommand]
    private void Remove(string path)
    {
        Paths.Remove(path);
        CheckCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Clear()
    {
        Paths.Clear();
        CheckCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearOutput() => Output = string.Empty;

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
        Status = "Stopping...";
        try { _current?.Kill(true); } catch { /* already finished */ }
    }

    private bool CanCheck() => !IsRunning && Paths.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check() => await RunCheck(Paths.ToList());

    public async Task RunCheck(IReadOnlyList<string> paths)
    {
        if (IsRunning || paths.Count == 0) return;

        var engine = ConvertViewModel.FindEngine();
        if (engine is null)
        {
            Info.Dialog("Conversion engine missing",
                "FortniteUnityPorter was not found next to the app. Reinstall the app, or check that antivirus did not remove the Engine folder.");
            return;
        }

        var args = new List<string>(engine.Value.Pre) { "--check-shader" };
        args.AddRange(paths);
        if (!FixProblems) args.Add("--no-fix");
        if (!CompileInUnity) args.Add("--no-unity");

        IsRunning = true;
        Status = "Checking";
        AppendOutput("\n");

        var info = new ProcessStartInfo(engine.Value.Command)
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

        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendOutput(e.Data + "\n"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendOutput(e.Data + "\n"); };
            process.Start();
            _current = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
        }
        catch (Exception e)
        {
            AppendOutput($"could not start: {e.Message}\n");
        }
        finally
        {
            _current = null;
            IsRunning = false;
            Status = "Check finished";
            AppendOutput("\ncheck finished\n");
            CheckCommand.NotifyCanExecuteChanged();
        }
    }

    private void AppendOutput(string text) => Dispatcher.UIThread.Post(() => Output += text);
}
