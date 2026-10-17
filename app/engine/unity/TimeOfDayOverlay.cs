using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

[Overlay(typeof(SceneView), "Time of Day", true)]
public class TimeOfDayOverlay : IMGUIOverlay
{
    static Texture2D bar;
    TimeOfDayController controller;

    static Color SkyAt(float h)
    {
        var night = new Color(0.03f, 0.05f, 0.14f);
        var dawn = new Color(0.95f, 0.55f, 0.3f);
        var day = new Color(0.45f, 0.72f, 0.98f);
        var e = Mathf.Sin((h - 6f) / 24f * Mathf.PI * 2f);
        if (e < -0.15f) return night;
        if (e < 0.05f) return Color.Lerp(night, dawn, Mathf.InverseLerp(-0.15f, 0.05f, e));
        if (e < 0.4f) return Color.Lerp(dawn, day, Mathf.InverseLerp(0.05f, 0.4f, e));
        return day;
    }

    static Texture2D Bar()
    {
        if (bar != null) return bar;
        const int w = 192;
        bar = new Texture2D(w, 1, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (var i = 0; i < w; i++) bar.SetPixel(i, 0, SkyAt(i / (float)(w - 1) * 24f));
        bar.Apply();
        return bar;
    }

    static TimeOfDayController Find()
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindFirstObjectByType<TimeOfDayController>();
#else
        return Object.FindObjectOfType<TimeOfDayController>();
#endif
    }

    public override void OnGUI()
    {
        if (controller == null) controller = Find();
        if (controller == null)
        {
            GUILayout.Label("No Time of Day controller in the scene");
            if (GUILayout.Button("Add one"))
            {
                var go = new GameObject("Time of Day");
                Undo.RegisterCreatedObjectUndo(go, "Add Time of Day");
                controller = go.AddComponent<TimeOfDayController>();
                Selection.activeGameObject = go;
            }
            return;
        }

        var h = controller.hour;
        var hh = Mathf.Min(23, (int)h);
        var mm = Mathf.Min(59, (int)((h - hh) * 60f));
        GUILayout.Label(string.Format("{0:00}:{1:00}   {2}", hh, mm, controller.PhaseName()), EditorStyles.boldLabel);

        var rect = GUILayoutUtility.GetRect(280f, 22f);
        GUI.DrawTexture(rect, Bar(), ScaleMode.StretchToFill);
        EditorGUI.BeginChangeCheck();
        var v = GUI.HorizontalSlider(rect, h, 0f, 24f, GUIStyle.none, GUI.skin.horizontalSliderThumb);
        if (EditorGUI.EndChangeCheck()) Set(v);

        GUILayout.BeginHorizontal();
        if (controller.presets != null)
            foreach (var p in controller.presets)
                if (GUILayout.Button(p.name)) Set(p.hour);
        GUILayout.EndHorizontal();

        if (controller.slotBindings != null && controller.slotBindings.Count > 0 && controller.slotBindings.Exists(b => b.material == null))
            if (GUILayout.Button("Auto Fill From Scene")) { Undo.RecordObject(controller, "Auto Fill"); controller.AutoFill(); EditorUtility.SetDirty(controller); }
    }

    void Set(float hour)
    {
        Undo.RecordObject(controller, "Time of Day");
        controller.SetHour(hour);
        EditorUtility.SetDirty(controller);
        SceneView.RepaintAll();
    }
}
