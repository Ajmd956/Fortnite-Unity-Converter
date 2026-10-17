using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

[ExecuteAlways]
[DisallowMultipleComponent]
public class TimeOfDayController : MonoBehaviour
{
    [Serializable] public class Channel { public bool none; public float d; public float[] t; public float[] v; public int[] m; public float[] a; public float[] l; }
    [Serializable] public class Entry { public string prop; public string kind; public Channel[] ch; }
    [Serializable] public class Group { public string name; public Entry[] props; }
    [Serializable] public class Day { public string sequence; public Group[] slots; public Group[] collections; public Group[] bindings; }
    [Serializable] public class SlotBinding { public string slot; public Material material; }
    [Serializable] public class Preset { public string name; public float hour; }

    const string SunDirectionA = "_C_SunAndMoonModelDirectionalVector";
    const string SunDirectionB = "_C_SunlightDirectionalVector";
    const string SunDirProp = "_PorterSunDir";
    const string MoonDirProp = "_PorterMoonDir";
    const string AtmoRayleigh = "_AtmoRayleigh";
    const string AtmoMie = "_AtmoMie";
    const string AtmoMieAbs = "_AtmoMieAbs";
    const string AtmoOzone = "_AtmoOzone";
    const string AtmoShape = "_AtmoShape";
    const string AtmoTent = "_AtmoTent";
    const string AtmoFactor = "_AtmoFactor";
    const string AtmoLight = "_AtmoLight";
    const string AtmoMieScale = "_AtmoMieScale";

    [Range(0f, 24f)] public float hour = 12f;
    public float hoursPerSecond = 0f;
    public TextAsset dayData;
    public List<SlotBinding> slotBindings = new List<SlotBinding>();
    public bool applyCollections = true;

    [Header("Sun and Moon")]
    public Light sun;
    public bool driveSun = true;
    [Tooltip("Turns the whole sun and moon path around the vertical axis (degrees).")]
    public float sunYaw = 0f;
    [Tooltip("How high the sun gets at noon (degrees). The day data does not hold this, so it is an estimate.")]
    [Range(10f, 90f)] public float sunHeight = 75f;
    public float sunIntensity = 1f;
    [Tooltip("The same light is the moon at night, as in Unreal.")]
    public float moonIntensity = 0.1f;
    public bool driveSunVectors = true;
    [Tooltip("Drives the atmosphere (scattering colours, scale heights, anisotropy, luminance factor) and the sun colour from the day data's SkyAtmosphere and Sun tracks.")]
    public bool atmosphereFromData = true;
    [Tooltip("Size of the sun's halo (Mie scattering scale). Unreal's Fortnite sky has a much bigger halo than the default scattering gives, so this starts at 2. 1 is Unreal's default.")]
    [Range(0.5f, 8f)] public float mieScale = 2f;
    [Tooltip("The sun light is tinted by the atmosphere between it and the ground, so it turns orange and dim near the horizon, as Unreal's atmosphere sun light does.")]
    public bool sunTransmittance = true;
    [Tooltip("The clouds' Direct Sun Light Color is not animated in the day data (it is one fixed white), so it is set here from the sun light after the atmosphere: white at midday, warmer toward the horizon. 0 leaves it white, 1 is the full atmosphere colour.")]
    [Range(0f, 1f)] public float cloudSunColorStrength = 1f;
    readonly Dictionary<int, string> sunColorProps = new Dictionary<int, string>();
    [Tooltip("Scales the moon's disc and glow. The moon colour from the day data is first scaled so its brightest value is Moon Peak, so the disc keeps its texture instead of clipping to white.")]
    [Range(0f, 2f)] public float moonDiscBrightness = 1f;
    [Range(0.1f, 1.5f)] public float moonPeak = 0.8f;
    [Tooltip("Brightness of the sun and moon disc and glow material (its Sun Exposure) while the moon is up. 1 is the value the converted material was built for. Lower it for a dimmer moon and glow, raise it for a brighter one.")]
    [Range(0.005f, 2f)] public float moonExposure = 1f;
    [Tooltip("The same for the sun, while the sun is up.")]
    [Range(0.005f, 2f)] public float sunExposure = 1f;
    [Tooltip("Sunrise and sunset are read from the day data, but only when they land within an hour or two of 6:00 and 18:00. Turn off to always use the two hours below.")]
    public bool timesFromData = false;
    public float sunriseHour = 6f;
    public float sunsetHour = 18f;

    public ReflectionProbe probe;
    public bool updateProbe = true;
    public bool updateAmbient = false;
    public float refreshInterval = 0.5f;

    public List<Preset> presets = new List<Preset>
    {
        new Preset { name = "Morning", hour = 7f },
        new Preset { name = "Day", hour = 12f },
        new Preset { name = "Evening", hour = 19f },
        new Preset { name = "Night", hour = 0f },
    };

    Day day;
    TextAsset loadedFrom;
    List<Material> targets;
    HashSet<string> tracked;
    readonly Dictionary<string, bool> colorCache = new Dictionary<string, bool>();
    float appliedHour = -1f;
    double lastRefresh;
    bool refreshPending;

    void OnEnable()
    {
        Load();
        Apply(true);
    }

    void OnValidate()
    {
        hour = Mathf.Clamp(hour, 0f, 24f);
        pathReady = false;
        angleReady = false;
        moonScales.Clear();
        Load();
        if (day != null && slotBindings.Count == 0) AutoFill();
        if (isActiveAndEnabled) Apply(true, false);
    }

    void Update()
    {
        if (Application.isPlaying && hoursPerSecond != 0f) SetHour(Mathf.Repeat(hour + hoursPerSecond * Time.deltaTime, 24f));
        if (refreshPending && Time.realtimeSinceStartupAsDouble - lastRefresh >= refreshInterval) RefreshEnvironment();
    }

    public void SetHour(float h)
    {
        hour = Mathf.Clamp(h, 0f, 24f);
        Apply(false);
    }

    public string PhaseName()
    {
        if (presets == null || presets.Count == 0) return "";
        var best = presets[0];
        var bestDistance = 99f;
        foreach (var p in presets)
        {
            var d = Mathf.Abs(Mathf.Repeat(hour - p.hour + 12f, 24f) - 12f);
            if (d < bestDistance) { bestDistance = d; best = p; }
        }
        return best.name;
    }

    void Load()
    {
        if (dayData == null) { day = null; loadedFrom = null; return; }
        if (loadedFrom == dayData && day != null) return;
        try
        {
            day = JsonUtility.FromJson<Day>(dayData.text);
            loadedFrom = dayData;
            pathReady = false;
            angleReady = false;
            targets = null;
            tracked = null;
            resolved.Clear();
            moonScales.Clear();
        }
        catch (Exception e)
        {
            day = null;
            Debug.LogWarning("TimeOfDayController: the day data could not be read: " + e.Message, this);
        }
    }

    [ContextMenu("Auto Fill From Scene")]
    public void AutoFill()
    {
        Load();
        if (day == null) return;
        if (slotBindings == null) slotBindings = new List<SlotBinding>();
        foreach (var g in day.slots ?? new Group[0])
        {
            var binding = slotBindings.Find(b => b.slot == g.name);
            if (binding == null)
            {
                binding = new SlotBinding { slot = g.name };
                slotBindings.Add(binding);
            }
            if (binding.material == null) binding.material = Guess(g.name);
        }
        if (sun == null)
        {
            foreach (var l in AllOf<Light>())
                if (l.type == LightType.Directional) { sun = l; break; }
        }
        if (probe == null)
        {
            foreach (var p in AllOf<ReflectionProbe>()) { probe = p; break; }
        }
    }

    static T[] AllOf<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return FindObjectsByType<T>(FindObjectsSortMode.None);
#else
        return FindObjectsOfType<T>();
#endif
    }

    static Material Guess(string slot)
    {
        var words = slot.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0 || slot.IndexOf("sun", StringComparison.OrdinalIgnoreCase) >= 0
            ? new[] { "moon", "sun" } : new[] { "sky" };
        foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
        {
            if (!r.gameObject.scene.IsValid()) continue;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                foreach (var w in words)
                    if (m.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return m;
            }
        }
        return null;
    }

    [ContextMenu("Refresh Materials")]
    public void RefreshMaterials()
    {
        targets = null;
        Apply(true);
    }

    // The day data names parameters the way Unreal does ("Clouds Light Color", "CloudsLight"). The converted shader names them
    // _S_<name> (scalar), _V_<name> (vector or colour) and _C_<name> (parameter collection) with every character that is not a letter,
    // digit or underscore turned into an underscore. This is the same rule as the converter's, so every name finds its property.
    static string Ident(string s)
    {
        var t = Regex.Replace(s ?? "", "[^A-Za-z0-9_]", "_");
        if (t.Length > 0 && char.IsDigit(t[0])) t = "_" + t;
        return t;
    }

    static string Mapped(string tag, string name)
    {
        return Regex.Replace(tag + Ident(name), "_+", "_");
    }

    readonly Dictionary<string, string> resolved = new Dictionary<string, string>();

    // The property of this material that the day-data entry drives, or null. Tries the converter's own name first, then the raw name.
    string Resolve(Material m, string tag, Entry e)
    {
        var key = m.shader.GetInstanceID() + ":" + tag + ":" + e.prop;
        if (resolved.TryGetValue(key, out var known)) return known;
        string found = null;
        var tags = tag == "_C_" ? new[] { "_C_" } : (e.kind == "float" ? new[] { "_S_", "_V_" } : new[] { "_V_", "_S_" });
        foreach (var t in tags)
        {
            var n = Mapped(t, e.prop);
            if (m.HasProperty(n)) { found = n; break; }
        }
        if (found == null && m.HasProperty(e.prop)) found = e.prop;
        resolved[key] = found;
        return found;
    }

    HashSet<string> Tracked()
    {
        if (tracked != null) return tracked;
        tracked = new HashSet<string> { SunDirectionA, SunDirectionB, SunDirProp, MoonDirProp, "_AtmoRayleigh", "_AtmoMie", "_AtmoMieAbs", "_AtmoOzone", "_AtmoShape", "_AtmoTent", "_AtmoFactor", "_AtmoLight", AtmoMieScale };
        if (day != null && day.collections != null)
            foreach (var g in day.collections)
                foreach (var e in g.props) { tracked.Add(Mapped("_C_", e.prop)); tracked.Add(e.prop); }
        return tracked;
    }

    List<Material> Targets()
    {
        if (targets != null) return targets;
        targets = new List<Material>();
        var names = Tracked();
        foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
        {
            if (m == null || m.shader == null || (m.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
            foreach (var n in names)
                if (m.HasProperty(n)) { targets.Add(m); break; }
        }
        return targets;
    }

    public void Apply(bool force, bool allowRefresh = true)
    {
        if (day == null) return;
        if (!force && Mathf.Approximately(appliedHour, hour)) return;
        appliedHour = hour;
        foreach (var b in slotBindings)
        {
            if (b == null || b.material == null || day.slots == null) continue;
            var g = Array.Find(day.slots, x => x.name == b.slot);
            if (g != null) ApplyGroup(g, b.material, "");
        }
        if (applyCollections && day.collections != null)
        {
            var list = Targets();
            foreach (var g in day.collections)
                foreach (var m in list)
                    if (m != null) ApplyGroup(g, m, "_C_");
        }
        DriveAtmosphere();
        DriveSun();
        refreshPending = true;
        if (allowRefresh && Time.realtimeSinceStartupAsDouble - lastRefresh >= refreshInterval) RefreshEnvironment();
    }

    void ApplyGroup(Group g, Material m, string tag)
    {
        if (g.props == null) return;
        foreach (var e in g.props)
        {
            if (e.ch == null || e.ch.Length == 0) continue;
            var prop = Resolve(m, tag, e);
            if (prop == null) continue;
            if (e.kind == "float")
            {
                m.SetFloat(prop, Eval(e.ch[0], hour, 0f));
                continue;
            }
            var r = Eval(e.ch[0], hour, 0f);
            var gr = e.ch.Length > 1 ? Eval(e.ch[1], hour, 0f) : 0f;
            var b = e.ch.Length > 2 ? Eval(e.ch[2], hour, 0f) : 0f;
            var a = e.ch.Length > 3 ? Eval(e.ch[3], hour, 1f) : 1f;
            if (tag.Length == 0 && IsMoonColor(e))
            {
                var k = MoonScale(e) * moonDiscBrightness;
                r *= k; gr *= k; b *= k;
            }
            if (IsColor(m, prop))
                m.SetColor(prop, new Color(ToProperty(r), ToProperty(gr), ToProperty(b), a));
            else
                m.SetVector(prop, new Vector4(r, gr, b, a));
        }
    }

    // Unreal's colour values are linear. A Color property is read by Unity as sRGB and converted to linear for a Linear project, so a linear
    // value is stored as its sRGB equivalent (the same curve the converter uses for the defaults). Values below zero keep their sign instead of
    // being clamped, since some curves (halo and sun bloom) use a negative channel on purpose.
    static float ToProperty(float linear)
    {
        if (QualitySettings.activeColorSpace != ColorSpace.Linear) return linear;
        if (linear <= 0.0031308f) return 12.92f * linear;
        return 1.055f * Mathf.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    bool IsColor(Material m, string prop)
    {
        var shader = m.shader;
        var key = shader.GetInstanceID() + ":" + prop;
        if (colorCache.TryGetValue(key, out var known)) return known;
        var isColor = false;
        var count = shader.GetPropertyCount();
        for (var i = 0; i < count; i++)
        {
            if (shader.GetPropertyName(i) != prop) continue;
            isColor = shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Color;
            break;
        }
        colorCache[key] = isColor;
        return isColor;
    }

    static bool IsMoonColor(Entry e)
    {
        return e != null && e.prop != null && e.ch != null && e.ch.Length >= 3
            && e.prop.IndexOf("moon", StringComparison.OrdinalIgnoreCase) >= 0 && e.prop.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    readonly Dictionary<Entry, float> moonScales = new Dictionary<Entry, float>();

    // The brightest the moon colour gets over the whole day, scaled to Moon Peak. Unity clips at 1 where Unreal's tone mapper does not, so the
    // raw curve value would turn the disc into a white blob.
    float MoonScale(Entry e)
    {
        if (moonScales.TryGetValue(e, out var known)) return known;
        var peak = 0f;
        for (var i = 0; i <= 480; i++)
        {
            var h = i * 0.05f;
            peak = Mathf.Max(peak, Eval(e.ch[0], h, 0f), Eval(e.ch[1], h, 0f), Eval(e.ch[2], h, 0f));
        }
        var k = peak > moonPeak ? moonPeak / peak : 1f;
        moonScales[e] = k;
        return k;
    }

    static float Eval(Channel c, float h, float fallback)
    {
        if (c == null || c.none) return fallback;
        var n = (c.t == null || c.v == null) ? 0 : Mathf.Min(c.t.Length, c.v.Length);
        if (n == 0) return c.d;
        if (h <= c.t[0]) return c.v[0];
        var last = n - 1;
        if (h >= c.t[last]) return c.v[last];
        var k = 0;
        while (k + 1 < last && c.t[k + 1] <= h) k++;
        var dt = c.t[k + 1] - c.t[k];
        var a = dt > 0f ? (h - c.t[k]) / dt : 0f;
        var mode = (c.m != null && k < c.m.Length) ? c.m[k] : 2;
        if (mode == 1) return c.v[k];
        if (mode == 0) return c.v[k] + (c.v[k + 1] - c.v[k]) * a;
        var p0 = c.v[k];
        var p3 = c.v[k + 1];
        var leave = (c.l != null && k < c.l.Length) ? c.l[k] : 0f;
        var arrive = (c.a != null && k + 1 < c.a.Length) ? c.a[k + 1] : 0f;
        var p1 = p0 + leave * dt / 3f;
        var p2 = p3 - arrive * dt / 3f;
        var b = 1f - a;
        return b * b * b * p0 + 3f * b * b * a * p1 + 3f * b * a * a * p2 + a * a * a * p3;
    }

    float riseHour = 6f;
    float setHour = 18f;
    bool pathReady;

    void FindSunTimes()
    {
        var rise = 6f;
        var set = 18f;
        var found = false;
        if (timesFromData && day != null && day.slots != null)
        {
            foreach (var g in day.slots)
            {
                if (g.props == null) continue;
                var e = Array.Find(g.props, x => x.prop != null && x.prop.IndexOf("Moon Color", StringComparison.OrdinalIgnoreCase) >= 0);
                if (e == null || e.ch == null || e.ch.Length < 3) continue;
                var prevLit = MoonLit(e, 0f);
                var seenRise = false;
                var seenSet = false;
                for (var i = 1; i <= 2400; i++)
                {
                    var h = i * 0.01f;
                    var lit = MoonLit(e, h);
                    if (prevLit && !lit && !seenRise) { rise = h; seenRise = true; }
                    else if (!prevLit && lit && seenRise && !seenSet) { set = h; seenSet = true; }
                    prevLit = lit;
                }
                found = seenRise && seenSet;
                break;
            }
        }
        // The moon colour fading out does not mean the sun is up, so a result far from dawn and dusk is not trusted (it gave a 19 hour day).
        if (found && (rise < 4.5f || rise > 7.5f || set < 16.5f || set > 19.5f)) found = false;
        if (!timesFromData || !found) { rise = sunriseHour; set = sunsetHour; }
        if (set <= rise) { rise = 6f; set = 18f; }
        riseHour = rise;
        setHour = set;
    }

    static bool MoonLit(Entry e, float h)
    {
        var m = Mathf.Max(Eval(e.ch[0], h, 0f), Mathf.Max(Eval(e.ch[1], h, 0f), Eval(e.ch[2], h, 0f)));
        return m > 0.002f;
    }

    // Unreal drives the sun and the moon from one "SunAngle" track on the day actor: 180 at sunrise, 90 overhead at noon, 0 at sunset, and then
    // the same track runs 0 to 180 again for the moon (the one directional light is the moon at night). The sun is up between the key where the
    // track peaks and the key where it bottoms out.
    bool angleReady;
    bool hasAngle;
    float angleRise = 3f;
    float angleSet = 21f;

    void FindAngleTrack()
    {
        angleReady = true;
        hasAngle = false;
        var e = Binding("Root Day Sequence Actor", "SunAngle");
        if (e == null || e.ch == null || e.ch.Length == 0 || e.ch[0] == null || e.ch[0].none) return;
        var c = e.ch[0];
        var n = (c.t == null || c.v == null) ? 0 : Mathf.Min(c.t.Length, c.v.Length);
        if (n < 3) return;
        var hi = 0; var lo = 0;
        for (var i = 1; i < n; i++)
        {
            if (c.v[i] > c.v[hi]) hi = i;
            if (c.v[i] < c.v[lo]) lo = i;
        }
        if (c.t[hi] >= c.t[lo]) return;
        angleRise = c.t[hi];
        angleSet = c.t[lo];
        hasAngle = true;
        riseHour = angleRise;
        setHour = angleSet;
    }

    public bool SunIsUp()
    {
        if (!angleReady) FindAngleTrack();
        if (hasAngle) return hour >= angleRise && hour < angleSet;
        if (!pathReady) { FindSunTimes(); pathReady = true; }
        return hour >= riseHour && hour <= setHour;
    }

    public Vector3 SunDirection()
    {
        if (!angleReady) FindAngleTrack();
        if (hasAngle)
        {
            var e0 = Binding("Root Day Sequence Actor", "SunAngle");
            var deg = Eval(e0.ch[0], hour, 90f);
            var a = deg * Mathf.Deg2Rad;
            var tilt = sunHeight * Mathf.Deg2Rad;
            // The body is the sun by day and the moon by night. Both come from this track, so by night the returned direction is the one
            // the moon is toward, mirrored below the horizon so the callers' "sun below the horizon means moon" rule still holds.
            var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a) * Mathf.Sin(tilt), -Mathf.Sin(a) * Mathf.Cos(tilt));
            if (!SunIsUp()) d = -d;
            return Quaternion.Euler(0f, sunYaw, 0f) * d;
        }
        if (!pathReady) { FindSunTimes(); pathReady = true; }
        var dayLength = setHour - riseHour;
        float angle;
        if (hour >= riseHour && hour <= setHour)
            angle = Mathf.PI * (hour - riseHour) / dayLength;
        else
        {
            var h = hour < riseHour ? hour + 24f : hour;
            angle = Mathf.PI + Mathf.PI * (h - setHour) / (24f - dayLength);
        }
        var e = sunHeight * Mathf.Deg2Rad;
        var dir = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle) * Mathf.Sin(e), -Mathf.Sin(angle) * Mathf.Cos(e));
        return Quaternion.Euler(0f, sunYaw, 0f) * dir;
    }

    Entry Binding(string group, string prop)
    {
        if (day == null || day.bindings == null) return null;
        foreach (var g in day.bindings)
        {
            if (g.name != group || g.props == null) continue;
            foreach (var e in g.props)
                if (e.prop == prop) return e;
        }
        return null;
    }

    public bool Rgb(string group, string prop, out Vector3 rgb)
    {
        rgb = Vector3.zero;
        var e = Binding(group, prop);
        if (e == null || e.ch == null || e.ch.Length < 3) return false;
        rgb = new Vector3(Eval(e.ch[0], hour, 0f), Eval(e.ch[1], hour, 0f), Eval(e.ch[2], hour, 0f));
        return true;
    }

    public bool Scalar(string group, string prop, out float value)
    {
        value = 0f;
        var e = Binding(group, prop);
        if (e == null || e.ch == null || e.ch.Length < 1) return false;
        value = Eval(e.ch[0], hour, 0f);
        return true;
    }

    Vector4 sunColorLinear = Vector4.one;
    float effectiveMieScale = 2f;
    Vector3 atmFactor = Vector3.one;
    Vector3 ambientCache = Vector3.zero;
    float ambientHour = -1f;
    float ambientSunYaw = float.NaN;
    Vector3 atmRay = new Vector3(0.175287f, 0.409607f, 1f);
    Vector3 atmMie = Vector3.one;
    Vector3 atmMieAbs = Vector3.one;
    Vector3 atmOzone = new Vector3(0.345561f, 1f, 0.045189f);
    Vector3 atmShape = new Vector3(0.8f, 8f, 1.2f);
    Vector2 atmTent = new Vector2(1f, 15f);
    float atmMulti = 1f;
    float transmittanceMinElevation;

    Vector3 Extinction(float h)
    {
        var rd = Mathf.Exp(-h / Mathf.Max(atmShape.y, 0.01f));
        var md = Mathf.Exp(-h / Mathf.Max(atmShape.z, 0.01f));
        var od = atmTent.x * Mathf.Max(1f - Mathf.Abs(h - 25f) / Mathf.Max(atmTent.y, 0.01f), 0f);
        return atmRay * (0.0331f * rd) + atmMie * (0.003996f * effectiveMieScale * md) + atmMieAbs * (0.00444f * effectiveMieScale * md) + atmOzone * (0.001881f * od);
    }

    // How much of each colour of light reaches the ground from this direction, with the same atmosphere the sky shader uses.
    Vector3 TransmittanceToward(Vector3 dir)
    {
        dir.Normalize();
        var o = new Vector3(0f, 6360.05f, 0f);
        var b = Vector3.Dot(o, dir);
        var c = Vector3.Dot(o, o) - 6420f * 6420f;
        var disc = b * b - c;
        if (disc < 0f) return Vector3.one;
        var tTop = -b + Mathf.Sqrt(disc);
        var optical = Vector3.zero;
        const int steps = 24;
        for (var i = 0; i < steps; i++)
        {
            var f = (i + 0.5f) / steps;
            var p = o + dir * (tTop * f * f);
            optical += Extinction(Mathf.Max(p.magnitude - 6360f, 0f)) * (tTop * 2f * f / steps);
        }
        return new Vector3(Mathf.Exp(-optical.x), Mathf.Exp(-optical.y), Mathf.Exp(-optical.z));
    }

    static float RaySphere(Vector3 o, Vector3 d, float r)
    {
        var b = Vector3.Dot(o, d);
        var c = Vector3.Dot(o, o) - r * r;
        var disc = b * b - c;
        return disc < 0f ? -1f : -b + Mathf.Sqrt(disc);
    }

    // The same single plus second-order scattering the sky shader computes, for one direction and one light.
    Vector3 ScatterLight(Vector3 v, Vector3 l, float illuminance)
    {
        var o = new Vector3(0f, 6360.05f, 0f);
        var tTop = RaySphere(o, v, 6420f);
        var tGround = RaySphere(o, v, 6360f);
        var tEnd = tTop;
        if (tGround > 0f)
        {
            var b = Vector3.Dot(o, v);
            var c = Vector3.Dot(o, o) - 6360f * 6360f;
            var disc = b * b - c;
            tEnd = Mathf.Min(tTop, -b - Mathf.Sqrt(Mathf.Max(disc, 0f)));
        }
        tEnd = Mathf.Max(tEnd, 0f);
        var mu = Vector3.Dot(v, l);
        var g = Mathf.Clamp(atmShape.x, -0.99f, 0.99f);
        var phaseR = 0.0596831f * (1f + mu * mu);
        var phaseM = 0.0795775f * (1f - g * g) / Mathf.Pow(Mathf.Max(1f + g * g - 2f * g * mu, 0.0001f), 1.5f);
        var optical = Vector3.zero;
        var sum = Vector3.zero;
        for (var i = 0; i < 16; i++)
        {
            var f = (i + 0.5f) / 16f;
            var ds = tEnd * 2f * f / 16f;
            var p = o + v * (tEnd * f * f);
            var h = Mathf.Max(p.magnitude - 6360f, 0f);
            optical += Extinction(h) * ds;
            var sunOptical = Vector3.zero;
            var tl = Mathf.Max(RaySphere(p, l, 6420f), 0f);
            var lit = 1f;
            var pl = Vector3.Dot(p, l);
            var cl = Vector3.Dot(p, p) - 6360f * 6360f;
            if (pl < 0f && pl * pl - cl > 0f) lit = 0f;
            for (var j = 0; j < 8; j++)
            {
                var fj = (j + 0.5f) / 8f;
                var q = p + l * (tl * fj * fj);
                sunOptical += Extinction(Mathf.Max(q.magnitude - 6360f, 0f)) * (tl * 2f * fj / 8f);
            }
            var rd = Mathf.Exp(-h / Mathf.Max(atmShape.y, 0.01f));
            var md = Mathf.Exp(-h / Mathf.Max(atmShape.z, 0.01f));
            var scatterR = atmRay * (0.0331f * rd);
            var scatterM = atmMie * (0.003996f * effectiveMieScale * md);
            var scatter = scatterR * phaseR + scatterM * phaseM + (scatterR + scatterM) * (0.0795775f * atmMulti);
            var total = optical + sunOptical;
            var trans = new Vector3(Mathf.Exp(-total.x), Mathf.Exp(-total.y), Mathf.Exp(-total.z));
            sum += Vector3.Scale(scatter, trans) * (lit * ds);
        }
        var light = new Vector3(sunColorLinear.x, sunColorLinear.y, sunColorLinear.z);
        return Vector3.Scale(Vector3.Scale(sum * illuminance, light), atmFactor);
    }

    // The sky's average colour as seen from the ground (up and four directions just above the horizon), before the tone curve. The fog adds this,
    // scaled by the fog actor's Sky Atmosphere Ambient Contribution Color Scale, the way Unreal lets the sky light the fog.
    public Vector3 SkyAmbientRadiance()
    {
        if (Mathf.Approximately(ambientHour, hour) && ambientSunYaw == sunYaw) return ambientCache;
        ambientHour = hour;
        ambientSunYaw = sunYaw;
        var sunIllum = 2.8f;
        var moonIllum = 1.3f;
        foreach (var m in Targets())
        {
            if (m == null || !m.HasProperty("_AtmoSun")) continue;
            sunIllum = m.GetFloat("_AtmoSun");
            if (m.HasProperty("_AtmoMoon")) moonIllum = m.GetFloat("_AtmoMoon");
            break;
        }
        var toSun = SunDirection();
        var moonUp = toSun.y < 0f;
        var low = Mathf.Cos(20f * Mathf.Deg2Rad);
        var rise = Mathf.Sin(20f * Mathf.Deg2Rad);
        var dirs = new[] { Vector3.up, new Vector3(low, rise, 0f), new Vector3(-low, rise, 0f), new Vector3(0f, rise, low), new Vector3(0f, rise, -low) };
        var sum = Vector3.zero;
        foreach (var d in dirs)
        {
            sum += ScatterLight(d, toSun, sunIllum);
            if (moonUp) sum += ScatterLight(d, -toSun, moonIllum);
        }
        ambientCache = sum / dirs.Length;
        return ambientCache;
    }

    void DriveAtmosphere()
    {
        sunColorLinear = Vector4.one;
        if (!atmosphereFromData || day == null || day.bindings == null) return;
        var rayleigh = new Vector4(0.175287f, 0.409607f, 1f, 0f);
        var mie = new Vector4(1f, 1f, 1f, 0f);
        var mieAbs = new Vector4(1f, 1f, 1f, 0f);
        var ozone = new Vector4(0.345561f, 1f, 0.045189f, 0f);
        var factor = new Vector4(1f, 1f, 1f, 0f);
        var shape = new Vector4(0.8f, 8f, 1.2f, 0f);
        if (Rgb("SkyAtmosphere", "RayleighScattering", out var v)) rayleigh = new Vector4(v.x, v.y, v.z, 0f);
        if (Rgb("SkyAtmosphere", "MieScattering", out v)) mie = new Vector4(v.x, v.y, v.z, 0f);
        if (Rgb("SkyAtmosphere", "MieAbsorption", out v)) mieAbs = new Vector4(v.x, v.y, v.z, 0f);
        if (Rgb("SkyAtmosphere", "OtherAbsorption", out v)) ozone = new Vector4(v.x, v.y, v.z, 0f);
        if (Rgb("SkyAtmosphere", "SkyAndAerialPerspectiveLuminanceFactor", out v)) factor = new Vector4(v.x, v.y, v.z, 0f);
        if (Scalar("SkyAtmosphere", "MieAnisotropy", out var f)) shape.x = f;
        if (Scalar("SkyAtmosphere", "RayleighExponentialDistribution", out f)) shape.y = f;
        if (Scalar("SkyAtmosphere", "MieExponentialDistribution", out f)) shape.z = f;
        var tent = new Vector4(1f, 15f, 1f, 0f);
        if (Scalar("SkyAtmosphere", "MultiScatteringFactor", out f)) tent.z = f;
        transmittanceMinElevation = Scalar("SkyAtmosphere", "TransmittanceMinLightElevationAngle", out f) ? Mathf.Clamp(f, 0f, 90f) : 0f;
        if (Scalar("SkyAtmosphere", "OtherTentDistribution.TipValue", out f)) tent.x = f;
        if (Scalar("SkyAtmosphere", "OtherTentDistribution.Width", out f)) tent.y = f;
        effectiveMieScale = mieScale;
        if (Scalar("SkyAtmosphere", "RayleighScatteringScale", out f)) rayleigh *= f / 0.0331f;
        if (Scalar("SkyAtmosphere", "MieScatteringScale", out f)) { mie *= f / 0.003996f; effectiveMieScale = 1f; }
        if (Scalar("SkyAtmosphere", "MieAbsorptionScale", out f)) mieAbs *= f / 0.00444f;
        if (Scalar("SkyAtmosphere", "OtherAbsorptionScale", out f)) ozone *= f / 0.001881f;
        if (Rgb("Sun", "LightColor", out v))
            sunColorLinear = new Vector4(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y), Mathf.Clamp01(v.z), 1f);
        atmFactor = new Vector3(factor.x, factor.y, factor.z);
        atmRay = new Vector3(rayleigh.x, rayleigh.y, rayleigh.z);
        atmMie = new Vector3(mie.x, mie.y, mie.z);
        atmMieAbs = new Vector3(mieAbs.x, mieAbs.y, mieAbs.z);
        atmOzone = new Vector3(ozone.x, ozone.y, ozone.z);
        atmShape = new Vector3(shape.x, shape.y, shape.z);
        atmTent = new Vector2(tent.x, tent.y);
        atmMulti = tent.z;
        foreach (var m in Targets())
        {
            if (m == null) continue;
            if (m.HasProperty(AtmoRayleigh)) m.SetVector(AtmoRayleigh, rayleigh);
            if (m.HasProperty(AtmoMie)) m.SetVector(AtmoMie, mie);
            if (m.HasProperty(AtmoMieAbs)) m.SetVector(AtmoMieAbs, mieAbs);
            if (m.HasProperty(AtmoOzone)) m.SetVector(AtmoOzone, ozone);
            if (m.HasProperty(AtmoShape)) m.SetVector(AtmoShape, shape);
            if (m.HasProperty(AtmoTent)) m.SetVector(AtmoTent, tent);
            if (m.HasProperty(AtmoFactor)) m.SetVector(AtmoFactor, factor);
            if (m.HasProperty(AtmoLight)) m.SetVector(AtmoLight, sunColorLinear);
            if (m.HasProperty(AtmoMieScale)) m.SetFloat(AtmoMieScale, effectiveMieScale);
        }
    }

    Vector3 ClampTransmittanceElevation(Vector3 dir)
    {
        var minSin = Mathf.Sin(transmittanceMinElevation * Mathf.Deg2Rad);
        if (transmittanceMinElevation <= 0f || dir.y >= minSin) return dir;
        var flat = new Vector2(dir.x, dir.z);
        flat = flat.sqrMagnitude < 1e-6f ? Vector2.right : flat.normalized;
        var c = Mathf.Cos(transmittanceMinElevation * Mathf.Deg2Rad);
        return new Vector3(flat.x * c, minSin, flat.y * c);
    }

    void DriveSun()
    {
        var toSun = SunDirection();
        var moonUp = toSun.y < 0f;
        var toMoon = -toSun;
        var toBody = moonUp ? toMoon : toSun;
        foreach (var b in slotBindings)
        {
            if (b == null || b.material == null || string.IsNullOrEmpty(b.slot)) continue;
            if (b.slot.IndexOf("sun", StringComparison.OrdinalIgnoreCase) < 0 && b.slot.IndexOf("moon", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (b.material.HasProperty("_SkyExposure")) b.material.SetFloat("_SkyExposure", moonUp ? moonExposure : sunExposure);
        }
        if (sun != null && driveSun)
        {
            sun.transform.rotation = Quaternion.LookRotation(-toBody, Vector3.up);
            var fade = Mathf.Clamp01(Mathf.InverseLerp(-0.02f, 0.12f, toBody.y));
            sun.intensity = (moonUp ? moonIntensity : sunIntensity) * fade;
            if (atmosphereFromData)
            {
                var lightRgb = new Vector3(sunColorLinear.x, sunColorLinear.y, sunColorLinear.z);
                if (sunTransmittance)
                {
                    var t = TransmittanceToward(ClampTransmittanceElevation(toBody));
                    var tz = TransmittanceToward(Vector3.up);
                    lightRgb = new Vector3(lightRgb.x * Mathf.Clamp01(t.x / Mathf.Max(tz.x, 1e-4f)), lightRgb.y * Mathf.Clamp01(t.y / Mathf.Max(tz.y, 1e-4f)), lightRgb.z * Mathf.Clamp01(t.z / Mathf.Max(tz.z, 1e-4f)));
                }
                sun.color = new Color(ToProperty(lightRgb.x), ToProperty(lightRgb.y), ToProperty(lightRgb.z), 1f);
            }
        }
        DriveCloudSunColor(toBody);
        if (!driveSunVectors) return;
        var ue = new Vector4(toBody.x, toBody.z, toBody.y, 0f);
        var sunVec = new Vector4(toSun.x, toSun.y, toSun.z, 0f);
        var moonVec = moonUp ? new Vector4(toMoon.x, toMoon.y, toMoon.z, 0f) : Vector4.zero;
        foreach (var m in Targets())
        {
            if (m == null) continue;
            if (m.HasProperty(SunDirectionA)) m.SetVector(SunDirectionA, ue);
            if (m.HasProperty(SunDirectionB)) m.SetVector(SunDirectionB, ue);
            if (m.HasProperty(SunDirProp)) m.SetVector(SunDirProp, sunVec);
            if (m.HasProperty(MoonDirProp)) m.SetVector(MoonDirProp, moonVec);
        }
    }

    // Unreal's cloud material multiplies its colour by the sun light colour, which the game sets while it runs (the day data holds a single white key).
    // Here it is the colour the atmosphere leaves in the sunlight: 1 at zenith, redder as the sun (or moon) gets low.
    void DriveCloudSunColor(Vector3 toBody)
    {
        if (!atmosphereFromData) return;
        var ratio = Vector3.one;
        if (cloudSunColorStrength > 0f)
        {
            var t = TransmittanceToward(ClampTransmittanceElevation(toBody));
            var tz = TransmittanceToward(Vector3.up);
            ratio = new Vector3(Mathf.Clamp01(t.x / Mathf.Max(tz.x, 1e-4f)), Mathf.Clamp01(t.y / Mathf.Max(tz.y, 1e-4f)), Mathf.Clamp01(t.z / Mathf.Max(tz.z, 1e-4f)));
            ratio = Vector3.Lerp(Vector3.one, ratio, cloudSunColorStrength);
        }
        foreach (var m in Targets())
        {
            if (m == null) continue;
            var prop = SunColorProperty(m);
            if (prop == null) continue;
            if (IsColor(m, prop)) m.SetColor(prop, new Color(ToProperty(ratio.x), ToProperty(ratio.y), ToProperty(ratio.z), 1f));
            else m.SetVector(prop, new Vector4(ratio.x, ratio.y, ratio.z, 1f));
        }
    }

    // The material's game-wide "direct sun light colour" property, whatever the skybox calls it (for example _C_CloudsDirectSunLightColor), or null.
    string SunColorProperty(Material m)
    {
        var id = m.shader.GetInstanceID();
        if (sunColorProps.TryGetValue(id, out var known)) return known;
        string found = null;
        var shader = m.shader;
        var count = shader.GetPropertyCount();
        for (var i = 0; i < count; i++)
        {
            var n = shader.GetPropertyName(i);
            if (n.StartsWith("_C_", StringComparison.Ordinal) && n.IndexOf("DirectSunLight", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0) { found = n; break; }
        }
        sunColorProps[id] = found;
        return found;
    }

    public void RefreshEnvironment()
    {
        refreshPending = false;
        lastRefresh = Time.realtimeSinceStartupAsDouble;
        if (updateProbe && probe != null) probe.RenderProbe();
        if (updateAmbient) DynamicGI.UpdateEnvironment();
    }
}
