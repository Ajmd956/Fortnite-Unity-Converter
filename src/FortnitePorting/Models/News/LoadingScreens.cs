using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using FortnitePorting.Models.API.Responses;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FortnitePorting.Models.News;

// Real Fortnite loading screens from the public fortnite-api.com cosmetics list.
// Every 10 seconds each news picture gets a new random one, never two the same on screen.
public static class LoadingScreens
{
    private const string Api = "https://fortnite-api.com/v2/cosmetics/br/search/all?type=loadingscreen";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Random Rng = new();
    private static readonly Queue<string> Recent = new();

    private static List<string> _pool = [];
    private static DispatcherTimer? _timer;
    private static NewsEntry[] _entries = [];
    private static int _warmTicks;

    private static string CacheFile => Path.Combine(App.ApplicationDataFolder.FullName, ".cache", "loadingscreens-api.json");

    public static void Start(IEnumerable<NewsEntry> entries)
    {
        if (_timer is not null) return;
        _entries = entries.ToArray();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = LoadPoolAsync();
    }

    private static void Tick()
    {
        if (_pool.Count > 0)
        {
            foreach (var entry in _entries)
                entry.Image = NextPicture(entry.Image);
        }

        // Fast ticks until every picture is set (or about 4 minutes), then every 10 seconds.
        _warmTicks++;
        var filled = _pool.Count > 0 && _entries.All(entry => !string.IsNullOrEmpty(entry.Image));
        if (_timer!.Interval != Interval && (filled || _warmTicks > 90))
            _timer.Interval = Interval;
    }

    private static string? NextPicture(string? current)
    {
        var onScreen = _entries.Select(entry => entry.Image).ToHashSet();
        var choices = _pool.Where(url => !onScreen.Contains(url) && !Recent.Contains(url)).ToList();
        if (choices.Count == 0) choices = _pool.Where(url => !onScreen.Contains(url)).ToList();
        if (choices.Count == 0) return current;

        var pick = choices[Rng.Next(choices.Count)];
        Recent.Enqueue(pick);
        while (Recent.Count > 24) Recent.Dequeue();
        return pick;
    }

    private static async Task LoadPoolAsync()
    {
        for (var attempt = 0; attempt < 7 && _pool.Count == 0; attempt++)
        {
            try
            {
                var urls = await FetchUrlsAsync();
                if (urls.Count > 0)
                {
                    _pool = urls;
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warning("Could not get the loading screen list: {Message}", e.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(20));
        }
    }

    private static async Task<List<string>> FetchUrlsAsync()
    {
        var file = new FileInfo(CacheFile);
        if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromHours(24))
        {
            var saved = Parse(await File.ReadAllTextAsync(file.FullName));
            if (saved.Count > 0) return saved;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Api);
            request.Headers.UserAgent.ParseAdd("FortniteUnityConverter");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var fresh = Parse(json);
            if (fresh.Count == 0) throw new Exception("the answer had no pictures");

            file.Directory!.Create();
            await File.WriteAllTextAsync(file.FullName, json);
            return fresh;
        }
        catch when (file.Exists)
        {
            return Parse(await File.ReadAllTextAsync(file.FullName));
        }
    }

    private static List<string> Parse(string json)
    {
        var urls = new List<string>();
        var data = JObject.Parse(json)["data"] as JArray;
        if (data is null) return urls;

        foreach (var item in data)
        {
            var url = item["images"]?["other"]?["background"]?.ToString();
            if (!string.IsNullOrEmpty(url) && url.StartsWith("https://", StringComparison.Ordinal))
                urls.Add(url);
        }

        return urls;
    }
}
