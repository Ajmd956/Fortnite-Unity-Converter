using UnityEditor;
using UnityEngine;

public static class PorterObjectBoundsMenu
{
    [MenuItem("Porter/Add Object Bounds To Selection")]
    static void AddToSelection()
    {
        int added = 0;
        foreach (GameObject go in Selection.gameObjects)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                bool wants = false;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && (m.HasProperty("_O_ObjectRadius") || m.HasProperty("_O_BoundsMin")))
                    {
                        wants = true;
                        break;
                    }
                }
                if (wants && r.GetComponent<PorterObjectBounds>() == null)
                {
                    Undo.AddComponent<PorterObjectBounds>(r.gameObject);
                    added++;
                }
            }
        }
        Debug.Log("Porter: added object bounds to " + added + " renderer(s)");
    }
}
