using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using FortnitePorting.Framework;
using FortnitePorting.Models.API.Responses;
using FortnitePorting.Models.Information;
using FortnitePorting.Models.News;
using FortnitePorting.Services;
using FortnitePorting.Shared.Extensions;
using FortnitePorting.Windows;

namespace FortnitePorting.ViewModels;

public partial class HomeViewModel(
    CUE4ParseService ueParse,
    APIService api,
    SettingsService settings,
    InfoService info,
    AppService app) : ViewModelBase
{
    [ObservableProperty] private CUE4ParseService _UEParse = ueParse;

    private readonly APIService _api = api;
    private readonly SettingsService _settings = settings;
    private readonly InfoService _info = info;
    private readonly AppService _app = app;

    [ObservableProperty] private ObservableCollection<NewsEntry> _news = [];

    public override async Task Initialize()
    {
        News = [..LocalNews.HomeCards];
        LocalNews.StartRotation();

        TaskService.Run(async () => await _UEParse.LoadCoreSessionAsync());
    }

    public void OpenNews(NewsEntry news)
    {
        ChangelogWindow.Preview(news.Description);
    }

    public void LaunchDiscord() => _app.Launch(Globals.DISCORD_URL);
    public void LaunchTwitter() => _app.Launch(Globals.TWITTER_URL);
    public void LaunchGitHub() => _app.Launch(Globals.GITHUB_URL);
    public void LaunchKoFi() => _app.Launch(Globals.KOFI_URL);
    public void LaunchWebsite() => _app.Launch(Globals.WEBSITE_URL);
}