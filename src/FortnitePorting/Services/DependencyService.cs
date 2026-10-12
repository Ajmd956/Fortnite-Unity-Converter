using System;
using System.IO;
using System.IO.Compression;
using Avalonia.Platform;
using FortnitePorting.Shared.Extensions;
using Serilog;

namespace FortnitePorting.Services;

public class DependencyService : IService
{
    public bool FinishedEnsuring;
    
    public readonly FileInfo BinkaDecoderFile = new(Path.Combine(App.DataFolder.FullName, "binka", "binkadec.exe"));
    public readonly FileInfo RadaDecoderFile = new(Path.Combine(App.DataFolder.FullName, "rada", "radadec.exe"));
    public readonly FileInfo VgmStreamFile = new(Path.Combine(App.DataFolder.FullName, "vgmstream", "vgmstream-cli.exe"));
    
    public readonly FileInfo EngineFile = new(Path.Combine(App.DataFolder.FullName, "engine", "FortniteUnityPorter.exe"));

    public readonly DirectoryInfo VgmStreamFolder = new(Path.Combine(App.DataFolder.FullName, "vgmstream"));

    public void Ensure()
    {
        TaskService.Run(() =>
        {
            EnsureResource("Assets/Dependencies/binkadec.exe", BinkaDecoderFile);
            EnsureResource("Assets/Dependencies/radadec.exe", RadaDecoderFile);
            EnsureVgmStream();
            EnsureEngine();
            FinishedEnsuring = true;
        });
    }

    private void EnsureResource(string path, FileInfo targetFile)
    {
        var assetStream = AssetLoader.Open(new Uri($"avares://FortnitePorting/{path}"));
        if (targetFile is { Exists: true, Length: > 0 } && targetFile.GetHash() == assetStream.GetHash()) return;

        targetFile.Directory?.Create();
        targetFile.Delete();
        File.WriteAllBytes(targetFile.FullName, assetStream.ReadToEnd());
    }

    private void EnsureEngine()
    {
        try
        {
            EnsureResource("Assets/Dependencies/FortniteUnityPorter.exe", EngineFile);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Conversion engine is not embedded in this build");
        }
    }

    private void EnsureVgmStream()
    {
        if (VgmStreamFile is { Exists: true, Length: > 0 } ) return;
        
        VgmStreamFolder.Create();
        var file = Api.DownloadFile("https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win.zip", VgmStreamFolder);
        if (!file.Exists || file.Length == 0) return;
        
        var zip = ZipFile.Open(file.FullName, ZipArchiveMode.Read);
        foreach (var zipFile in zip.Entries)
        {
            using var zipStream = zipFile.Open();
            using var fileStream = new FileStream(Path.Combine(VgmStreamFolder.FullName, zipFile.FullName), FileMode.OpenOrCreate, FileAccess.Write);
            zipStream.CopyTo(fileStream);
        }
    }
}
