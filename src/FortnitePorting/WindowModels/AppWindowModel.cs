using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using FortnitePorting.Framework;
using FortnitePorting.Models.Information;
using FortnitePorting.Services;
using FortnitePorting.Shared.Extensions;
using FortnitePorting.Views;
using Serilog;

namespace FortnitePorting.WindowModels;

public partial class AppWindowModel(
    InfoService info,
    SettingsService settings,
    CUE4ParseService ueParse,
    AppService app) : WindowModelBase
{
    [ObservableProperty] private InfoService _info = info;
    [ObservableProperty] private SettingsService _settings = settings;
    [ObservableProperty] private CUE4ParseService _UEParse = ueParse;

    private readonly AppService _app = app;

    [ObservableProperty] private string _versionString = Globals.Version.Identifier switch
    {
        "dev" => "dev-build",
        var hash when CommitShaMatch().IsMatch(hash) => hash,
        _ => Globals.VersionString
    };
    [ObservableProperty] private int _unreadNewsCount;
    [ObservableProperty] private int _chatNotifications;
    [ObservableProperty] private int _unsubmittedPolls;
    [ObservableProperty] private SetupView? _setupViewContent;
    [ObservableProperty] private AppUpdate? _updateVersion;

    [ObservableProperty] private bool _splashVisible = true;
    [ObservableProperty] private string _splashStatus = "Loading dependencies...";

    private const int SplashMinMs = 4000;

    private Task SetSplash(string status) => TaskService.RunDispatcherAsync(() => SplashStatus = status);

    private async Task RunSplash()
    {
        var started = Environment.TickCount64;
        await SetSplash("Loading dependencies...");
        await Task.Delay(900);
        await SetSplash("Getting everything ready...");
        await Task.Delay(900);
        await SetSplash("Starting the API...");
        await Task.Delay(900);
        await SetSplash("All set. Let's go!");
        var remaining = SplashMinMs - (int) (Environment.TickCount64 - started);
        await Task.Delay(Math.Max(400, remaining));
        await TaskService.RunDispatcherAsync(() => SplashVisible = false);
    }

    public override async Task Initialize()
    {
        await RunSplash();

        if (!_settings.Installation.FinishedSetup)
        {
            await TaskService.RunDispatcherAsync(() =>
            {
                SetupViewContent = new SetupView();
            });
        }

        await CheckForUpdate();
    }

    [RelayCommand]
    public void SignIn()
    {
        _info.Message("Sign In", "Sign In is not available");
    }

    [RelayCommand]
    public async Task Update()
    {
        if (UpdateVersion is null) return;

        _info.Message("Update", $"Version {UpdateVersion.Version} is downloading. The app restarts when it is ready.");
        if (await AppUpdater.DownloadAndApplyAsync(UpdateVersion))
            _app.Shutdown();
        else
            _info.Message("Update", "The update could not be downloaded. Try again later.", InfoBarSeverity.Warning);
    }

    public async Task CheckForUpdate()
    {
        if (Globals.IsDevBuild) return;

        try
        {
            UpdateVersion = await AppUpdater.CheckAsync();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Update check failed");
            return;
        }

        if (UpdateVersion is null) return;

        _info.Dialog($"Update {UpdateVersion.Version}", "A new version is available.", buttons: [
            new DialogButton
            {
                Text = "Update",
                IsPrimary = true,
                Action = () => TaskService.Run(Update)
            },
            new DialogButton
            {
                Text = "Later"
            }
        ]);
    }

    [GeneratedRegex(@"^[0-9a-f]{7}$")]
    private static partial Regex CommitShaMatch();
}