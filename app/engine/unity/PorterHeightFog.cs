using UnityEngine;

/// <summary>
/// Unreal's exponential height fog for the built-in pipeline. It is an image effect, so it has to sit on a camera: the converted sky rig adds it
/// to the main camera by itself (Porter Sky Lighting, Auto Attach Fog). It runs in the Game view and in the Scene view (Scene view toolbar,
/// Effects menu, Image Effects must be on). If you would rather use Unity's own fog, turn on Use Built In Fog.
/// </summary>
[ExecuteAlways]
[ImageEffectAllowedInSceneView]
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public class PorterHeightFog : MonoBehaviour
{
    public TimeOfDayController controller;
    public PorterSkyRig skyRig;
    public Shader shader;
    [Tooltip("Unreal's Fog Density. The day data does not hold it; 0.012 is the value read from the Unreal fog actor.")]
    public float density = 0.012f;
    [Tooltip("Used only when the day data has no Fog Height Falloff track. The Unreal fog actor has 0.05.")]
    public float fallbackHeightFalloff = 0.05f;
    public float heightFalloffScale = 1f;
    [Tooltip("World height (Unity Y, metres) of the fog's base, like the fog actor's Z in Unreal.")]
    public float fogHeight = 0f;
    [Tooltip("Metres. Unreal Start Distance is 10000 cm = 100 m.")]
    public float startDistance = 100f;
    [Tooltip("Unreal Fog Max Opacity is 0.3.")]
    [Range(0f, 1f)] public float maxOpacity = 0.3f;
    [Tooltip("Metres. Unreal Directional Inscattering Start Distance is 10000 cm = 100 m.")]
    public float directionalStartDistance = 100f;
    public float brightness = 1f;
    public float directionalStrength = 1f;
    [Tooltip("Pixels farther than this (metres) are the sky and stay unfogged.")]
    public float skyStartDistance = 30000f;
    public Color fallbackFogColor = new Color(0.5f, 0.6f, 0.75f, 1f);
    [Tooltip("Use Unity's own exponential fog (works everywhere, but has no height falloff and no glow around the sun) instead of the image effect.")]
    public bool useBuiltInFog = false;

    public static bool BuiltInActive { get; private set; }

    Camera cam;
    Material material;

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        Camera.onPreCull += NeedDepth;
        if (controller == null) controller = FindController();
        if (skyRig == null) skyRig = FindRig();
        if (shader == null) shader = Shader.Find("Hidden/PorterHeightFog");
    }

    void OnDisable()
    {
        Camera.onPreCull -= NeedDepth;
        BuiltInActive = false;
        if (material != null) DestroyImmediate(material);
        material = null;
    }

    // Every camera that renders the scene needs a depth texture for the effect, the Scene view camera included.
    static void NeedDepth(Camera c)
    {
        if (c == null || (c.cameraType != CameraType.Game && c.cameraType != CameraType.SceneView)) return;
        c.depthTextureMode |= DepthTextureMode.Depth;
    }

    static TimeOfDayController FindController()
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<TimeOfDayController>();
#else
        return FindObjectOfType<TimeOfDayController>();
#endif
    }

    static PorterSkyRig FindRig()
    {
#if UNITY_2023_1_OR_NEWER
        return FindFirstObjectByType<PorterSkyRig>();
#else
        return FindObjectOfType<PorterSkyRig>();
#endif
    }

    float dataDensity, dataMaxOpacity, dataStart, dataDirStart, dataCutoff, dataDensity2, dataFalloff2, dataOffset2;
    bool hasDensity, hasMaxOpacity, hasStart, hasDirStart, hasCutoff, hasSecond;

    void ReadFogData()
    {
        hasDensity = hasMaxOpacity = hasStart = hasDirStart = hasCutoff = hasSecond = false;
        if (controller == null) return;
        const string g = "ExponentialHeightFog";
        hasDensity = controller.Scalar(g, "FogDensity", out dataDensity);
        hasMaxOpacity = controller.Scalar(g, "FogMaxOpacity", out dataMaxOpacity);
        hasStart = controller.Scalar(g, "StartDistance", out dataStart);
        hasDirStart = controller.Scalar(g, "DirectionalInscatteringStartDistance", out dataDirStart);
        hasCutoff = controller.Scalar(g, "FogCutoffDistance", out dataCutoff);
        if (controller.Scalar(g, "SecondFogData.FogDensity", out dataDensity2))
        {
            hasSecond = true;
            if (!controller.Scalar(g, "SecondFogData.FogHeightFalloff", out dataFalloff2)) dataFalloff2 = 0f;
            if (!controller.Scalar(g, "SecondFogData.FogHeightOffset", out dataOffset2)) dataOffset2 = 0f;
        }
    }

    void Evaluate(out float falloff, out Vector3 fogColor, out Vector3 dirColor, out float dirExponent, out Vector3 toBody)
    {
        falloff = fallbackHeightFalloff;
        fogColor = new Vector3(fallbackFogColor.r, fallbackFogColor.g, fallbackFogColor.b);
        dirColor = Vector3.zero;
        dirExponent = 4f;
        toBody = Vector3.up;
        if (controller == null) return;
        if (controller.Scalar("ExponentialHeightFog", "FogHeightFalloff", out var f)) falloff = f;
        if (controller.Scalar("ExponentialHeightFog", "DirectionalInscatteringExponent", out f)) dirExponent = f;
        if (controller.Rgb("ExponentialHeightFog", "FogInscatteringLuminance", out var c)) fogColor = c;
        if (controller.Rgb("ExponentialHeightFog", "DirectionalInscatteringLuminance", out c)) dirColor = c * directionalStrength;
        if (controller.Rgb("ExponentialHeightFog", "SkyAtmosphereAmbientContributionColorScale", out var ambientScale))
        {
            var ambient = controller.SkyAmbientRadiance();
            fogColor += new Vector3(ambient.x * ambientScale.x, ambient.y * ambientScale.y, ambient.z * ambientScale.z);
        }
        var toSun = controller.SunDirection();
        toBody = toSun.y >= 0f ? toSun : -toSun;
    }

    double lastFind;

    void Update()
    {
        if ((controller == null || skyRig == null) && Time.realtimeSinceStartupAsDouble - lastFind > 1.0)
        {
            lastFind = Time.realtimeSinceStartupAsDouble;
            if (controller == null) controller = FindController();
            if (skyRig == null) skyRig = FindRig();
        }
        BuiltInActive = useBuiltInFog && isActiveAndEnabled;
        if (!BuiltInActive) return;
        Evaluate(out var falloff, out var fogColor, out _, out _, out _);
        var perMetre = Mathf.Max(falloff * heightFalloffScale / 10f, 0.000001f);
        var atCamera = density / 10f * Mathf.Pow(2f, -perMetre * (cam.transform.position.y - fogHeight));
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = Mathf.Clamp(atCamera, 0f, 1f);
        var shown = fogColor * brightness;
        RenderSettings.fogColor = new Color(Mathf.Clamp01(shown.x), Mathf.Clamp01(shown.y), Mathf.Clamp01(shown.z), 1f);
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        var c = Camera.current != null ? Camera.current : cam;
        if (useBuiltInFog || shader == null || !shader.isSupported || c == null || c.orthographic) { Graphics.Blit(source, destination); return; }
        if (material == null) material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

        var near = c.nearClipPlane;
        var far = c.farClipPlane;
        // the sky rig raises the far clip while the camera renders, so the depth buffer was made with that far plane
        if (skyRig != null && skyRig.raiseFarClip && skyRig.requiredFarClip > far) far = skyRig.requiredFarClip;

        var t = c.transform;
        var half = c.fieldOfView * 0.5f * Mathf.Deg2Rad;
        var toRight = t.right * (near * Mathf.Tan(half) * c.aspect);
        var toTop = t.up * (near * Mathf.Tan(half));
        var topLeft = t.forward * near - toRight + toTop;
        var scale = topLeft.magnitude * far / near;
        topLeft.Normalize();
        topLeft *= scale;
        var topRight = t.forward * near + toRight + toTop;
        topRight.Normalize();
        topRight *= scale;
        var bottomRight = t.forward * near + toRight - toTop;
        bottomRight.Normalize();
        bottomRight *= scale;
        var bottomLeft = t.forward * near - toRight - toTop;
        bottomLeft.Normalize();
        bottomLeft *= scale;
        var corners = Matrix4x4.identity;
        corners.SetRow(0, topLeft);
        corners.SetRow(1, topRight);
        corners.SetRow(2, bottomRight);
        corners.SetRow(3, bottomLeft);

        Evaluate(out var falloff, out var fogColor, out var dirColor, out var dirExponent, out var toBody);
        ReadFogData();
        var useDensity = hasDensity ? dataDensity : density;
        var useMaxOpacity = hasMaxOpacity ? dataMaxOpacity : maxOpacity;
        var useStart = hasStart ? dataStart / 100f : startDistance;
        var useDirStart = hasDirStart ? dataDirStart / 100f : directionalStartDistance;
        var useCutoff = hasCutoff && dataCutoff > 0f ? dataCutoff / 100f : skyStartDistance;
        var densityPerMetre = useDensity / 10f;
        var density2PerMetre = hasSecond ? dataDensity2 / 10f : 0f;
        var falloff2PerMetre = hasSecond ? Mathf.Max(dataFalloff2 * heightFalloffScale / 10f, 0.000001f) : 0.000001f;
        var falloffPerMetre = Mathf.Max(falloff * heightFalloffScale / 10f, 0.000001f);
        material.SetMatrix("_FogCorners", corners);
        material.SetVector("_FogCamPos", t.position);
        material.SetVector("_FogColor", new Vector4(fogColor.x, fogColor.y, fogColor.z, 1f) * brightness);
        material.SetVector("_FogDirColor", new Vector4(dirColor.x, dirColor.y, dirColor.z, 1f) * brightness);
        material.SetVector("_FogSunDir", new Vector4(toBody.x, toBody.y, toBody.z, 0f));
        material.SetVector("_FogParams", new Vector4(densityPerMetre, falloffPerMetre, fogHeight, useStart));
        material.SetVector("_FogParams2", new Vector4(dirExponent, useMaxOpacity, useCutoff, useDirStart));
        material.SetVector("_FogParams3", new Vector4(density2PerMetre, falloff2PerMetre, fogHeight + (hasSecond ? dataOffset2 / 100f : 0f), 0f));
        CustomBlit(source, destination, material);
    }

    // Vertex z holds the frustum corner index: 0 top left, 1 top right, 2 bottom right, 3 bottom left (the quad is drawn bottom left first).
    static void CustomBlit(RenderTexture source, RenderTexture dest, Material m)
    {
        RenderTexture.active = dest;
        m.SetTexture("_MainTex", source);
        GL.PushMatrix();
        GL.LoadOrtho();
        m.SetPass(0);
        GL.Begin(GL.QUADS);
        GL.MultiTexCoord2(0, 0f, 0f);
        GL.Vertex3(0f, 0f, 3f);
        GL.MultiTexCoord2(0, 1f, 0f);
        GL.Vertex3(1f, 0f, 2f);
        GL.MultiTexCoord2(0, 1f, 1f);
        GL.Vertex3(1f, 1f, 1f);
        GL.MultiTexCoord2(0, 0f, 1f);
        GL.Vertex3(0f, 1f, 0f);
        GL.End();
        GL.PopMatrix();
    }
}
