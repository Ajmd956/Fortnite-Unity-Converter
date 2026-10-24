using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// One control for a whole converted effect. It sits on the prefab root, finds every PorterBakedParticleSystem under it and plays, pauses,
// stops and restarts them together, scales their speed, rate, size, tint, intensity and opacity from one place, switches single
// emitters on and off, and can copy shared settings (looping, prewarm, space, forces) to all of them.
//
// The master values multiply what each emitter was converted with. That starting value is remembered the first time the controller
// sees the emitter (Rebase takes the current values as the new starting point).
[ExecuteAlways]
[DisallowMultipleComponent]
public class PorterParticleController : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public PorterBakedParticleSystem player;
        public bool enabled = true;
        public bool captured;
        public float baseSpeed = 1f, baseRate = 1f, baseSize = 1f, baseIntensity = 1f, baseOpacity = 1f;
        public Color baseTint = Color.white;
    }

    [Serializable]
    public class Shared
    {
        public bool loop = true;
        public bool prewarm;
        public float startDelay;
        public bool worldSpace;
        public float lifetimeScale = 1f;
        public float lifetimeRandom;
        public Vector3 extraVelocity;
        public Vector3 extraAcceleration;
        public float noiseStrength;
        public float noiseFrequency = 1f;
    }

    public List<Entry> emitters = new List<Entry>();

    // master values (1 and white leave every emitter as converted)
    public float masterSpeed = 1f;
    public float masterRate = 1f;
    public float masterSize = 1f;
    public float masterIntensity = 1f;
    public float masterOpacity = 1f;
    public Color masterTint = Color.white;

    public Shared shared = new Shared();

    float lastSpeed = -1f, lastRate = -1f, lastSize = -1f, lastIntensity = -1f, lastOpacity = -1f;
    Color lastTint = new Color(-1f, -1f, -1f, -1f);
    [NonSerialized] List<bool> lastEnabled = new List<bool>();

    public int EmitterCount { get { return emitters.Count; } }

    void Reset()
    {
        Collect();
    }

    void OnEnable()
    {
        Collect();
        ApplyEnabled();
        Apply(true);
    }

    void OnValidate()
    {
        Collect();
        Apply(true);
    }

    // masters edited at run time or animated are picked up here (a few float compares, no allocation)
    void Update()
    {
        if (masterSpeed != lastSpeed || masterRate != lastRate || masterSize != lastSize || masterIntensity != lastIntensity
            || masterOpacity != lastOpacity || masterTint != lastTint) Apply(false);
        bool changed = lastEnabled.Count != emitters.Count;
        for (int i = 0; !changed && i < emitters.Count; i++) changed = lastEnabled[i] != emitters[i].enabled;
        if (changed) ApplyEnabled();
    }

    // refresh the list: new players under this object are added, players that are gone are dropped, the order and flags of the rest stay
    public void Collect()
    {
        var found = GetComponentsInChildren<PorterBakedParticleSystem>(true);
        for (int i = emitters.Count - 1; i >= 0; i--)
            if (emitters[i] == null || emitters[i].player == null || Array.IndexOf(found, emitters[i].player) < 0) emitters.RemoveAt(i);
        for (int i = 0; i < found.Length; i++)
        {
            bool known = false;
            for (int k = 0; k < emitters.Count && !known; k++) known = emitters[k].player == found[i];
            if (!known) emitters.Add(new Entry { player = found[i], enabled = found[i].gameObject.activeSelf });
        }
    }

    void Capture(Entry e)
    {
        var p = e.player;
        e.baseSpeed = p.speed;
        e.baseRate = p.rateMultiplier;
        e.baseSize = p.sizeScale;
        e.baseIntensity = p.colorIntensity;
        e.baseOpacity = p.opacity;
        e.baseTint = p.colorTint;
        e.captured = true;
    }

    // writes starting value x master into every emitter
    public void Apply(bool force)
    {
        for (int i = 0; i < emitters.Count; i++)
        {
            var e = emitters[i];
            if (e == null || e.player == null) continue;
            if (!e.captured) Capture(e);
            var p = e.player;
            p.speed = e.baseSpeed * masterSpeed;
            p.rateMultiplier = e.baseRate * masterRate;
            p.sizeScale = e.baseSize * masterSize;
            p.colorIntensity = e.baseIntensity * masterIntensity;
            p.opacity = e.baseOpacity * masterOpacity;
            p.colorTint = e.baseTint * masterTint;
        }
        lastSpeed = masterSpeed;
        lastRate = masterRate;
        lastSize = masterSize;
        lastIntensity = masterIntensity;
        lastOpacity = masterOpacity;
        lastTint = masterTint;
    }

    // takes what the emitters have now as their starting values and sets every master back to 1
    public void Rebase()
    {
        masterSpeed = masterRate = masterSize = masterIntensity = masterOpacity = 1f;
        masterTint = Color.white;
        for (int i = 0; i < emitters.Count; i++)
            if (emitters[i] != null && emitters[i].player != null) Capture(emitters[i]);
        Apply(true);
    }

    // puts every master back to 1 (the emitters return to what they were converted with)
    public void ResetMasters()
    {
        masterSpeed = masterRate = masterSize = masterIntensity = masterOpacity = 1f;
        masterTint = Color.white;
        Apply(true);
    }

    public void ApplyEnabled()
    {
        lastEnabled.Clear();
        for (int i = 0; i < emitters.Count; i++)
        {
            var e = emitters[i];
            if (e != null && e.player != null && e.player.gameObject.activeSelf != e.enabled) e.player.gameObject.SetActive(e.enabled);
            lastEnabled.Add(e != null && e.enabled);
        }
    }

    public void SetEmitterEnabled(int index, bool on)
    {
        if (index < 0 || index >= emitters.Count) return;
        emitters[index].enabled = on;
        ApplyEnabled();
    }

    public void SetEmitterEnabled(string emitterName, bool on)
    {
        for (int i = 0; i < emitters.Count; i++)
            if (emitters[i].player != null && emitters[i].player.name.IndexOf(emitterName, StringComparison.OrdinalIgnoreCase) >= 0)
                emitters[i].enabled = on;
        ApplyEnabled();
    }

    // ---- playback ----
    bool Live(Entry e)
    {
        return e != null && e.enabled && e.player != null && e.player.gameObject.activeInHierarchy;
    }

    public void Play()
    {
        for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) emitters[i].player.Play();
    }

    public void Pause()
    {
        for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) emitters[i].player.Pause();
    }

    public void Resume()
    {
        for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) emitters[i].player.Resume();
    }

    public void Stop()
    {
        for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) emitters[i].player.Stop();
    }

    public void Restart()
    {
        for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) emitters[i].player.Restart();
    }

    // the longest cycle of the enabled emitters
    public float Duration
    {
        get
        {
            float d = 0f;
            for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) d = Mathf.Max(d, emitters[i].player.Duration);
            return d;
        }
    }

    public bool AnyPlaying
    {
        get
        {
            for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i]) && emitters[i].player.IsPlaying && !emitters[i].player.IsPaused) return true;
            return false;
        }
    }

    public bool AnyPaused
    {
        get
        {
            for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i]) && emitters[i].player.IsPaused) return true;
            return false;
        }
    }

    public int TotalParticles
    {
        get
        {
            int n = 0;
            for (int i = 0; i < emitters.Count; i++) if (Live(emitters[i])) n += emitters[i].player.CurrentParticles;
            return n;
        }
    }

    // ---- shared settings ----
    // takes the shared values from the first enabled emitter
    public void CopySharedFrom(PorterBakedParticleSystem p)
    {
        if (p == null) return;
        shared.loop = p.loop;
        shared.prewarm = p.prewarm;
        shared.startDelay = p.startDelay;
        shared.worldSpace = p.worldSpace;
        shared.lifetimeScale = p.lifetimeScale;
        shared.lifetimeRandom = p.lifetimeRandom;
        shared.extraVelocity = p.extraVelocity;
        shared.extraAcceleration = p.extraAcceleration;
        shared.noiseStrength = p.noiseStrength;
        shared.noiseFrequency = p.noiseFrequency;
    }

    // writes the shared values into every emitter
    public void PushSharedSettings()
    {
        for (int i = 0; i < emitters.Count; i++)
        {
            var e = emitters[i];
            if (e == null || e.player == null) continue;
            var p = e.player;
            p.loop = shared.loop;
            p.prewarm = shared.prewarm;
            p.startDelay = shared.startDelay;
            p.worldSpace = shared.worldSpace;
            p.lifetimeScale = shared.lifetimeScale;
            p.lifetimeRandom = shared.lifetimeRandom;
            p.extraVelocity = shared.extraVelocity;
            p.extraAcceleration = shared.extraAcceleration;
            p.noiseStrength = shared.noiseStrength;
            p.noiseFrequency = shared.noiseFrequency;
            if (shared.extraVelocity != Vector3.zero) p.useVelocity = true;
            if (shared.extraAcceleration != Vector3.zero) p.useForce = true;
            if (shared.noiseStrength != 0f) p.useNoise = true;
        }
    }

    // single-value setters for scripts, animation events and UnityEvents
    public void SetSpeed(float v) { masterSpeed = Mathf.Max(0f, v); Apply(false); }
    public void SetRate(float v) { masterRate = Mathf.Max(0f, v); Apply(false); }
    public void SetSize(float v) { masterSize = Mathf.Max(0f, v); Apply(false); }
    public void SetIntensity(float v) { masterIntensity = Mathf.Max(0f, v); Apply(false); }
    public void SetOpacity(float v) { masterOpacity = Mathf.Max(0f, v); Apply(false); }
    public void SetTint(Color c) { masterTint = c; Apply(false); }
}

#if UNITY_EDITOR
[CustomEditor(typeof(PorterParticleController))]
public class PorterParticleControllerInspector : Editor
{
    static bool fPlayback = true, fMaster = true, fShared, fList = true;

    public override bool RequiresConstantRepaint()
    {
        var t = target as PorterParticleController;
        return t != null && t.AnyPlaying;
    }

    static bool Module(bool open, string title)
    {
        return EditorGUILayout.Foldout(open, title, true, EditorStyles.foldoutHeader);
    }

    SerializedProperty P(string n)
    {
        return serializedObject.FindProperty(n);
    }

    void Master(string prop, string label, string tip)
    {
        var p = P(prop);
        EditorGUILayout.PropertyField(p, new GUIContent(label, tip));
        if (p.propertyType == SerializedPropertyType.Float && p.floatValue < 0f) p.floatValue = 0f;
    }

    public override void OnInspectorGUI()
    {
        var t = (PorterParticleController)target;
        serializedObject.Update();

        fPlayback = Module(fPlayback, "Playback");
        if (fPlayback)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.BeginHorizontal();
            string playLabel = !t.AnyPlaying && !t.AnyPaused ? "Play" : (t.AnyPaused ? "Resume" : "Pause");
            if (GUILayout.Button(playLabel))
            {
                if (!t.AnyPlaying && !t.AnyPaused) t.Play();
                else if (t.AnyPaused) t.Resume();
                else t.Pause();
            }
            if (GUILayout.Button("Restart")) t.Restart();
            if (GUILayout.Button("Stop")) t.Stop();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Emitters", t.EmitterCount + " (" + t.TotalParticles + " particles)");
            EditorGUILayout.LabelField("Longest cycle", t.Duration.ToString("0.00") + " s");
            EditorGUI.indentLevel--;
        }

        fMaster = Module(fMaster, "Master");
        if (fMaster)
        {
            EditorGUI.indentLevel++;
            Master("masterSpeed", "Speed", "Playback speed of every emitter, times what it was converted with.");
            Master("masterRate", "Rate", "Emission rate of every emitter.");
            Master("masterSize", "Size", "Sprite size, mesh scale, ribbon width and decal size.");
            Master("masterIntensity", "Intensity", "Multiplies the colour's brightness.");
            Master("masterOpacity", "Opacity", "Multiplies the alpha.");
            EditorGUILayout.PropertyField(P("masterTint"), new GUIContent("Tint", "Multiplies every particle's colour."));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset Masters")) { serializedObject.ApplyModifiedProperties(); Undo.RecordObjects(Players(t), "Reset particle masters"); t.ResetMasters(); serializedObject.Update(); }
            if (GUILayout.Button("Use Current As Base")) { serializedObject.ApplyModifiedProperties(); t.Rebase(); serializedObject.Update(); }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        fShared = Module(fShared, "Shared Settings");
        if (fShared)
        {
            EditorGUI.indentLevel++;
            var s = P("shared");
            EditorGUILayout.PropertyField(s.FindPropertyRelative("loop"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("prewarm"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("startDelay"), new GUIContent("Start Delay"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("worldSpace"), new GUIContent("World Space"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("lifetimeScale"), new GUIContent("Lifetime Scale"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("lifetimeRandom"), new GUIContent("Lifetime Random"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("extraVelocity"), new GUIContent("Extra Velocity"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("extraAcceleration"), new GUIContent("Extra Acceleration"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("noiseStrength"), new GUIContent("Noise Strength"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("noiseFrequency"), new GUIContent("Noise Frequency"));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy From First Emitter"))
            {
                serializedObject.ApplyModifiedProperties();
                for (int i = 0; i < t.emitters.Count; i++)
                    if (t.emitters[i].player != null) { t.CopySharedFrom(t.emitters[i].player); break; }
                serializedObject.Update();
            }
            if (GUILayout.Button("Push To All Emitters"))
            {
                serializedObject.ApplyModifiedProperties();
                Undo.RecordObjects(Players(t), "Push shared particle settings");
                t.PushSharedSettings();
                MarkDirty(t);
                serializedObject.Update();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        fList = Module(fList, "Emitters");
        if (fList)
        {
            EditorGUI.indentLevel++;
            var list = P("emitters");
            for (int i = 0; i < list.arraySize; i++)
            {
                var e = list.GetArrayElementAtIndex(i);
                var pl = e.FindPropertyRelative("player").objectReferenceValue as PorterBakedParticleSystem;
                if (pl == null) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(e.FindPropertyRelative("enabled"), GUIContent.none, GUILayout.Width(18f));
                EditorGUILayout.LabelField(pl.name, GUILayout.MinWidth(80f));
                GUI.enabled = false;
                EditorGUILayout.TextField(pl.kind, GUILayout.Width(60f));
                GUI.enabled = true;
                if (GUILayout.Button("Select", GUILayout.Width(52f))) Selection.activeObject = pl.gameObject;
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Refresh List"))
            {
                serializedObject.ApplyModifiedProperties();
                t.Collect();
                EditorUtility.SetDirty(t);
                serializedObject.Update();
            }
            EditorGUI.indentLevel--;
        }

        serializedObject.ApplyModifiedProperties();
    }

    static UnityEngine.Object[] Players(PorterParticleController t)
    {
        var list = new List<UnityEngine.Object>();
        for (int i = 0; i < t.emitters.Count; i++) if (t.emitters[i].player != null) list.Add(t.emitters[i].player);
        return list.ToArray();
    }

    static void MarkDirty(PorterParticleController t)
    {
        for (int i = 0; i < t.emitters.Count; i++) if (t.emitters[i].player != null) EditorUtility.SetDirty(t.emitters[i].player);
    }
}
#endif
