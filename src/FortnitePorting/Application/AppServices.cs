using System;
using System.Linq;
using System.Reflection;
using FortnitePorting.Framework;
using FortnitePorting.Services;
using FortnitePorting.Providers;
using FortnitePorting.ViewModels;
using FortnitePorting.WindowModels;
using Microsoft.Extensions.DependencyInjection;

namespace FortnitePorting.Application;

public static class AppServices
{
    public static ServiceProvider Services;

    public static void Initialize()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddCommonServices();
        serviceCollection.AddViewModels();

        Services = serviceCollection.BuildServiceProvider();
    }
    
    // Services
    public static AppService App => Services.GetRequiredService<AppService>();
    public static SettingsService AppSettings => Services.GetRequiredService<SettingsService>();
    public static DependencyService Dependencies => Services.GetRequiredService<DependencyService>();
    public static InfoService Info => Services.GetRequiredService<InfoService>();
    public static APIService Api => Services.GetRequiredService<APIService>();
    public static NavigationService Navigation => Services.GetRequiredService<NavigationService>();
    public static CUE4ParseService UEParse => Services.GetRequiredService<CUE4ParseService>();
    public static AssetLoaderService AssetLoading => Services.GetRequiredService<AssetLoaderService>();
    public static FilesService Files => Services.GetRequiredService<FilesService>();
    public static BridgeService ConversionBridge => Services.GetRequiredService<BridgeService>();
    public static ExportService Exporter => Services.GetRequiredService<ExportService>();
    public static ExportAssetProvider ExportAssets => Services.GetRequiredService<ExportAssetProvider>();
    public static WindowManagerService WindowManager => Services.GetRequiredService<WindowManagerService>();
    public static AudioPlaybackService Audio => Services.GetRequiredService<AudioPlaybackService>();
   
    // ViewModels
    public static AppWindowModel AppWM => Services.GetRequiredService<AppWindowModel>();
    public static FilesViewModel FilesVM => Services.GetRequiredService<FilesViewModel>();
    public static ConvertViewModel ConvertVM => Services.GetRequiredService<ConvertViewModel>();
    
}

public static class AppServiceExtensions
{
    extension(ServiceCollection collection)
    {
        public void AddCommonServices()
        {
            var serviceTypes = Assembly.GetAssembly(typeof(IService))?
                .GetTypes()
                .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(IService))) ?? [];

            foreach (var serviceType in serviceTypes)
            {
                collection.AddSingleton(serviceType);
            }
        }
    
        public void AddViewModels()
        {
            var viewModelTypes = Assembly.GetAssembly(typeof(ViewModelBase))?
                .GetTypes()
                .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(ViewModelBase))) ?? [];

            foreach (var viewModelType in viewModelTypes)
            {
                if (viewModelType.GetCustomAttribute<TransientAttribute>() is not null)
                {
                    collection.AddTransient(viewModelType);
                }
                else
                {
                    collection.AddSingleton(viewModelType);
                }
            }
        }
    }
}

public class TransientAttribute : Attribute;