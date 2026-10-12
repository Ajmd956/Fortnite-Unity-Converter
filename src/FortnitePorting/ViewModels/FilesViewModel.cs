using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.Utils;
using DynamicData;
using FluentAvalonia.UI.Controls;
using FortnitePorting.CUE4Parse.Extensions;
using FortnitePorting.CUE4Parse.Models.Unreal;
using FortnitePorting.CUE4Parse.Models.Unreal.VirtualTexture;
using FortnitePorting.Exporting;
using FortnitePorting.Exporting.Context;
using FortnitePorting.Exporting.Models;
using FortnitePorting.Exporting.Models.Files;
using FortnitePorting.Exporting.Models.Files.Meta;
using FortnitePorting.Extensions;
using FortnitePorting.Framework;
using FortnitePorting.Services;
using FortnitePorting.Models;
using FortnitePorting.Models.Files;
using FortnitePorting.Models.Information;
using FortnitePorting.Shared.Extensions;
using FortnitePorting.Views;
using FortnitePorting.Windows;
using Newtonsoft.Json;
using Serilog;

namespace FortnitePorting.ViewModels;

public partial class FilesViewModel(
    FilesService files,
    ExportService exporter,
    CUE4ParseService ueParse,
    InfoService info,
    NavigationService navigation,
    SettingsService settings,
    AppService app) : ViewModelBase, IResettable
{
    [ObservableProperty] private FilesService _files = files;

    private readonly ExportService _exporter = exporter;
    private readonly CUE4ParseService _ueParse = ueParse;
    private readonly InfoService _info = info;
    private readonly NavigationService _navigation = navigation;
    private readonly SettingsService _settings = settings;
    private readonly AppService _app = app;

    [ObservableProperty] private FileBrowserContext _context = new()
    {
        IsDragDropEnabled = true
    };

    public void Reset()
    {
        Context.Reset();
        Context = new FileBrowserContext { IsDragDropEnabled = true };
        InvalidateInitialization();
    }

    public override async Task Initialize()
    {
        if (_ueParse.Provider is null) return;
        Context.Initialize();
    }

    public override async Task OnViewOpened()
    {
    }

    public void JumpTo(string path)
    {
        Context.JumpTo(path);
    }

    [RelayCommand]
    public async Task Properties()
    {
        var selectedPaths = (Context.UseFlatView
            ? Context.SelectedFlatViewItems.Select(f => f.Path)
            : Context.SelectedFileViewItems.Where(f => f.Type == ENodeType.File).Select(f => f.FilePath)).ToList();

        foreach (var path in selectedPaths)
            await PreviewProperties(path);
    }

    private async Task PreviewProperties(string path)
    {
        try
        {
            if (_ueParse.Provider.TryLoadObjectExports(path, out var exports))
            {
                var json = JsonConvert.SerializeObject(exports, Formatting.Indented);
                await TaskService.RunDispatcherAsync(() =>
                    PropertiesPreviewWindow.Preview(
                        path.SubstringAfterLast("/").SubstringBefore("."), json));
            }
        }
        catch (Exception)
        {
            _info.Message("Properties", $"Failed to preview {path}");
        }
    }

    private async Task PreviewProperties(UObject asset)
    {
        try
        {
            var path = _exporter.FixPath(asset.GetPathName());
            var exports = await _ueParse.Provider.LoadAllObjectsAsync(path);
            var json = JsonConvert.SerializeObject(exports, Formatting.Indented);
            await TaskService.RunDispatcherAsync(() =>
                PropertiesPreviewWindow.Preview(asset.Name, json));
        }
        catch (Exception)
        {
            _info.Message("Properties", $"Failed to preview {asset.Name}");
        }
    }

    [RelayCommand]
    public async Task Preview()
    {
        var selectedPaths = (Context.UseFlatView
            ? Context.SelectedFlatViewItems.Select(f => f.Path)
            : Context.SelectedFileViewItems.Where(f => f.Type == ENodeType.File).Select(f => f.FilePath)).ToList();

        var loadedAssets = new List<UObject>();
        foreach (var path in selectedPaths)
        {
            var basePath = _exporter.FixPath(path);

            UObject? asset;
            if (path.EndsWith(".umap"))
            {
                var package = await _ueParse.Provider.LoadPackageAsync(basePath);
                asset = package.GetExports().OfType<UWorld>().FirstOrDefault();
            }
            else
            {
                asset = await _ueParse.Provider.SafeLoadPackageObjectAsync(basePath);
                asset ??= await _ueParse.Provider.SafeLoadPackageObjectAsync(
                    $"{basePath}.{basePath.SubstringAfterLast("/")}_C");
            }

            asset = TransformAssetForPreview(asset);
            if (asset is null) continue;
            loadedAssets.Add(asset);
        }

        if (loadedAssets.Count == 0)
        {
            await Properties();
            return;
        }

        var meshAssets = loadedAssets.Where(x => x is ULevel or UStaticMesh or USkeletalMesh).ToArray();
        if (meshAssets.Length > 0)
        {
            loadedAssets.RemoveMany(meshAssets);
            ModelPreviewWindow.Preview(meshAssets);
        }

        foreach (var asset in loadedAssets)
            await PreviewAsset(asset);
    }

    private UObject? TransformAssetForPreview(UObject? asset) => asset switch
    {
        UVirtualTextureBuilder vtb => vtb.Texture.Load<UVirtualTexture2D>(),
        UPaperSprite sprite => sprite.BakedSourceTexture?.Load<UTexture2D>(),
        UWorld world => world.PersistentLevel.Load<ULevel>(),
        _ => asset
    };

    public async Task PreviewAsset(UObject? asset)
    {
        var name = asset?.Name!;
        asset = TransformAssetForPreview(asset);
        if (asset is null) return;

        switch (asset)
        {
            case UTexture texture:
                TexturePreviewWindow.Preview(name, texture);
                break;
            case UMaterial:
            case UMaterialFunction:
                if (!asset.HasEditorData)
                {
                    _info.Message("Material Preview",
                        "Material node-tree data cannot be loaded because UEFN is not installed.",
                        closeTime: 5, severity: InfoBarSeverity.Error);
                    break;
                }
                MaterialPreviewWindow.Preview(asset);
                break;
            case UMaterialInstanceConstant instance:
                _info.Dialog($"Preview {instance.Name}", "What asset type would you like to preview?", buttons:
                [
                    new DialogButton
                    {
                        Text = "Material Properties",
                        Action = () => TaskService.Run(async () => await PreviewProperties(instance))
                    },
                    new DialogButton
                    {
                        Text = "Material Node Tree",
                        Action = () =>
                        {
                            UUnrealMaterial? parentMaterial = instance;
                            while (parentMaterial is UMaterialInstanceConstant mic)
                                parentMaterial = mic.Parent?.LoadOrDefault<UMaterialInterface>();
                            if (parentMaterial is not null)
                                MaterialPreviewWindow.Preview(parentMaterial);
                        }
                    }
                ]);
                break;
            case UStaticMesh:
            case USkeletalMesh:
            case ULevel:
                ModelPreviewWindow.Preview([asset]);
                break;
            case UAnimationAsset animation:
                PreviewAnimation(animation);
                break;
            case USoundWave soundWave:
                SoundPreviewWindow.Preview(soundWave);
                break;
            case USoundCue soundCue:
                SoundCuePreviewWindow.Preview(soundCue);
                break;
            default:
                await PreviewProperties(asset);
                break;
        }
    }

    private void PreviewAnimation(UAnimationAsset animation)
    {
        if (ModelPreviewWindow.TryApplyAnimation(animation))
            return;

        _info.Dialog($"Preview {animation.Name}", "Choose a skeletal mesh to preview this animation.", buttons:
        [
            new DialogButton
            {
                Text = "Use Default Mannequin",
                IsPrimary = true,
                Action = () => TaskService.Run(async () =>
                    await PreviewAnimationWithMeshAsync(animation, DefaultMannequinMeshPath))
            },
            new DialogButton
            {
                Text = "Select Mesh",
                Action = () => TaskService.RunDispatcher(async () =>
                {
                    if (await FilePickerWindow.OpenBrowserAsync("Select Skeletal Mesh") is not { Length: > 0 } paths)
                        return;

                    await PreviewAnimationWithMeshAsync(animation, _exporter.FixPath(paths[0]));
                })
            }
        ]);
    }

    private async Task PreviewAnimationWithMeshAsync(UAnimationAsset animation, string meshPath)
    {
        var mesh = await _ueParse.Provider.SafeLoadPackageObjectAsync<USkeletalMesh>(meshPath);
        mesh ??= await _ueParse.Provider.SafeLoadPackageObjectAsync(meshPath) as USkeletalMesh;
        if (mesh is null)
        {
            await TaskService.RunDispatcherAsync(() =>
                _info.Message("Model Viewer", "Could not load the selected skeletal mesh.",
                    severity: InfoBarSeverity.Warning));
            return;
        }

        await TaskService.RunDispatcherAsync(() => ModelPreviewWindow.Preview([mesh], animation));
    }

    private const string DefaultMannequinMeshPath =
        "FortniteGame/Content/Creative/Devices/Mannequin/Meshes/CP_Device_Mannequin";

    private List<string> GetSelectedFilePaths()
    {
        var paths = Context.UseFlatView
            ? Context.SelectedFlatViewItems.Select(x => x.Path).ToList()
            : Context.SelectedFileViewItems.Where(x => x.Type == ENodeType.File).Select(x => x.FilePath).ToList();

        var folders = Context.UseFlatView ? [] : Context.SelectedFileViewItems.Where(x => x.Type == ENodeType.Folder);
        foreach (var folder in folders)
        {
            if (folder.SourceNode is null) continue;
            paths.AddRange(Context.GetAllFileDescendants(folder.SourceNode, folder)
                .Where(x => x.Type == ENodeType.File)
                .Select(x => x.FilePath));
        }

        return paths;
    }

    [RelayCommand]
    public async Task Export()
    {
        var paths = GetSelectedFilePaths();
        if (paths.Count == 0)
        {
            _info.Message("Exporter", "Select a material to export.", InfoBarSeverity.Warning);
            return;
        }

        var materials = new List<string>();
        var unsupported = 0;
        foreach (var path in paths)
        {
            if (_ueParse.Provider.TryLoadObjectExports(path, out var exports) && exports.Any(x => x is UMaterialInterface))
                materials.Add(path);
            else
                unsupported++;
        }

        if (materials.Count == 0)
        {
            _info.Message("Export to Unity", "Not available for Unity yet. Only materials and shaders can be exported.", InfoBarSeverity.Warning);
            return;
        }

        if (unsupported > 0)
            _info.Message("Export to Unity", $"Skipped {unsupported} item(s): not available for Unity yet.", closeTime: 5);

        ConvertVM.AddToQueue(materials);
        _navigation.App.Open<ConvertView>();
        if (ConvertVM.ConvertCommand.CanExecute(null))
            await ConvertVM.ConvertCommand.ExecuteAsync(null);
    }
}
