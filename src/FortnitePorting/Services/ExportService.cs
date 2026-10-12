using System.Linq;
using CUE4Parse.GameTypes.FN.Assets.Exports.DataAssets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using FortnitePorting.CUE4Parse.Models.Unreal.VirtualTexture;
using FortnitePorting.Exporting;

namespace FortnitePorting.Services;

public class ExportService(AssetLoaderService assetLoading) : IService
{
    public EExportType DetermineExportType(UObject asset)
    {
        var exportType = asset switch
        {
            USkeletalMesh => EExportType.Mesh,
            UStaticMesh => EExportType.Mesh,
            USkeleton => EExportType.Mesh,
            UBlueprintGeneratedClass => EExportType.Mesh,
            UWorld => EExportType.World,
            UTexture => EExportType.Texture,
            UVirtualTextureBuilder => EExportType.Texture,
            UBuildingTextureData => EExportType.Texture,
            USoundWave => EExportType.Sound,
            USoundCue => EExportType.Sound,
            UAnimMontage => EExportType.Animation,
            UAnimSequenceBase => EExportType.Animation,
            UFontFace => EExportType.Font,
            UPoseAsset => EExportType.PoseAsset,
            UMaterialInstance => EExportType.MaterialInstance,
            UMaterial => EExportType.Material,
            _ => EExportType.None
        };

        if (exportType is EExportType.None)
        {
            exportType = asset.ExportType switch
            {
                "CustomCharacterPart" => EExportType.CharacterPart,
                "FortBuildingInstructions" => EExportType.Mesh,
                _ => EExportType.None
            };
        }

        if (exportType is EExportType.None)
        {
            foreach (var loader in assetLoading.Categories.SelectMany(category => category.Loaders))
            {
                if (loader.ClassNames.Contains(asset.ExportType))
                {
                    exportType = loader.Type;
                    break;
                }
            }
        }

        return exportType;
    }

    public string FixPath(string path) => ExportSession.FixPath(path);
}
