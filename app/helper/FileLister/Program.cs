using System.Security.Cryptography;
using FortnitePorting.MaterialPorter;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Versions;

// The app looks after its own game keys and mappings: keys come from fortnite-api.com (and are kept in the
// cache for when it is down), mappings from your own .usmap, the cache, or the Fortnite Central list.
const string KeysUrl = "https://fortnite-api.com/v2/aes";
const string MappingsListUrl = "https://fortnitecentral.genxgames.gg/api/v1/mappings";

var archive = ArgValue(args, "--archive");
var output = ArgValue(args, "--out");
var cache = ArgValue(args, "--cache");

var serve = Array.IndexOf(args, "--serve") >= 0;
var noApi = Array.IndexOf(args, "--no-api") >= 0;
var portText = ArgValue(args, "--port");
var port = int.TryParse(portText, out var parsedPort) && parsedPort > 0 ? parsedPort : 24320;

if (archive.Length == 0 || cache.Length == 0 || (!serve && output.Length == 0))
{
    Say("error: usage");
    return 2;
}

if (!Directory.Exists(archive))
{
    Say("error: archive-missing");
    return 3;
}

Directory.CreateDirectory(cache);

try
{
    Say("status: Getting keys and mappings");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteUnityConverter");

    var info = await LoadKeysAsync(http, cache);
    if (info is null)
    {
        Say("error: api-unreachable");
        return 4;
    }

    if (info.Keys?.MainKey is null || string.IsNullOrWhiteSpace(info.Keys.MainKey.Key))
    {
        Say("error: no-keys");
        return 4;
    }

    var mappingsPath = await FindMappingsAsync(http, cache);
    if (mappingsPath is null) Say("status: No mappings found, reading without them");

    Say("status: Preparing decompression");
    await OodleHelper.InitializeAsync(Path.Combine(cache, OodleHelper.OodleFileName));

    try
    {
        var detexPath = Path.Combine(cache, CUE4Parse_Conversion.Textures.BC.DetexHelper.DLL_NAME);
        if (CUE4Parse_Conversion.Textures.BC.DetexHelper.LoadDll(detexPath))
            CUE4Parse_Conversion.Textures.BC.DetexHelper.Initialize(detexPath);
    }
    catch (Exception)
    {
    }

    Say("status: Reading game archives");
    var provider = new DefaultFileProvider(archive, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE6_0, optionOverrides: new Dictionary<string, bool> { ["StaticMesh.KeepMobileMinLODSettingOnDesktop"] = true }), StringComparer.OrdinalIgnoreCase);
    provider.Initialize();
    provider.ReadNaniteData = true; // Fortnite Porting turns this on by default; Nanite meshes fail to read without it

    await provider.SubmitKeyAsync(new FGuid(), new FAesKey(info.Keys.MainKey.Key));
    foreach (var extra in info.Keys.ExtraKeys ?? [])
    {
        try
        {
            await provider.SubmitKeyAsync(new FGuid(extra.Guid), new FAesKey(extra.Key));
        }
        catch (Exception)
        {
        }
    }

    if (mappingsPath is not null)
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath, StringComparer.Ordinal);

    provider.LoadVirtualPaths();

    if (provider.Files.Count == 0)
    {
        Say("error: no-files");
        return 5;
    }

    if (serve)
    {
        Bridge? bridge = null;
        if (!noApi)
        {
            Say("status: Starting the API");
            try
            {
                GameContext.DataDir = Path.Combine(cache, "Api");
                Directory.CreateDirectory(GameContext.DataDir);
                var game = new GameContext
                {
                    Provider = provider,
                    BuildVersion = string.IsNullOrWhiteSpace(info.Version) ? "unknown" : info.Version
                };
                var materials = new MaterialService(game);
                AssetExport.Materials = materials;
                bridge = new Bridge(game, materials);
                bridge.Start(port);
                Say("api: ready " + port);
            }
            catch (Exception ex)
            {
                bridge?.Dispose();
                bridge = null;
                Say("api: unavailable " + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
            }
        }

        Say("ready");
        string? request;
        while ((request = Console.In.ReadLine()) is not null)
        {
            request = request.Trim();
            if (request.Length == 0) continue;
            if (request.StartsWith("export\t", StringComparison.Ordinal))
            {
                var parts = request.Split('\t');
                if (parts.Length < 4) continue;
                var kind = parts[1];
                var assetPath = parts[2];
                var outRoot = parts[3];
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var file = await AssetExport.RunAsync(provider, kind, assetPath, outRoot);
                        Say("export\t" + kind + "\t" + assetPath + "\tok\t" + file);
                    }
                    catch (Exception ex)
                    {
                        Say("export\t" + kind + "\t" + assetPath + "\terror\t" + ex.Message.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
                    }
                });
                continue;
            }
            Say("type\t" + request + "\t" + ResolveClass(provider, request));
        }
        bridge?.Dispose();
        return 0;
    }

    Say("status: Building the file list");
    var lines = new List<string>(provider.Files.Count / 2);
    foreach (var file in provider.Files.Values)
    {
        var filePath = file.Path;
        var validExt = filePath.EndsWith(".uasset", StringComparison.Ordinal) || filePath.EndsWith(".umap", StringComparison.Ordinal) || filePath.EndsWith(".ufont", StringComparison.Ordinal);
        if (!validExt || filePath.Contains("/_Verse/", StringComparison.Ordinal)) continue;
        lines.Add(filePath + "\t" + ContainerName(file));
    }

    lines.Sort(StringComparer.OrdinalIgnoreCase);
    var temp = output + ".tmp";
    await File.WriteAllLinesAsync(temp, lines);
    File.Move(temp, output, true);

    Say("done: " + lines.Count);
    return 0;
}
catch (Exception ex)
{
    Say("error: " + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
    return 1;
}

static string ArgValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
}

static void Say(string line)
{
    Console.WriteLine(line);
    Console.Out.Flush();
}

static bool HashMatches(string path, string? expected)
{
    if (string.IsNullOrWhiteSpace(expected)) return true;
    using var stream = File.OpenRead(path);
    var actual = Convert.ToHexString(MD5.HashData(stream));
    return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
}

static string ContainerName(CUE4Parse.FileProvider.Objects.GameFile file)
{
    var name = (file as VfsEntry)?.Vfs?.Name ?? string.Empty;
    return name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

static string ResolveClass(DefaultFileProvider provider, string path)
{
    try
    {
        var clean = path.TrimStart('/');
        if (!provider.TryLoadPackage(clean, out var package)) return "?";
        var fileName = clean.Substring(clean.LastIndexOf('/') + 1);
        var dot = fileName.IndexOf('.');
        if (dot >= 0) fileName = fileName.Substring(0, dot);
        var export = FindPrimaryExport(package, fileName);
        var className = export?.Class?.Name.Text;
        return string.IsNullOrWhiteSpace(className) ? "?" : className;
    }
    catch (Exception)
    {
        return "?";
    }
}

static ResolvedObject? FindPrimaryExport(IPackage package, string fileName)
{
    ResolvedObject? namedExport = null;
    ResolvedObject? packageRootExport = null;

    for (var i = 0; i < package.ExportMapLength; i++)
    {
        var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
        if (pointer?.Object is null) continue;

        var outer = pointer.Outer;
        var isPackageRoot = outer is null
                            || outer.ExportIndex < 0
                            || !ReferenceEquals(outer.Package, package);
        if (isPackageRoot)
            packageRootExport ??= pointer;

        var nameMatches = pointer.Name.Text.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                          || pointer.Name.Text.Equals(fileName + "_C", StringComparison.OrdinalIgnoreCase);
        if (!nameMatches) continue;

        if (isPackageRoot)
            return pointer;

        namedExport ??= pointer;
    }

    return namedExport ?? packageRootExport;
}

static string? ToObjectPath(string packagePath)
{
    var parts = packagePath.Split('/');
    if (parts.Length < 3) return null;

    string? mount = null;
    var rest = string.Empty;

    if (parts[0].Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
    {
        if (parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase))
        {
            mount = "Game";
            rest = string.Join('/', parts.Skip(2));
        }
        else if (parts[1].Equals("Plugins", StringComparison.OrdinalIgnoreCase))
        {
            var contentAt = Array.FindIndex(parts, 2, p => p.Equals("Content", StringComparison.OrdinalIgnoreCase));
            if (contentAt > 2)
            {
                mount = parts[contentAt - 1];
                rest = string.Join('/', parts.Skip(contentAt + 1));
            }
        }
    }
    else if (parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase))
    {
        mount = "Engine";
        rest = string.Join('/', parts.Skip(2));
    }

    if (mount is null || rest.Length == 0) return null;

    var name = rest.Split('/').Last();
    return "/" + mount + "/" + rest + "." + name;
}

static async Task<VersionInfo?> LoadKeysAsync(HttpClient http, string cache)
{
    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
    var cached = Path.Combine(cache, "keys.json");
    var manual = Path.Combine(cache, "keys.manual.json");   // your own keys, same layout as keys.json: wins over everything
    try
    {
        if (File.Exists(manual))
        {
            var m = JsonSerializer.Deserialize<VersionInfo>(await File.ReadAllTextAsync(manual), jsonOpts);
            if (!string.IsNullOrWhiteSpace(m?.Keys?.MainKey?.Key)) return m;
        }
    }
    catch (Exception) { }

    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(KeysUrl));
        var data = doc.RootElement.GetProperty("data");
        var main = data.GetProperty("mainKey").GetString();
        if (!string.IsNullOrWhiteSpace(main))
        {
            var live = new VersionInfo
            {
                Version = data.TryGetProperty("build", out var bv) ? bv.GetString() : null,
                Keys = new KeysInfo { MainKey = new KeyInfo { Key = main }, ExtraKeys = new List<KeyInfo>() }
            };
            if (data.TryGetProperty("dynamicKeys", out var dyn) && dyn.ValueKind == JsonValueKind.Array)
                foreach (var d in dyn.EnumerateArray())
                {
                    var key = d.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var guid = d.TryGetProperty("pakGuid", out var g) ? g.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(guid))
                        live.Keys.ExtraKeys.Add(new KeyInfo { Key = key, Guid = guid.Replace("-", "") });
                }
            try { await File.WriteAllTextAsync(cached, JsonSerializer.Serialize(live, jsonOpts)); } catch (Exception) { }
            return live;
        }
    }
    catch (Exception) { }

    try
    {
        if (File.Exists(cached)) return JsonSerializer.Deserialize<VersionInfo>(await File.ReadAllTextAsync(cached), jsonOpts);
    }
    catch (Exception) { }
    return null;
}

static async Task<string?> FindMappingsAsync(HttpClient http, string cache)
{
    // 1. a .usmap you put in the cache yourself
    var custom = Path.Combine(cache, "custom.usmap");
    if (File.Exists(custom)) return custom;

    // 2. the newest one offered online, downloaded once
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(MappingsListUrl));
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement>();
        JsonElement? best = null;
        DateTime bestAt = DateTime.MinValue;
        foreach (var e in items)
        {
            if (!e.TryGetProperty("url", out var u) || string.IsNullOrWhiteSpace(u.GetString())) continue;
            if (e.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("platform", out var pf) && pf.ValueKind == JsonValueKind.String &&
                !pf.GetString()!.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
            var at = e.TryGetProperty("uploaded", out var up) && DateTime.TryParse(up.GetString(), out var parsed) ? parsed : DateTime.MinValue;
            if (best is null || at > bestAt) { best = e; bestAt = at; }
        }
        if (best is { } pick)
        {
            var url = pick.GetProperty("url").GetString()!;
            var name = pick.TryGetProperty("fileName", out var fn) && !string.IsNullOrWhiteSpace(fn.GetString())
                ? fn.GetString()! : url.Split('?')[0].Split('/').Last();
            name = Path.GetFileName(name);
            var path = Path.Combine(cache, name);
            long? length = pick.TryGetProperty("length", out var ln) && ln.ValueKind == JsonValueKind.Number ? ln.GetInt64() : null;
            if (!File.Exists(path) || (length is not null && new FileInfo(path).Length != length))
            {
                Say("status: Downloading mappings");
                await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url));
            }
            return path;
        }
    }
    catch (Exception) { }

    // 3. the newest one already in the cache
    return Directory.EnumerateFiles(cache, "*.usmap").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}

class VersionInfo
{
    public string? Version { get; set; }
    public KeysInfo? Keys { get; set; }
    public MappingsInfo? Mappings { get; set; }
}

class KeysInfo
{
    public KeyInfo? MainKey { get; set; }
    public List<KeyInfo>? ExtraKeys { get; set; }
}

class KeyInfo
{
    public string Key { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
}

class MappingsInfo
{
    public string? Url { get; set; }
    public string? Md5Hash { get; set; }
}
