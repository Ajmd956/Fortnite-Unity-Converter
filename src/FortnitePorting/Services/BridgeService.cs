using System;
using System.IO;
using FortnitePorting.MaterialPorter;
using Serilog;

namespace FortnitePorting.Services;

/// <summary>
/// The local API the conversion engine (FortniteUnityPorter) reads material data from.
/// It runs on the game files this app already has loaded.
/// </summary>
public class BridgeService(CUE4ParseService ueParse) : IService
{
    public const int DefaultPort = 24320;

    private Bridge? _bridge;

    public bool IsRunning => _bridge is not null;
    public string Address => $"http://localhost:{DefaultPort}";

    public void Start()
    {
        Stop();
        if (ueParse.Provider is null) return;

        try
        {
            GameContext.DataDir = Path.Combine(ueParse.CacheFolder.FullName, "Api");
            Directory.CreateDirectory(GameContext.DataDir);

            var game = new GameContext
            {
                Provider = ueParse.Provider,
                BuildVersion = string.IsNullOrWhiteSpace(ueParse.ResolvedVersion) ? "unknown" : ueParse.ResolvedVersion
            };

            var bridge = new Bridge(game, new MaterialService(game));
            bridge.Log += message => Log.Information("[Bridge] {Message}", message);
            bridge.Start(DefaultPort);
            _bridge = bridge;
            Log.Information("Conversion API ready on port {Port}", DefaultPort);
        }
        catch (Exception e)
        {
            _bridge = null;
            Log.Error(e, "The conversion API could not start");
        }
    }

    public void Stop()
    {
        _bridge?.Dispose();
        _bridge = null;
    }
}
