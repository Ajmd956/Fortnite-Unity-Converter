using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FortnitePorting.Models;
using FortnitePorting.ViewModels;
using Serilog;

namespace FortnitePorting.Services;

public record AppUpdate(string Version, string Url, long Size, string Digest, bool Authorized);

public static class AppUpdater
{
    private const string Repo = "Ajmd956/Fortnite-Unity-Converter";
    private const string Api = "https://api.github.com";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteUnityConverter-Updater");
        return client;
    }

    private static string ReadToken()
    {
        try
        {
            var file = Path.Combine(ConvertViewModel.RootFolder, "settings.json");
            if (!File.Exists(file)) return string.Empty;

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("updateToken", out var token)
                ? token.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string accept, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd(accept);
        if (token.Length > 0)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static async Task<AppUpdate?> CheckAsync()
    {
        var token = ReadToken();
        using var request = CreateRequest($"{Api}/repos/{Repo}/releases/latest", "application/vnd.github+json", token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!FPVersion.TryParse(tag.TrimStart('v', 'V'), null, out var remote) || remote <= Globals.Version) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("installer", StringComparison.OrdinalIgnoreCase)) continue;

            var url = token.Length > 0
                ? asset.GetProperty("url").GetString()
                : asset.GetProperty("browser_download_url").GetString();
            if (url is null) continue;

            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() ?? string.Empty : string.Empty;
            digest = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : string.Empty;

            var size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            return new AppUpdate(tag.TrimStart('v', 'V'), url, size, digest, token.Length > 0);
        }

        return null;
    }

    public static async Task<bool> DownloadAndApplyAsync(AppUpdate update)
    {
        var host = new Uri(update.Url).Host.ToLowerInvariant();
        var trusted = host == "api.github.com" || host == "github.com" || host.EndsWith(".github.com") || host.EndsWith(".githubusercontent.com");
        if (!trusted)
        {
            Log.Warning("Refusing update from {Host}", host);
            return false;
        }

        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current) || !current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        var target = Path.Combine(Path.GetDirectoryName(current)!, Path.GetFileNameWithoutExtension(current) + ".update.exe");
        var partial = target + ".part";

        try
        {
            using var request = CreateRequest(update.Url, "application/octet-stream", update.Authorized ? ReadToken() : string.Empty);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;

            using var hash = SHA256.Create();
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                }

                hash.TransformFinalBlock([], 0, 0);
            }

            if (update.Size > 0 && received != update.Size) throw new InvalidDataException("Incomplete download");
            if (update.Digest.Length > 0 && !Convert.ToHexString(hash.Hash!).Equals(update.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum mismatch");

            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
        }
        catch (Exception e)
        {
            Log.Error(e, "Update download failed");
            try { File.Delete(partial); } catch { }
            return false;
        }

        var script = Path.Combine(Path.GetTempPath(), $"fuc-update-{Environment.ProcessId}.cmd");
        var text = string.Join("\r\n",
            "@echo off",
            "setlocal",
            "set /a n=0",
            ":retry",
            $"move /y \"{target}\" \"{current}\" >nul 2>&1",
            "if not errorlevel 1 goto done",
            "set /a n+=1",
            "if %n% geq 90 goto fail",
            "ping -n 2 127.0.0.1 >nul",
            "goto retry",
            ":done",
            $"start \"\" \"{current}\"",
            "goto end",
            ":fail",
            $"del /f /q \"{target}\" >nul 2>&1",
            ":end",
            "(goto) 2>nul & del /f /q \"%~f0\"",
            "");
        await File.WriteAllTextAsync(script, text, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return true;
    }
}
