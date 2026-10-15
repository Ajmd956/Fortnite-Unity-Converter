using System.Diagnostics;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Sounds;
using Newtonsoft.Json;

namespace FortnitePorting.MaterialPorter
{
    public class GameContext
    {
        public static string DataDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaterialPorter");

        public IFileProvider Provider { get; set; } = null!;
        public string BuildVersion { get; set; } = "unknown";
        public bool Mounted => Provider != null;
    }

    public static class Timing
    {
        public static void Log(string what, Stopwatch sw)
        {
        }
    }
}

static class AssetExport
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static FortnitePorting.MaterialPorter.MaterialService? Materials { get; set; }

    static async Task<string> TextureFileAsync(string stem, string outRoot, int size)
    {
        var service = Materials ?? throw new InvalidOperationException("the texture service is not running");
        var made = size > 0 ? await service.ThumbnailAsync(stem, size) : await service.TextureAsync(stem);
        if (made is null || string.IsNullOrEmpty(made.File) || !File.Exists(made.File))
            throw new FileNotFoundException("the texture could not be decoded");
        Directory.CreateDirectory(outRoot);
        var name = stem.Substring(stem.LastIndexOf('/') + 1);
        var dest = Path.Combine(outRoot, name + Path.GetExtension(made.File));
        File.Copy(made.File, dest, true);
        return dest;
    }

    public static async Task<string> RunAsync(DefaultFileProvider provider, string kind, string path, string outRoot)
    {
        await Gate.WaitAsync();
        try
        {
            if (kind == "thumb" || kind == "banner")
            {
                var tc = path.Trim().Replace('\\', '/').TrimStart('/');
                return await TextureFileAsync(StripExtension(tc), outRoot, kind == "thumb" ? 128 : 2048);
            }
            var clean = path.Trim().Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0) throw new ArgumentException("path missing");
            var stem = StripExtension(clean);
            var name = stem.Substring(stem.LastIndexOf('/') + 1);
            Directory.CreateDirectory(outRoot);
            using var meshVersion = (kind is "model" or "modelexport" or "meshthumb") ? MeshVersionScope.Enter(provider, stem) : null;

            switch (kind)
            {
                case "raw":
                {
                    string? firstRaw = null;
                    foreach (var ext in new[] { "uasset", "umap", "uexp", "ubulk", "uptnl" })
                    {
                        if (!provider.TrySaveAsset(stem + "." + ext, out var data)) continue;
                        var rawFile = Path.Combine(outRoot, name + "." + ext);
                        await File.WriteAllBytesAsync(rawFile, data);
                        firstRaw ??= rawFile;
                    }
                    if (firstRaw is null) throw new FileNotFoundException("that file is not in the game archives");
                    return firstRaw;
                }
                case "json":
                {
                    if (!provider.TryLoadPackage(clean, out var package))
                        throw new FileNotFoundException("that file is not in the game archives");
                    var json = JsonConvert.SerializeObject(package.GetExports(), new JsonSerializerSettings
                    {
                        Formatting = Formatting.Indented,
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                    });
                    var file = Path.Combine(outRoot, name + ".json");
                    await File.WriteAllTextAsync(file, json, new UTF8Encoding(false));
                    return file;
                }
                case "sound":
                {
                    var obj = provider.LoadPackageObject(stem);
                    obj.Decode(true, out var format, out var data);
                    if (data is null || data.Length == 0)
                        obj.Decode(false, out format, out data);
                    if (data is null || data.Length == 0)
                        throw new InvalidDataException("this asset has no sound data of its own");
                    var ext = string.IsNullOrWhiteSpace(format) ? "bin" : format.ToLowerInvariant();
                    if (ext is "rada" or "binka" or "adpcm")
                    {
                        var wav = await SoundDecoders.ToWavAsync(ext, data, outRoot, name);
                        if (wav is not null) return wav;
                    }
                    var file = Path.Combine(outRoot, name + "." + ext);
                    await File.WriteAllBytesAsync(file, data);
                    return file;
                }
                case "matgraph":
                {
                    // the material's (or material function's) node graph, as the game stores it in its editor-only data
                    var service = Materials ?? throw new InvalidOperationException("the material service is not running");
                    var graphFile = await service.GraphAsync(stem);
                    var graphDest = Path.Combine(outRoot, name + ".json");
                    File.Copy(graphFile, graphDest, true);
                    return graphDest;
                }
                case "modelexport":
                {
                    // the Files page export: just the model file in the chosen folder, no textures and no subfolders
                    var work = Path.Combine(Path.GetTempPath(), "fuc_model_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    try
                    {
                        var mobj = ResolveMesh(provider, stem);
                        var mres = await RunWithDiag(provider, stem, mobj, true, () => RunWithRetry(
                            () => { var s1 = new ExportSession(); s1.Add(mobj); return s1.RunAsync(work, new ExportOptions(meshFormat: EMeshFormat.Gltf2, textureFormat: ETextureFormat.Png)); },
                            () => WithoutNanite(provider, stem, alt => { var s2 = new ExportSession(); s2.Add(alt); return s2.RunAsync(work, new ExportOptions(meshFormat: EMeshFormat.Gltf2, textureFormat: ETextureFormat.Png, meshQuality: EMeshQuality.All, naniteMeshFormat: ENaniteMeshFormat.NoNanite)); }),
                            r => r.All(x => !x.Success),
                            true));
                        var bad = mres.FirstOrDefault(r => !r.Success);
                        if (bad is not null && mres.All(r => !r.Success))
                            throw new InvalidDataException((bad.Error?.Message ?? "export failed") + " " + MeshDiag(provider, stem, mobj));
                        var made = Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).ToList();
                        var model = made.FirstOrDefault(f => f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                                    ?? made.FirstOrDefault(f => f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException("the export produced no model file");
                        var dest = Path.Combine(outRoot, name + Path.GetExtension(model));
                        File.Copy(model, dest, true);
                        if (model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                        {
                            var bin = Path.ChangeExtension(model, ".bin");
                            if (File.Exists(bin)) File.Copy(bin, Path.ChangeExtension(dest, ".bin"), true);
                        }
                        return dest;
                    }
                    finally { try { Directory.Delete(work, true); } catch { } }
                }
                case "texture":
                case "model":
                {
                    if (kind == "texture")
                    {
                        try { return await TextureFileAsync(stem, outRoot, 0); }
                        catch (Exception fastPathError) when (fastPathError is not FileNotFoundException) { }
                    }
                    var obj = kind == "model" ? ResolveMesh(provider, stem) : provider.LoadPackageObject(stem);
                    var results = await RunWithDiag(provider, stem, obj, kind == "model", () => RunWithRetry(
                        () => { var s1 = new ExportSession(); s1.Add(obj); return s1.RunAsync(outRoot, new ExportOptions(meshFormat: EMeshFormat.Gltf2, textureFormat: ETextureFormat.Png)); },
                        () => WithoutNanite(provider, stem, alt => { var s2 = new ExportSession(); s2.Add(alt); return s2.RunAsync(outRoot, new ExportOptions(meshFormat: EMeshFormat.Gltf2, textureFormat: ETextureFormat.Png, meshQuality: EMeshQuality.All, naniteMeshFormat: ENaniteMeshFormat.NoNanite)); }),
                        r => r.All(x => !x.Success),
                        kind == "model"));
                    var failed = results.FirstOrDefault(r => !r.Success);
                    if (failed is not null && results.All(r => !r.Success))
                        throw kind == "model" ? new InvalidDataException((failed.Error?.Message ?? "export failed") + " " + MeshDiag(provider, stem, obj)) : (failed.Error ?? new InvalidDataException("export failed"));
                    var first = results.SelectMany(r => r.DiskFilePaths ?? Array.Empty<string>()).FirstOrDefault();
                    return first ?? outRoot;
                }
                case "meshthumb":
                {
                    var obj = ResolveMesh(provider, stem);
                    var file = Path.Combine(outRoot, name + ".mesh");
                    try
                    {
                    switch (obj)
                    {
                        case UStaticMesh staticMesh:
                        {
                            var dto = new StaticMeshDto(staticMesh, EMeshQuality.Highest);
                            if (dto.LODs.Count == 0) { dto.Dispose(); dto = new StaticMeshDto(staticMesh, EMeshQuality.All, ENaniteMeshFormat.NoNanite); }
                            using (dto) WriteMeshThumb(file, dto);
                            break;
                        }
                        case USkeletalMesh skeletalMesh:
                        {
                            var dto = new SkeletalMeshDto(skeletalMesh, EMeshQuality.Highest, exportMorphTarget: false);
                            if (dto.LODs.Count == 0)
                            {
                                dto.Dispose();
                                var plain = LoadWithoutNanite(provider, stem) as USkeletalMesh ?? skeletalMesh;
                                dto = new SkeletalMeshDto(plain, EMeshQuality.All, ENaniteMeshFormat.NoNanite, false);
                            }
                            using (dto) WriteMeshThumb(file, dto);
                            break;
                        }
                        default:
                            throw new InvalidDataException("this asset is not a mesh");
                    }
                    }
                    catch (Exception e) when (e is not InvalidDataException { Message: "this asset is not a mesh" })
                    {
                        throw new InvalidDataException(e.Message + " " + MeshDiag(provider, stem, obj), e);
                    }
                    return file;
                }
                default:
                    throw new ArgumentException("unknown export kind " + kind);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    sealed class MeshVersionScope : IDisposable
    {
        static readonly EGame[] Fallbacks = { EGame.GAME_UE5_8, EGame.GAME_UE5_7, EGame.GAME_UE5_6, EGame.GAME_UE5_5 };

        readonly IFileProvider _provider;
        readonly EGame _original;
        readonly bool _originalNanite;

        MeshVersionScope(IFileProvider provider)
        {
            _provider = provider;
            _original = provider.Versions.Game;
            _originalNanite = provider.ReadNaniteData;
        }

        public static MeshVersionScope? Enter(IFileProvider provider, string stem)
        {
            if (MeshIsUsable(provider, stem)) return null;
            var scope = new MeshVersionScope(provider);
            foreach (var nanite in new[] { scope._originalNanite, false })
            {
                provider.ReadNaniteData = nanite;
                foreach (var candidate in Fallbacks.Prepend(scope._original).Distinct())
                {
                    if (nanite == scope._originalNanite && candidate == scope._original) continue;
                    provider.Versions.Game = candidate;
                    if (MeshIsUsable(provider, stem)) return scope;
                }
            }
            scope.Dispose();
            return null;
        }

        public void Dispose()
        {
            _provider.Versions.Game = _original;
            _provider.ReadNaniteData = _originalNanite;
        }

        static bool MeshIsUsable(IFileProvider provider, string stem)
        {
            var prevFatal = CUE4Parse.Globals.FatalObjectSerializationErrors;
            CUE4Parse.Globals.FatalObjectSerializationErrors = true;
            try
            {
                foreach (var lazy in provider.LoadPackage(stem).ExportsLazy)
                {
                    var export = lazy.Value;
                    switch (export)
                    {
                        case USkeletalMesh skeletal:
                        {
                            using var dto = new SkeletalMeshDto(skeletal, EMeshQuality.Highest, exportMorphTarget: false);
                            if (dto.LODs.Count == 0) return false;
                            break;
                        }
                        case UStaticMesh staticMesh when staticMesh.RenderData?.LODs is not null:
                        {
                            using var dto = new StaticMeshDto(staticMesh, EMeshQuality.Highest);
                            if (dto.LODs.Count == 0) return false;
                            break;
                        }
                    }
                }
                return true;
            }
            catch (Exception) { return false; }
            finally { CUE4Parse.Globals.FatalObjectSerializationErrors = prevFatal; }
        }
    }

    static UObject ResolveMesh(IFileProvider provider, string stem)
    {
        var obj = provider.LoadPackageObject(stem);
        if (obj is UStaticMesh sm0 && sm0.RenderData?.LODs is null)
        {
            try
            {
                var alt = provider.LoadPackage(stem).GetExports().OfType<UStaticMesh>().FirstOrDefault(m => m.RenderData?.LODs is not null);
                if (alt is not null) return alt;
            }
            catch (Exception) { }
        }
        return obj;
    }

    static UObject LoadWithoutNanite(IFileProvider provider, string stem)
    {
        var prev = provider.ReadNaniteData;
        provider.ReadNaniteData = false;
        try { return ResolveMesh(provider, stem); }
        finally { provider.ReadNaniteData = prev; }
    }

    static Task<T> WithoutNanite<T>(IFileProvider provider, string stem, Func<UObject, Task<T>> run)
        => run(LoadWithoutNanite(provider, stem));

    static string MeshDiag(IFileProvider provider, string stem, UObject? obj)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("[diag] class=").Append(obj?.GetType().Name).Append(" exportType=").Append(obj?.ExportType).Append(" name=").Append(obj?.Name);
            if (obj is UStaticMesh sm) sb.Append(" bCooked=").Append(sm.bCooked).Append(" renderData=").Append(sm.RenderData is null ? "null" : "lods=" + (sm.RenderData.LODs?.Length.ToString() ?? "null"));
            sb.Append(" readNanite=").Append(provider.ReadNaniteData);
            sb.Append(" exports=");
            foreach (var o in provider.LoadPackage(stem).GetExports()) sb.Append(o.Name).Append(':').Append(o.GetType().Name).Append(o is UStaticMesh m ? (m.RenderData is null ? "(noRD)" : "(rd)") : "").Append(',');
            var prevFatal = CUE4Parse.Globals.FatalObjectSerializationErrors;
            CUE4Parse.Globals.FatalObjectSerializationErrors = true;
            try
            {
                foreach (var lazy in provider.LoadPackage(stem).ExportsLazy)
                {
                    try { _ = lazy.Value; }
                    catch (Exception e)
                    {
                        var inner = e.InnerException ?? e;
                        var st = (inner.StackTrace ?? "").Split('\n').Take(4).Select(x => x.Trim());
                        sb.Append(" parseError=").Append(inner.GetType().Name).Append(": ").Append(inner.Message).Append(" at ").Append(string.Join(" <- ", st));
                    }
                }
            }
            finally { CUE4Parse.Globals.FatalObjectSerializationErrors = prevFatal; }
            return sb.ToString();
        }
        catch (Exception e) { return "[diag failed: " + e.Message + "]"; }
    }

    static async Task<T> RunWithDiag<T>(IFileProvider provider, string stem, UObject obj, bool diag, Func<Task<T>> run)
    {
        try { return await run(); }
        catch (Exception e) when (diag) { throw new InvalidDataException(e.Message + " " + MeshDiag(provider, stem, obj), e); } // model only (see caller)
    }

    static async Task<T> RunWithRetry<T>(Func<Task<T>> first, Func<Task<T>> second, Func<T, bool> failed, bool allowRetry)
    {
        Exception? firstError = null;
        T result = default!;
        try { result = await first(); }
        catch (Exception e) when (allowRetry) { firstError = e; }
        if (!allowRetry || (firstError is null && !failed(result))) return result;
        try { return await second(); }
        catch (Exception e) { throw firstError ?? e; }
    }

    static void WriteMeshThumb<TVertex>(string file, MeshDto<TVertex> dto) where TVertex : struct, IMeshVertex
    {
        if (dto.LODs.Count == 0) throw new InvalidDataException("mesh has no LOD data");
        var lod = dto.LODs[0];
        if (lod.Vertices.Length == 0 || lod.Indices.Length < 3) throw new InvalidDataException("mesh has no triangles");
        using var stream = File.Create(file);
        using var writer = new BinaryWriter(stream);
        writer.Write(lod.Vertices.Length);
        writer.Write(lod.Indices.Length);
        foreach (var vertex in lod.Vertices)
        {
            writer.Write((float) vertex.Position.X);
            writer.Write((float) vertex.Position.Y);
            writer.Write((float) vertex.Position.Z);
            writer.Write((float) vertex.Normal.X);
            writer.Write((float) vertex.Normal.Y);
            writer.Write((float) vertex.Normal.Z);
        }
        foreach (var index in lod.Indices) writer.Write(index);
    }

    static string StripExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.IndexOf('.', slash + 1);
        return dot >= 0 ? path.Substring(0, dot) : path;
    }
}

static class SoundDecoders
{
    static string? Find(string tool)
    {
        var bases = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, ".."), Path.Combine(AppContext.BaseDirectory, "..", "..") };
        foreach (var b in bases)
        {
            var p = Path.GetFullPath(Path.Combine(b, "tools", tool));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static async Task<string?> VgmStreamAsync()
    {
        try
        {
            var dir = Path.Combine(FortnitePorting.MaterialPorter.GameContext.DataDir, "vgmstream");
            var exe = Path.Combine(dir, "vgmstream-cli.exe");
            if (File.Exists(exe)) return exe;
            Directory.CreateDirectory(dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteUnityConverter");
            var zip = Path.Combine(dir, "vgmstream-win.zip");
            await File.WriteAllBytesAsync(zip, await http.GetByteArrayAsync("https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win.zip"));
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, true);
            File.Delete(zip);
            return File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> ToWavAsync(string format, byte[] data, string outRoot, string name)
    {
        var wav = Path.Combine(outRoot, name + ".wav");
        var temp = Path.Combine(Path.GetTempPath(), "FUC_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var input = Path.Combine(temp, "in." + format);
            await File.WriteAllBytesAsync(input, data);
            string? exe;
            string args;
            switch (format)
            {
                case "rada":
                    exe = Find("radadec.exe");
                    args = "-i \"" + input + "\" -o \"" + wav + "\"";
                    break;
                case "binka":
                    exe = Find("binkadec.exe");
                    args = "-i \"" + input + "\" -o \"" + wav + "\"";
                    break;
                case "adpcm":
                    exe = await VgmStreamAsync();
                    args = "-o \"" + wav + "\" \"" + input + "\"";
                    break;
                default:
                    return null;
            }
            if (exe is null) return null;
            if (File.Exists(wav)) File.Delete(wav);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null) return null;
            var outText = proc.StandardOutput.ReadToEndAsync();
            var errText = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
            return File.Exists(wav) && new FileInfo(wav).Length > 44 ? wav : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
