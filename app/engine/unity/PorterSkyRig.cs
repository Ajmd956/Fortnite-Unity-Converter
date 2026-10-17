using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Makes a converted sky behave like Unreal's: the sky is at infinity. While each camera renders, the whole sky (dome, sun/moon, clouds) is
/// moved onto that camera, and the camera's far clip is raised just far enough to reach the whole sky. Both are undone as soon as that
/// camera has rendered, so no camera or object is left changed. Nothing needs to be set up; turn either option off to place the sky by hand.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class PorterSkyRig : MonoBehaviour
{
    [Tooltip("Centre the sky on every camera while it renders (the sky then never gets closer or farther).")]
    public bool followCamera = true;
    [Tooltip("Raise a camera's far clip to Required Far Clip while it renders, if it is shorter.")]
    public bool raiseFarClip = true;
    [Tooltip("Distance from this object to the far edge of the sky (dome, sun, clouds), with a margin. Recompute from the context menu after moving things.")]
    public float requiredFarClip = 0f;

    Vector3 savedPosition;
    bool moved;
    Camera farCamera;
    float savedFar;
    bool raised;

    void OnEnable()
    {
        Camera.onPreCull += Before;
        Camera.onPostRender += After;
        RenderPipelineManager.beginCameraRendering += BeforeSrp;
        RenderPipelineManager.endCameraRendering += AfterSrp;
    }

    void OnDisable()
    {
        Camera.onPreCull -= Before;
        Camera.onPostRender -= After;
        RenderPipelineManager.beginCameraRendering -= BeforeSrp;
        RenderPipelineManager.endCameraRendering -= AfterSrp;
        Restore();
    }

    void BeforeSrp(ScriptableRenderContext context, Camera cam) { Before(cam); }

    void AfterSrp(ScriptableRenderContext context, Camera cam) { After(cam); }

    void Before(Camera cam)
    {
        if (cam == null || cam.cameraType == CameraType.Preview || !isActiveAndEnabled) return;
        Restore();
        if (followCamera)
        {
            savedPosition = transform.position;
            moved = true;
            transform.position = cam.transform.position;
        }
        if (raiseFarClip && !cam.orthographic && requiredFarClip > cam.farClipPlane)
        {
            farCamera = cam;
            savedFar = cam.farClipPlane;
            raised = true;
            cam.farClipPlane = requiredFarClip;
        }
    }

    void After(Camera cam)
    {
        Restore();
    }

    void Restore()
    {
        if (moved)
        {
            transform.position = savedPosition;
            moved = false;
        }
        if (raised)
        {
            if (farCamera != null) farCamera.farClipPlane = savedFar;
            raised = false;
            farCamera = null;
        }
    }

    const float SunDistance = 50000f;

    /// <summary>Works out Required Far Clip from the renderers below this object, and from the distance the sun and moon are drawn at.</summary>
    [ContextMenu("Recompute Far Clip")]
    public void Recompute()
    {
        float far = 0f;
        Vector3 origin = transform.position;
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            Bounds b = r.bounds;
            far = Mathf.Max(far, Vector3.Distance(origin, b.center) + b.extents.magnitude);
        }
        if (far > 0f) far = Mathf.Max(far, SunDistance * 1.2f);
        requiredFarClip = far > 0f ? far * 1.1f : 0f;
    }
}
