using UnityEngine;
using UnityEngine.Rendering;

[ExecuteAlways]
[DisallowMultipleComponent]
public class PorterSkyLighting : MonoBehaviour
{
    public TimeOfDayController controller;
    public PorterSkyRig skyRig;
    public LayerMask captureMask = ~0;
    [Range(8, 64)] public int resolution = 24;
    public float captureInterval = 0.25f;
    public Transform captureOrigin;
    public bool driveAmbient = true;
    public bool flipAmbientDirection = false;
    [Range(0f, 4f)] public float ambientIntensity = 1f;
    [Tooltip("How much of the sky's colour tints things lit by the ambient light (characters, props). 1 is the raw sky colour, 0 is grey with the same brightness. Lower it if skin looks purple or blue.")]
    [Range(0f, 1f)] public float ambientSaturation = 0.4f;
    [Range(0f, 1f)] public float groundBounce = 0.2f;
    public Color groundTint = Color.white;
    [Tooltip("Adds the height fog image effect to the main camera if no camera has it yet.")]
    public bool autoAttachFog = true;
    public bool driveFog = true;
    [Range(0f, 1f)] public float fogBlend = 1f;
    public bool driveSunShadows = true;
    [Range(0f, 1f)] public float shadowStrength = 0.9f;

    static readonly Vector3[] Forward = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
    static readonly Vector3[] Up = { Vector3.up, Vector3.up, Vector3.back, Vector3.forward, Vector3.up, Vector3.up };

    Camera cam;
    RenderTexture rt;
    Texture2D tex;
    double lastCapture;
    float lastHour = -1f;
    public Color skyAverage { get; private set; }
    public Color horizonColor { get; private set; }

    void OnEnable()
    {
        if (controller == null) controller = GetComponent<TimeOfDayController>();
        if (skyRig == null) skyRig = GetComponent<PorterSkyRig>();
        lastCapture = -999;
        EnsureFog();
    }

    double lastFogCheck;

    void EnsureFog()
    {
        lastFogCheck = Time.realtimeSinceStartupAsDouble;
        if (!autoAttachFog) return;
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<PorterHeightFog>() != null) return;
#else
        if (FindObjectOfType<PorterHeightFog>() != null) return;
#endif
        var target = Camera.main;
        if (target == null)
        {
            foreach (var c in Camera.allCameras)
                if (c != null && c.cameraType == CameraType.Game) { target = c; break; }
        }
        if (target == null) return;
        var fog = target.gameObject.AddComponent<PorterHeightFog>();
        fog.controller = controller;
        fog.skyRig = skyRig;
        Debug.Log("Porter: added the height fog to " + target.name + ". Fog Density and the other settings are on that component.", target);
    }

    bool drivingAmbient;

    void OnDisable()
    {
        ReleaseAmbient();
        Release();
    }

    void ReleaseAmbient()
    {
        if (!drivingAmbient) return;
        drivingAmbient = false;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;
        DynamicGI.UpdateEnvironment();
    }

    void Release()
    {
        if (cam != null) DestroyImmediate(cam.gameObject);
        if (rt != null) { rt.Release(); DestroyImmediate(rt); }
        if (tex != null) DestroyImmediate(tex);
        cam = null;
        rt = null;
        tex = null;
    }

    void Update()
    {
        if (!driveAmbient) ReleaseAmbient();
        if (autoAttachFog && Time.realtimeSinceStartupAsDouble - lastFogCheck > 2.0) EnsureFog();
        var h = controller != null ? controller.hour : 0f;
        var due = Time.realtimeSinceStartupAsDouble - lastCapture >= captureInterval;
        if (!due && Mathf.Approximately(h, lastHour)) return;
        if (!due && Time.realtimeSinceStartupAsDouble - lastCapture < 0.05) return;
        lastHour = h;
        CaptureNow();
    }

    [ContextMenu("Capture Now")]
    public void CaptureNow()
    {
        lastCapture = Time.realtimeSinceStartupAsDouble;
        Prepare();
        var origin = captureOrigin != null ? captureOrigin.position : transform.position;
        cam.transform.position = origin;
        cam.cullingMask = captureMask;
        cam.farClipPlane = skyRig != null && skyRig.requiredFarClip > 0f ? skyRig.requiredFarClip : 100000f;

        var n = resolution * resolution;
        var faces = new Color[6][];
        for (var f = 0; f < 6; f++)
        {
            cam.transform.rotation = Quaternion.LookRotation(Forward[f], Up[f]);
            cam.targetTexture = rt;
            cam.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = previous;
            faces[f] = tex.GetPixels();
        }
        cam.targetTexture = null;

        var sumSky = Vector3.zero;
        var weightSky = 0f;
        var sumHorizon = Vector3.zero;
        var weightHorizon = 0f;
        for (var f = 0; f < 6; f++)
        {
            var right = Vector3.Cross(Up[f], Forward[f]).normalized;
            for (var y = 0; y < resolution; y++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    var u = ((x + 0.5f) / resolution) * 2f - 1f;
                    var v = ((y + 0.5f) / resolution) * 2f - 1f;
                    var dir = (Forward[f] + right * u + Up[f] * v).normalized;
                    var c = faces[f][y * resolution + x];
                    var solid = 1f / Mathf.Pow(1f + u * u + v * v, 1.5f);
                    if (dir.y > 0f)
                    {
                        var w = solid * dir.y;
                        sumSky += new Vector3(c.r, c.g, c.b) * w;
                        weightSky += w;
                    }
                    if (dir.y > -0.05f && dir.y < 0.12f)
                    {
                        sumHorizon += new Vector3(c.r, c.g, c.b) * solid;
                        weightHorizon += solid;
                    }
                }
            }
        }
        var avg = weightSky > 0f ? sumSky / weightSky : Vector3.zero;
        var hor = weightHorizon > 0f ? sumHorizon / weightHorizon : avg;
        skyAverage = new Color(avg.x, avg.y, avg.z, 1f);
        horizonColor = new Color(hor.x, hor.y, hor.z, 1f);
        var ground = new Color(avg.x * groundTint.r, avg.y * groundTint.g, avg.z * groundTint.b, 1f) * groundBounce;

        if (driveAmbient)
        {
            var sh = new SphericalHarmonicsL2();
            sh.Clear();
            var texel = 2f / resolution;
            for (var f = 0; f < 6; f++)
            {
                var right = Vector3.Cross(Up[f], Forward[f]).normalized;
                for (var y = 0; y < resolution; y++)
                {
                    for (var x = 0; x < resolution; x++)
                    {
                        var u = ((x + 0.5f) / resolution) * 2f - 1f;
                        var v = ((y + 0.5f) / resolution) * 2f - 1f;
                        var dir = (Forward[f] + right * u + Up[f] * v).normalized;
                        var c = dir.y >= 0f ? faces[f][y * resolution + x] : ground;
                        var luma = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                        c = new Color(Mathf.Lerp(luma, c.r, ambientSaturation), Mathf.Lerp(luma, c.g, ambientSaturation), Mathf.Lerp(luma, c.b, ambientSaturation), 1f);
                        var solid = texel * texel / Mathf.Pow(1f + u * u + v * v, 1.5f);
                        sh.AddDirectionalLight(flipAmbientDirection ? -dir : dir, new Color(c.r, c.g, c.b, 1f), solid / Mathf.PI);
                    }
                }
            }
            drivingAmbient = true;
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = sh;
            RenderSettings.ambientIntensity = ambientIntensity;
        }

        if (driveFog && RenderSettings.fog && !PorterHeightFog.BuiltInActive)
            RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor, horizonColor, fogBlend);

        if (driveSunShadows && controller != null && controller.sun != null)
        {
            controller.sun.shadows = controller.sun.shadows == LightShadows.None ? LightShadows.Soft : controller.sun.shadows;
            controller.sun.shadowStrength = shadowStrength;
        }
    }

    void Prepare()
    {
        if (rt != null && rt.width != resolution) Release();
        if (rt == null)
        {
            rt = new RenderTexture(resolution, resolution, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Default);
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.Create();
        }
        if (tex == null)
        {
            tex = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
        }
        if (cam == null)
        {
            var go = new GameObject("PorterSkyLightingCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = 90f;
            cam.aspect = 1f;
            cam.nearClipPlane = 0.3f;
            cam.allowHDR = true;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
        }
    }
}
