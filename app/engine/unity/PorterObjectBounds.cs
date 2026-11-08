// x-flip: True
using System.Collections.Generic;
using UnityEngine;

// Feeds Unreal's ObjectRadius to converted shaders: the radius of the mesh's bounding sphere, in UE centimetres.
// The shader multiplies it by the object's scale, the same way Unreal does.
[ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(Renderer))]
public class PorterObjectBounds : MonoBehaviour
{
    static readonly int RadiusId = Shader.PropertyToID("_O_ObjectRadius");
    static readonly int BoundsMinId = Shader.PropertyToID("_O_BoundsMin");
    static readonly int BoundsMaxId = Shader.PropertyToID("_O_BoundsMax");
    static readonly Dictionary<Mesh, float> Cache = new Dictionary<Mesh, float>();

    Renderer rend;
    MaterialPropertyBlock block;

    void OnEnable()
    {
        Apply();
    }

    void OnValidate()
    {
        Apply();
    }

    public void Apply()
    {
        if (rend == null)
        {
            rend = GetComponent<Renderer>();
        }
        Mesh mesh = null;
        SkinnedMeshRenderer skinned = rend as SkinnedMeshRenderer;
        if (skinned != null)
        {
            mesh = skinned.sharedMesh;
        }
        else
        {
            MeshFilter filter = GetComponent<MeshFilter>();
            if (filter != null)
            {
                mesh = filter.sharedMesh;
            }
        }
        if (mesh == null)
        {
            return;
        }
        float radius;
        if (!Cache.TryGetValue(mesh, out radius))
        {
            radius = SphereRadius(mesh);
            Cache[mesh] = radius;
        }
        if (block == null)
        {
            block = new MaterialPropertyBlock();
        }
        rend.GetPropertyBlock(block);
        block.SetFloat(RadiusId, radius * 100.0f);
        Bounds mb = mesh.bounds;
        block.SetVector(BoundsMinId, new Vector4(-mb.max.x * 100.0f, mb.min.z * 100.0f, mb.min.y * 100.0f, 0.0f));
        block.SetVector(BoundsMaxId, new Vector4(-mb.min.x * 100.0f, mb.max.z * 100.0f, mb.max.y * 100.0f, 0.0f));
        rend.SetPropertyBlock(block);
    }

    static float SphereRadius(Mesh mesh)
    {
        Bounds b = mesh.bounds;
        if (!mesh.isReadable)
        {
            return b.extents.magnitude;
        }
        Vector3 c = b.center;
        float best = 0.0f;
        Vector3[] v = mesh.vertices;
        for (int i = 0; i < v.Length; i++)
        {
            best = Mathf.Max(best, (v[i] - c).sqrMagnitude);
        }
        return Mathf.Sqrt(best);
    }
}
