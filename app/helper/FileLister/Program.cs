using System.Security.Cryptography;
using FortnitePorting.MaterialPorter;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Versions;

// The app looks after its own game keys and mappings: keys come from fortnite-api.com (and are kept in the
// cache for when it is down), mappings from your own .usmap, the cache, or the Fortnite Central list.
const string ZeroKey = "0x0000000000000000000000000000000000000000000000000000000000000000";
const string KeysUrl = "https://fortnite-api.com/v2/aes";
const string PortingVersionUrl = "https://api.fortniteporting.app/v2/fortnite/versions/latest";
// Same service Fortnite Porting uses for "Fetch Version": keys and mappings for any game version, e.g. .../versions/38.11
const string PortingVersionBase = "https://api.fortniteporting.app/v2/fortnite/versions/";
const string MappingsListUrl = "https://fortnitecentral.genxgames.gg/api/v1/mappings";

var archive = ArgValue(args, "--archive");
var output = ArgValue(args, "--out");
var cache = ArgValue(args, "--cache");
// --version 38.11 : read keys and mappings saved for that game version only (never the latest ones from the internet).
// --local-textures : never download texture data from Epic's servers; only what is already on disk is used.
var gameVersion = ArgValue(args, "--version").Trim();
var localTextures = Array.IndexOf(args, "--local-textures") >= 0;
// --keys-file <path> : keys typed in by the user (a Custom profile). Same layout as keys.json; used instead of any download.
// --no-nanite / --no-bundles : turn off Nanite geometry / the pre-downloaded streamed assets (the profile's switches).
var keysFile = ArgValue(args, "--keys-file").Trim();
var loadNanite = Array.IndexOf(args, "--no-nanite") < 0;
var loadBundles = Array.IndexOf(args, "--no-bundles") < 0;
// Profile modes (same as Fortnite Porting's Installation profiles):
// default      = Latest (Installed): latest keys + mappings from the service, installed bundles and streamed textures.
// --custom     = Custom: only the keys typed in the profile (--keys-file), the --mappings file, no bundles, no streamed textures.
// --language X : ELanguage name for in-game text (English leaves it alone).
var custom = Array.IndexOf(args, "--custom") >= 0;
var mappingsArg = ArgValue(args, "--mappings").Trim();
var languageArg = ArgValue(args, "--language").Trim();
// --ue GAME_UE5_5 : Unreal Engine version used to read the files. Leave out for the newest (what Fortnite Porting uses by default).
var gameEngine = Enum.TryParse<EGame>(ArgValue(args, "--ue").Trim(), true, out var parsedEngine) ? parsedEngine : EGame.GAME_UE6_0;

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

    var localKeys = ReadLocalKeys(keysFile);
    VersionInfo? info = null;
    string? mappingsPath;
    if (custom)
    {
        var typedMain = localKeys.Main.Length > 0 ? localKeys.Main : ZeroKey;
        try { _ = new FAesKey(typedMain); }
        catch (Exception)
        {
            Say("error: bad-keys");
            return 4;
        }
        mappingsPath = mappingsArg.Length > 0 && File.Exists(mappingsArg) ? mappingsArg : null;
        if (mappingsPath is null) Say("status: No mappings file set, reading without it");
    }
    else
    {
        info = await LoadKeysAsync(http, cache, gameVersion, "");
        if (info is null)
        {
            Say(gameVersion.Length > 0 ? "error: no-keys-version" : "error: api-unreachable");
            return 4;
        }

        if (info.Keys?.MainKey is null || string.IsNullOrWhiteSpace(info.Keys.MainKey.Key))
        {
            Say("error: no-keys");
            return 4;
        }

        mappingsPath = await FindMappingsAsync(http, cache, gameVersion);
        if (mappingsPath is null)
        {
            if (serve)
            {
                Say("error: no-mappings");
                return 4;
            }
            Say("status: No mappings found, reading without them");
        }
    }

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
    var provider = new DefaultFileProvider(archive, SearchOption.TopDirectoryOnly, new VersionContainer(gameEngine, optionOverrides: new Dictionary<string, bool> { ["StaticMesh.KeepMobileMinLODSettingOnDesktop"] = true }), StringComparer.OrdinalIgnoreCase);
    provider.OnDemandOptions = new IoStoreOnDemandOptions
    {
        ChunkHostUri = new Uri("https://egdownload.fastly-edge.com/", UriKind.Absolute),
        ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(cache, "chunks")),
        // local-textures: every download request fails at once, so only chunks already in the cache folder are read
        DownloaderClient = localTextures ? new HttpClient(new OfflineHandler()) : new HttpClient(new StreamGuardHandler()) { Timeout = TimeSpan.FromSeconds(30) },
        Timeout = TimeSpan.FromSeconds(30)
    };
    provider.Initialize();
    provider.ReadNaniteData = loadNanite; // Fortnite Porting turns this on by default; Nanite meshes fail to read without it

    if (!custom)
    {
        if (loadBundles) await LoadInstalledBundlesAsync(provider);
        await LoadTextureStreamingAsync(http, provider, archive, cache, localTextures);
    }

    if (custom || info is null)
    {
        await provider.SubmitKeyAsync(new FGuid(), new FAesKey(localKeys.Main.Length > 0 ? localKeys.Main : ZeroKey));
    }
    else
    {
        await provider.SubmitKeyAsync(new FGuid(), new FAesKey(info.Keys!.MainKey!.Key));
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
    }

    // Extra keys typed into the profile: each one is tried on every archive that is still locked (Fortnite Porting does the same)
    foreach (var vfs in provider.UnloadedVfs.ToArray())
    {
        foreach (var extraKey in localKeys.Extra)
        {
            try
            {
                var key = new FAesKey(extraKey);
                if (!vfs.TestAesKey(key)) continue;
                await provider.SubmitKeyAsync(vfs.EncryptionKeyGuid, key);
            }
            catch (Exception)
            {
            }
        }
    }

    if (mappingsPath is not null)
        provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappingsPath, StringComparer.Ordinal);

    provider.LoadVirtualPaths();

    if (Enum.TryParse<ELanguage>(languageArg, true, out var gameLanguage) && gameLanguage != ELanguage.English)
    {
        if (!provider.TryChangeCulture(provider.GetLanguageCode(gameLanguage)))
            Say("status: Failed to load the language " + languageArg);
    }

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
                    BuildVersion = string.IsNullOrWhiteSpace(info?.Version) ? "unknown" : info!.Version!
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

// The profile's own keys: { "mainKey": "0x...", "extraKeys": ["0x...", ...] }. Fortnite Porting's extra keys have no GUID;
// each one is tried on the locked archives.
static (string Main, List<string> Extra) ReadLocalKeys(string file)
{
    var extra = new List<string>();
    var main = string.Empty;
    try
    {
        if (file.Length > 0 && File.Exists(file))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.TryGetProperty("mainKey", out var m) && m.ValueKind == JsonValueKind.String) main = (m.GetString() ?? string.Empty).Trim();
            if (root.TryGetProperty("extraKeys", out var e) && e.ValueKind == JsonValueKind.Array)
                foreach (var k in e.EnumerateArray())
                    if (k.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(k.GetString())) extra.Add(k.GetString()!.Trim());
        }
    }
    catch (Exception)
    {
    }
    return (main, extra);
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

static async Task LoadInstalledBundlesAsync(DefaultFileProvider provider)
{
    // The Epic Games Launcher option "Pre-Download Streamed Assets" puts the full-size texture data on your PC in this
    // folder. Fortnite Porting mounts it ("Load Installed Bundles"), so textures come from your files instead of Epic's servers.
    try
    {
        var dir = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FortniteGame", "Saved", "PersistentDownloadDir", "GameCustom", "InstalledBundles"));
        if (!dir.Exists)
        {
            Say("status: No pre-downloaded streamed assets on this PC (Epic Games Launcher > Fortnite > Options > Pre-Download Streamed Assets)");
            return;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var containers = 0;
        foreach (var file in dir.EnumerateFiles("*.*", options))
        {
            var extension = file.Extension.TrimStart('.').ToLowerInvariant();
            if (extension is "pak" or "utoc")
            {
                provider.RegisterVfs(file.FullName, [file.OpenRead()], it => new FStreamArchive(it, File.OpenRead(it), provider.Versions));
                containers++;
            }
            else if (extension is "uondemandtoc")
            {
                await provider.RegisterVfsAsync(new IoChunkToc(file.FullName, provider.Versions));
                containers++;
            }
        }

        Say(containers > 0 ? "status: Loaded " + containers + " pre-downloaded streamed asset files" : "status: The pre-downloaded streamed assets folder is empty");
    }
    catch (Exception ex)
    {
        Say("status: Pre-downloaded streamed assets failed to load (" + ex.Message.Replace('\r', ' ').Replace('\n', ' ') + ")");
    }
}

static async Task LoadTextureStreamingAsync(HttpClient http, DefaultFileProvider provider, string archive, string cache, bool localOnly)
{
    // Fortnite keeps the large texture mips in streamed (on-demand) chunks, not in the paks. Without them every
    // texture can only be read at its smallest stored mip (64x64). Same steps as Fortnite Porting's texture streaming.
    try
    {
        var iniPath = Path.GetFullPath(Path.Combine(archive, "..", "..", "..", "Cloud", "IoStoreOnDemand.ini"));
        if (!File.Exists(iniPath))
        {
            Say("status: Streamed textures are not installed, textures will be low resolution");
            return;
        }

        var match = System.Text.RegularExpressions.Regex.Match(await File.ReadAllTextAsync(iniPath), "^\\s*TocPath\\s*=\\s*\"?([^\"\\r\\n]+?)\"?\\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!match.Success)
        {
            Say("status: Streamed textures: no TocPath found, textures will be low resolution");
            return;
        }

        var tocPath = match.Groups[1].Value.Trim().TrimStart('/');
        var tocFile = new FileInfo(Path.Combine(cache, "chunks", tocPath.Substring(tocPath.LastIndexOf('/') + 1)));
        if (localOnly && (!tocFile.Exists || tocFile.Length == 0))
        {
            Say("status: Local textures only: no streamed texture index on disk, textures use the mips stored in the game files");
            return;
        }

        if (!tocFile.Exists || tocFile.Length == 0)
        {
            Say("status: Downloading streamed texture index");
            var bytes = await http.GetByteArrayAsync("https://download.epicgames.com/" + tocPath);
            await File.WriteAllBytesAsync(tocFile.FullName, bytes);
        }

        Say("status: Loading streamed textures");
        await provider.RegisterVfsAsync(new IoChunkToc(tocFile.FullName, provider.Versions));
        await provider.MountAsync();
    }
    catch (Exception ex)
    {
        Say("status: Streamed textures failed to load (" + ex.Message.Replace('\r', ' ').Replace('\n', ' ') + "), textures will be low resolution");
    }
}

static async Task<VersionInfo?> LoadKeysAsync(HttpClient http, string cache, string version, string keysFile)
{
    if (keysFile.Length > 0)
    {
        try
        {
            if (File.Exists(keysFile))
            {
                var typed = JsonSerializer.Deserialize<VersionInfo>(await File.ReadAllTextAsync(keysFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(typed?.Keys?.MainKey?.Key)) return typed;
            }
        }
        catch (Exception) { }
        Say("status: The encryption keys in this profile could not be read");
        return null;
    }

    if (version.Length > 0)
    {
        // A chosen game version never uses the live keys (they belong to the newest build). Only keys saved for
        // that version are used: keys.manual.<version>.json or keys.<version>.json in the cache folder.
        var safe = SafeVersion(version);
        foreach (var name in new[] { "keys.manual." + safe + ".json" })
        {
            try
            {
                var file = Path.Combine(cache, name);
                if (!File.Exists(file)) continue;
                var v = JsonSerializer.Deserialize<VersionInfo>(await File.ReadAllTextAsync(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(v?.Keys?.MainKey?.Key)) return v;
            }
            catch (Exception) { }
        }
        // Same lookup Fortnite Porting's "Fetch Version" does: keys for exactly this version.
        try
        {
            var fetchOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
            var fetched = JsonSerializer.Deserialize<VersionInfo>(await http.GetStringAsync(PortingVersionBase + Uri.EscapeDataString(version)), fetchOpts);
            if (!string.IsNullOrWhiteSpace(fetched?.Keys?.MainKey?.Key))
            {
                try { await File.WriteAllTextAsync(Path.Combine(cache, "keys." + safe + ".json"), JsonSerializer.Serialize(fetched, fetchOpts)); } catch (Exception) { }
                return fetched;
            }
        }
        catch (Exception) { }

        try
        {
            var saved = Path.Combine(cache, "keys." + safe + ".json");
            if (File.Exists(saved))
            {
                var v = JsonSerializer.Deserialize<VersionInfo>(await File.ReadAllTextAsync(saved), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!string.IsNullOrWhiteSpace(v?.Keys?.MainKey?.Key)) return v;
            }
        }
        catch (Exception) { }

        Say("status: No keys found for version " + version + " (not available online, and no keys.manual." + safe + ".json in the cache folder)");
        return null;
    }

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

static bool IsUsmap(string path)
{
    try
    {
        using var f = File.OpenRead(path);
        if (f.Length < 64) return false;
        var head = new byte[2];
        return f.Read(head, 0, 2) == 2 && head[0] == 0xC4 && head[1] == 0x30;
    }
    catch (Exception)
    {
        return false;
    }
}

static async Task<bool> DownloadUsmapAsync(HttpClient http, string url, string path)
{
    var part = path + ".part";
    try
    {
        var bytes = await http.GetByteArrayAsync(url);
        if (bytes.Length < 64 || bytes[0] != 0xC4 || bytes[1] != 0x30) return false;
        await File.WriteAllBytesAsync(part, bytes);
        File.Move(part, path, true);
        return true;
    }
    catch (Exception)
    {
        try { File.Delete(part); } catch (Exception) { }
        return false;
    }
}

static JsonElement? Prop(JsonElement element, string name)
{
    if (element.ValueKind != JsonValueKind.Object) return null;
    foreach (var prop in element.EnumerateObject())
        if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return prop.Value;
    return null;
}

static string KeepOnlyMappings(string cache, string keep)
{
    try
    {
        var keepFull = Path.GetFullPath(keep);
        foreach (var file in Directory.EnumerateFiles(cache, "*.usmap").ToList())
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("custom.usmap", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("custom.", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFullPath(file).Equals(keepFull, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch (Exception) { }
        }
    }
    catch (Exception) { }
    return keep;
}

static string SafeVersion(string v) => new string(v.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_').ToArray());

static async Task<string?> FindMappingsAsync(HttpClient http, string cache, string version)
{
    if (version.Length == 0) return await FindLatestMappingsAsync(http, cache);

    // Your own custom.<version>.usmap always wins. Otherwise the mappings for exactly this version are fetched from
    // the same service Fortnite Porting uses, and kept as custom.<version>.fetched.usmap.
    var safe = SafeVersion(version);
    var own = Path.Combine(cache, "custom." + safe + ".usmap");
    if (File.Exists(own) && IsUsmap(own)) return own;

    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(PortingVersionBase + Uri.EscapeDataString(version)));
        var mappings = Prop(doc.RootElement, "mappings");
        var url = mappings is { } m ? Prop(m, "url")?.GetString() : null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var md5 = Prop(mappings!.Value, "md5Hash")?.GetString();
            var target = Path.Combine(cache, "custom." + safe + ".fetched.usmap");
            if (File.Exists(target) && IsUsmap(target) && (string.IsNullOrWhiteSpace(md5) || HashMatches(target, md5))) return target;
            Say("status: Downloading mappings for " + version);
            if (await DownloadUsmapAsync(http, url, target)) return target;
        }
    }
    catch (Exception) { }

    return FindVersionMappings(cache, version);
}

static string? FindVersionMappings(string cache, string version)
{
    // Mappings for a chosen version come from a file you saved: custom.<version>.usmap, or any .usmap whose name contains the version.
    var safe = SafeVersion(version);
    var exact = Path.Combine(cache, "custom." + safe + ".usmap");
    if (File.Exists(exact) && IsUsmap(exact)) return exact;
    return Directory.EnumerateFiles(cache, "*.usmap")
        .Where(f => Path.GetFileName(f).Contains(safe, StringComparison.OrdinalIgnoreCase) && IsUsmap(f))
        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
}

static async Task<string?> FindLatestMappingsAsync(HttpClient http, string cache)
{
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(PortingVersionUrl));
        var mappings = Prop(doc.RootElement, "mappings");
        var url = mappings is { } m ? Prop(m, "url")?.GetString() : null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            var md5 = Prop(mappings!.Value, "md5Hash")?.GetString();
            var name = Path.GetFileName(url.Split('?')[0]);
            if (name.Length > 0)
            {
                var path = Path.Combine(cache, name);
                if (File.Exists(path) && IsUsmap(path) && (string.IsNullOrWhiteSpace(md5) || HashMatches(path, md5))) return KeepOnlyMappings(cache, path);
                Say("status: Downloading mappings");
                if (await DownloadUsmapAsync(http, url, path)) return KeepOnlyMappings(cache, path);
            }
        }
    }
    catch (Exception) { }

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
            if (File.Exists(path) && IsUsmap(path) && (length is null || new FileInfo(path).Length == length)) return KeepOnlyMappings(cache, path);
            Say("status: Downloading mappings");
            if (await DownloadUsmapAsync(http, url, path)) return KeepOnlyMappings(cache, path);
        }
    }
    catch (Exception) { }

    var custom = Path.Combine(cache, "custom.usmap");
    if (File.Exists(custom)) return custom;

    return Directory.EnumerateFiles(cache, "*.usmap").Where(IsUsmap).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
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
