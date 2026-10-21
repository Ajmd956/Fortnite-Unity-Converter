import argparse
import json
import math
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

import numpy as np

from niagara_core import niagara

APP_NAME = "NiagaraPorter"
DEFAULT_BRIDGE = "http://localhost:24320"
DT = 1.0 / 60.0
CURVE_POINTS = 8            # a Unity gradient holds at most 8 colour keys
BURST_WINDOW = 0.1          # births in the first 0.1 s count as a burst at time 0
INITIAL_ATTRIBUTES = ("Position", "Velocity", "SpriteRotation", "MeshOrientation")


# ---------------------------------------------------------------- getting the asset

def http_get(bridge, route, path):
    url = "%s/%s?%s" % (bridge.rstrip("/"), route, urllib.parse.urlencode({"path": path}))
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return r.read()
    except urllib.error.HTTPError as e:
        why = ""
        try:
            why = str(json.loads(e.read().decode("utf-8")).get("error") or "")
        except Exception:
            pass
        raise RuntimeError("HTTP Error %s%s" % (e.code, (": " + why) if why else ""))
    except urllib.error.URLError as e:
        raise RuntimeError("urlopen error: %s" % (e.reason,))


def fetch_dump(bridge, path):
    """The package's exports as the helper's asset route writes them (FModel's Save Properties)."""
    info = json.loads(http_get(bridge, "asset", path).decode("utf-8"))
    file = info["file"]
    raw = open(file, "rb").read() if os.path.exists(file) else http_get(bridge, "file", file)
    return json.loads(raw.decode("utf-8-sig"))


def to_exports(dump):
    """The dump's exports in the form the playback code reads: name, type, outer, props."""
    package = None
    for e in dump:
        outer = e.get("Outer")
        if outer and outer.get("ObjectPath"):
            package = outer["ObjectPath"].rpartition(".")[0]
            break
    out = []
    for e in dump:
        outer = (e.get("Outer") or {}).get("ObjectPath")
        out.append({"name": e["Name"], "type": e["Type"],
                    "outer": package if e["Type"] == "NiagaraSystem" else outer,
                    "props": e.get("Properties") or {}})
    return out


# ---------------------------------------------------------------- playing it

class Recorder:
    """What one emitter did over a play: when particles were born, what they were born with, and how
    colour and size run over a particle's age."""

    def __init__(self):
        self.births = []        # (time, count)
        self.total = 0
        self.max_alive = 0
        self.lifetimes = []
        self.persistent = False
        self.ages = []          # per tick: normalised age of every live particle
        self.colors = []        # and its colour (4, n)
        self.sizes = []         # and its size (3, n): sprite size (x, y, 1) or mesh scale
        self.sprite = False
        self.initial = {}       # per attribute: what each particle was born with, (components, born) per birth tick

    def tick(self, e, t0):
        count = int(e.data.count)
        self.max_alive = max(self.max_alive, count)
        born = int(getattr(e, "born", 0))
        rows = {}
        for name in ("Age", "Lifetime", "Color", "SpriteSize", "Scale") + INITIAL_ATTRIBUTES:
            a = e.attribute(name)
            rows[name] = None if a is None else np.array(a[:, :count], np.float64)
        if born > 0:
            self.births.append((t0, born))
            self.total += born
            for name in INITIAL_ATTRIBUTES:
                if rows[name] is not None and count >= born:
                    self.initial.setdefault(name, []).append(rows[name][:, count - born:count])
            if rows["Lifetime"] is not None:
                self.lifetimes.extend(rows["Lifetime"][0, count - born:count].tolist())
        age, life = rows["Age"], rows["Lifetime"]
        if age is None or life is None or not count:
            return
        age, life = age[0], life[0]
        if np.any((life > 0) & (age > life * 1.01 + 2 * DT)):
            self.persistent = True
        ok = life > 0
        if not ok.any():
            return
        size = rows["SpriteSize"]
        if size is not None:
            self.sprite = True
            size = np.vstack([size[:2], np.ones((1, count))])
        else:
            size = rows["Scale"]
        self.ages.append((age / np.where(ok, life, 1.0))[ok])
        self.colors.append(None if rows["Color"] is None else rows["Color"][:, ok])
        self.sizes.append(None if size is None else size[:, ok])


def play(exports, user, seconds, seed=1):
    system = niagara.System(exports, seed=seed, user=user)
    recorders = {}
    for i in range(int(round(seconds / DT))):
        system.tick(DT)
        for e in system.emitters:
            recorders.setdefault(e.name, Recorder()).tick(e, i * DT)
        if system.done:
            break
    for e in system.emitters:
        recorders.setdefault(e.name, Recorder())
    return system, recorders


def find_gates(exports, system, recorders, seconds):
    """Emitters that spawned nothing may wait on user parameters (the game sets them). Try the
    system's float parameters at 1, then its bool ones too, then drop parameters one at a time while
    the same emitters still wake. Returns (parameters, emitters that woke) or None."""
    quiet = [e.name for e in system.emitters if recorders[e.name].total == 0]
    if not quiet:
        return None
    users = system.users()
    floats = [n for n, t, v in users if t == "NiagaraFloat"]
    both = [n for n, t, v in users if t in ("NiagaraFloat", "NiagaraBool")]
    for pool in (floats, both):
        if not pool:
            continue
        _, rec = play(exports, {n: [1.0] for n in pool}, seconds)
        woke = [n for n in quiet if rec.get(n) and rec[n].total > 0]
        if not woke:
            continue
        keep = list(pool)
        for n in (list(pool) if len(pool) <= 20 else []):
            trial = [k for k in keep if k != n]
            if not trial:
                continue
            _, rec = play(exports, {k: [1.0] for k in trial}, seconds)
            if all(rec.get(w) and rec[w].total > 0 for w in woke):
                keep = trial
        return keep, woke
    return None


# ---------------------------------------------------------------- what it says

def r4(x):
    return round(float(x), 4)


def split_births(births):
    """(bursts, loop period, rate). A burst that comes round again and again (at least three times,
    evenly, at least 0.25 s apart) is one burst of a loop of that period. What is left: births on
    the first tick are a burst at time 0; the later ones, at least three of them, are a steady rate
    (events after the first, over the time from the first to the last); fewer are bursts of their
    own."""
    multi = [(t, n) for t, n in births if n >= 2]
    period, repeated = 0.0, set()
    if len(multi) >= 3:
        gaps = np.diff([t for t, n in multi])
        mean = float(gaps.mean())
        if mean >= 0.25 and float(gaps.std()) <= 0.05 * mean + DT:
            period = mean
            repeated = {t for t, n in multi}
    rest = [(t, n) for t, n in births if t not in repeated]
    bursts = []
    if period:
        counts = [n for t, n in multi]
        bursts.append({"time": 0.0, "count": int(np.median(counts))})
    start = sum(n for t, n in rest if t < DT / 2)
    if start:
        bursts.append({"time": 0.0, "count": int(start)})
    late = [(t, n) for t, n in rest if t >= DT / 2]
    rate = 0.0
    if len(late) >= 3:
        span = late[-1][0] - late[0][0]
        rate = sum(n for t, n in late[1:]) / span if span > 0 else 0.0
    else:
        bursts += [{"time": r4(t), "count": int(n)} for t, n in late]
    return bursts, r4(period), r4(rate)


def curves(r):
    """Colour and size over normalised age, as CURVE_POINTS points: each the mean over the
    particle-ticks near that age. Size comes back as a fraction of its largest."""
    if not r.ages:
        return [], [], None
    age = np.concatenate(r.ages)
    keep = age <= 1.0 + 1e-9
    age = age[keep]
    if not len(age):
        return [], [], None
    centers = np.linspace(0.0, 1.0, CURVE_POINTS)
    half = 0.5 / (CURVE_POINTS - 1)
    color_rows = [c for c in r.colors if c is not None]
    size_rows = [s for s in r.sizes if s is not None]
    color = np.hstack(color_rows)[:, keep] if color_rows else None
    size = np.hstack(size_rows)[:, keep] if size_rows else None
    color_out, size_pts = [], []
    for c in centers:
        near = np.abs(age - c) <= half + 1e-9
        if not near.any():
            continue
        if color is not None:
            m = color[:, near].mean(axis=1)
            color_out.append({"t": r4(c), "r": r4(m[0]), "g": r4(m[1]), "b": r4(m[2]), "a": r4(m[3])})
        if size is not None:
            size_pts.append((c, size[:, near].mean(axis=1)))
    size_out, start = [], None
    if size_pts:
        top = np.max([s for _, s in size_pts], axis=0)
        start = {"x": r4(top[0]), "y": r4(top[1]), "z": r4(top[2])}
        drive = top[0] if top[0] > 0 else 1.0
        size_out = [{"t": r4(c), "s": r4(s[0] / drive)} for c, s in size_pts]
    return color_out, size_out, start


def strip(path):
    return re.sub(r"\.\d+$", "", path) if isinstance(path, str) else None


def object_path(ref):
    return strip((ref or {}).get("ObjectPath")) if isinstance(ref, dict) else None


def renderers(system, emitter):
    """Each renderer an emitter draws with: its kind and the materials and meshes it names."""
    version = getattr(emitter, "version", None)
    if version is None:
        version = (getattr(emitter, "export", None) or {}).get("props") or {}
    found = []
    for ref in version.get("RendererProperties") or []:
        export = system.export(ref)
        if export is None:
            continue
        props = export["props"]
        kind = export["type"].replace("Niagara", "").replace("RendererProperties", "")
        info = {"kind": kind, "enabled": bool(props.get("bIsEnabled", True)), "material": "", "mesh": "", "materials": [],
                "facing": str(props.get("FacingMode") or "").split("::")[-1], "note": ""}
        if kind in ("Sprite", "Ribbon"):
            info["material"] = object_path(props.get("Material")) or ""
        elif kind == "Mesh":
            meshes = [object_path(m.get("Mesh")) for m in props.get("Meshes") or []]
            info["mesh"] = next((m for m in meshes if m), "")
            if props.get("bOverrideMaterials"):
                info["materials"] = [p for p in (object_path(o.get("ExplicitMat")) for o in props.get("OverrideMaterials") or []) if p]
            info["material"] = info["materials"][0] if info["materials"] else ""
            if not info["material"]:
                info["note"] = "uses the mesh's own materials"
        elif kind == "Light":
            info["note"] = "light renderer: not converted"
        else:
            info["note"] = "%s renderer: not converted" % kind
        found.append(info)
    return found


def short(path):
    return (path or "").rsplit("/", 1)[-1]


def summarize(system, e, r, required, approximate):
    bursts, period, rate = split_births(r.births)
    color, size, start = curves(r)
    life = np.array(r.lifetimes) if r.lifetimes else None
    out = {
        "name": e.name,
        "local": bool(e.local),
        "approximate": e.name in approximate,
        "requires": required,
        "persistent": bool(r.persistent),
        "bursts": bursts,
        "loopPeriod": period,
        "rate": rate,
        "totalBorn": int(r.total),
        "maxAlive": int(r.max_alive),
        "lifetimeMin": r4(life.min()) if life is not None else 0.0,
        "lifetimeMax": r4(life.max()) if life is not None else 0.0,
        "colorCurve": color,
        "sizeCurve": size,
        "startSize": start or {"x": 1.0, "y": 1.0, "z": 1.0},
        "sizeUnit": "cm" if r.sprite else "scale",
        "renderers": renderers(system, e),
    }
    return out


def warnings_for(em):
    w = []
    name = em["name"]
    if em["approximate"]:
        w.append("%s: a stateless emitter, played from its settings; the engine's random draws are not reproduced" % name)
    if em["requires"]:
        w.append("%s: only emits when the game sets %s to 1; its object is created switched off" % (name, ", ".join(em["requires"])))
    if em["totalBorn"] == 0:
        w.append("%s: spawned nothing in the time played" % name)
    else:
        w.append("%s: velocity, rotation and spawn position are not carried over" % name)
    peak = max([max(c["r"], c["g"], c["b"], c["a"]) for c in em["colorCurve"]] or [0.0])
    if peak > 1.0:
        w.append("%s: colour goes up to %.2f (above 1); Unity particle colours stop at 1" % (name, peak))
    for r in em["renderers"]:
        if r["note"]:
            w.append("%s: %s" % (name, r["note"]))
        if r["kind"] == "Sprite" and r["facing"] not in ("", "FaceCamera"):
            w.append("%s: sprite facing %s is drawn facing the camera" % (name, r["facing"]))
    return w


# ---------------------------------------------------------------- the Unity script

PARTICLE_CS = r'''using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class PorterParticleSetup
{
    [Serializable]
    class Burst { public float time; public int count; }

    [Serializable]
    class Key { public float t, r, g, b, a, s; }

    [Serializable]
    class Size { public float x, y, z; }

    [Serializable]
    class Rend { public string kind, material, mesh, facing, note; public bool enabled; public List<string> materials; }

    [Serializable]
    class Emitter
    {
        public string name, sizeUnit;
        public bool local, persistent;
        public List<string> requires;
        public List<Burst> bursts;
        public float rate, loopPeriod, lifetimeMin, lifetimeMax;
        public int maxAlive;
        public List<Key> colorCurve, sizeCurve;
        public Size startSize;
        public List<Rend> renderers;
    }

    [Serializable]
    class Manifest { public string system; public List<Emitter> emitters; }

    [Serializable]
    class TexEntry { public string name, prop; public int srgb; public bool normal; }

    [Serializable]
    class TexManifest { public List<TexEntry> textureList; }

    public static GameObject BuildPrefab(string jsonPath, bool force)
    {
        TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath);
        if (asset == null) return null;
        Manifest d = JsonUtility.FromJson<Manifest>(asset.text);
        if (d == null || d.emitters == null || string.IsNullOrEmpty(d.system)) return null;
        string folder = Path.GetDirectoryName(jsonPath).Replace('\\', '/');
        string prefabPath = folder + "/" + d.system + ".prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null)
        {
            if (!force) return null;
            prefabPath = AssetDatabase.GenerateUniqueAssetPath(prefabPath);
        }
        GameObject root = new GameObject(d.system);
        foreach (Emitter e in d.emitters)
        {
            List<Rend> drawn = e.renderers.FindAll(r => r.enabled && (r.kind == "Sprite" || r.kind == "Mesh"));
            if (drawn.Count == 0)
            {
                Debug.LogWarning("Porter: " + e.name + " has no sprite or mesh renderer, so it was left out");
                continue;
            }
            for (int i = 0; i < drawn.Count; i++)
            {
                Make(root.transform, e, drawn[i], drawn.Count > 1 ? e.name + " " + (i + 1) : e.name, folder);
            }
        }
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Debug.Log("Porter: saved " + prefabPath);
        return prefab;
    }

    static void Make(Transform parent, Emitter e, Rend r, string label, string folder)
    {
        GameObject go = new GameObject(label);
        go.transform.SetParent(parent, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = ps.main;
        main.loop = e.rate > 0f || e.loopPeriod > 0f;
        main.duration = e.loopPeriod > 0f ? e.loopPeriod : 1f;
        main.startSpeed = 0f;
        main.maxParticles = Mathf.Max(8, e.maxAlive * 2);
        main.simulationSpace = e.local ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
        if (e.persistent)
        {
            main.startLifetime = new ParticleSystem.MinMaxCurve(100000f);
        }
        else if (e.lifetimeMax > e.lifetimeMin)
        {
            main.startLifetime = new ParticleSystem.MinMaxCurve(e.lifetimeMin, e.lifetimeMax);
        }
        else
        {
            main.startLifetime = new ParticleSystem.MinMaxCurve(e.lifetimeMax);
        }

        float unit = e.sizeUnit == "cm" ? 0.01f : 1f;
        main.startSize3D = true;
        main.startSizeX = e.startSize.x * unit;
        main.startSizeY = e.startSize.y * unit;
        main.startSizeZ = e.startSize.z;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = e.rate;
        ParticleSystem.Burst[] bursts = new ParticleSystem.Burst[e.bursts.Count];
        for (int i = 0; i < bursts.Length; i++)
        {
            bursts[i] = new ParticleSystem.Burst(e.bursts[i].time, (short)e.bursts[i].count);
        }
        emission.SetBursts(bursts);

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = false;

        if (e.colorCurve != null && e.colorCurve.Count > 0)
        {
            int n = Mathf.Max(2, e.colorCurve.Count);
            GradientColorKey[] colors = new GradientColorKey[n];
            GradientAlphaKey[] alphas = new GradientAlphaKey[n];
            for (int i = 0; i < n; i++)
            {
                Key k = e.colorCurve[Mathf.Min(i, e.colorCurve.Count - 1)];
                float t = e.colorCurve.Count == 1 ? i : k.t;
                Color c = new Color(Mathf.Clamp01(k.r), Mathf.Clamp01(k.g), Mathf.Clamp01(k.b), Mathf.Clamp01(k.a)).gamma;
                colors[i] = new GradientColorKey(new Color(c.r, c.g, c.b, 1f), t);
                alphas[i] = new GradientAlphaKey(c.a, t);
            }
            Gradient gradient = new Gradient();
            gradient.SetKeys(colors, alphas);
            ParticleSystem.ColorOverLifetimeModule colorModule = ps.colorOverLifetime;
            colorModule.enabled = true;
            colorModule.color = gradient;
        }

        if (e.sizeCurve != null && e.sizeCurve.Count > 1)
        {
            Keyframe[] frames = new Keyframe[e.sizeCurve.Count];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = new Keyframe(e.sizeCurve[i].t, e.sizeCurve[i].s);
            }
            ParticleSystem.SizeOverLifetimeModule sizeModule = ps.sizeOverLifetime;
            sizeModule.enabled = true;
            sizeModule.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(frames));
        }

        ParticleSystemRenderer pr = go.GetComponent<ParticleSystemRenderer>();
        if (r.kind == "Mesh")
        {
            pr.renderMode = ParticleSystemRenderMode.Mesh;
            string meshPath = FindPath(NameOf(r.mesh), "Mesh", folder);
            Mesh mesh = meshPath == null ? null : AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh != null) pr.mesh = mesh;
            else Debug.LogWarning("Porter: mesh " + NameOf(r.mesh) + " was not found in the project (it should be the .fbx in the Meshes folder next to this prefab), so " + label + " draws nothing until you set its mesh");
        }
        else
        {
            pr.renderMode = ParticleSystemRenderMode.Billboard;
        }
        Material material = EnsureMaterial(r.material, folder);
        if (material != null) pr.sharedMaterial = material;
        else Debug.LogWarning("Porter: no shader named " + NameOf(r.material) + " was found in the project, so " + label + " uses the default material");

        go.SetActive(e.requires == null || e.requires.Count == 0);
    }

    static string NameOf(string objectPath)
    {
        if (string.IsNullOrEmpty(objectPath)) return "";
        string name = objectPath.Substring(objectPath.LastIndexOf('/') + 1);
        int dot = name.IndexOf('.');
        return dot >= 0 ? name.Substring(0, dot) : name;
    }

    static string FindPath(string name, string type, string folder)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string found = null;
        foreach (string guid in AssetDatabase.FindAssets(name + " t:" + type))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(p) != name) continue;
            if (p.StartsWith(folder + "/")) return p;
            if (found == null) found = p;
        }
        return found;
    }

    static Material EnsureMaterial(string objectPath, string folder)
    {
        string name = NameOf(objectPath);
        if (name.Length == 0) return null;
        string materialPath = FindPath(name, "Material", folder);
        if (materialPath != null) return AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        string shaderPath = FindPath(name, "Shader", folder);
        if (shaderPath == null) return null;
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        if (shader == null) return null;
        string dir = Path.GetDirectoryName(shaderPath).Replace('\\', '/');
        Material material = new Material(shader);
        AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".mat"));
        ApplyTextures(material, name, dir);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void ApplyTextures(Material material, string name, string dir)
    {
        TextAsset list = null;
        foreach (string guid in AssetDatabase.FindAssets(name + " t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith("/" + name + ".textures.json")) continue;
            list = AssetDatabase.LoadAssetAtPath<TextAsset>(p);
            if (p.StartsWith(dir + "/")) break;
        }
        if (list == null)
        {
            Debug.LogWarning("Porter: " + name + ".textures.json was not found, so " + name + " has no textures assigned");
            return;
        }
        TexManifest manifest = JsonUtility.FromJson<TexManifest>(list.text);
        if (manifest == null || manifest.textureList == null) return;
        foreach (TexEntry e in manifest.textureList)
        {
            string texPath = FindPath(e.name, "Texture2D", dir);
            if (texPath == null)
            {
                Debug.LogWarning("Porter: texture not found in project: " + e.name);
                continue;
            }
            TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;
                TextureImporterType want = e.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (importer.textureType != want) { importer.textureType = want; changed = true; }
                if (!e.normal && e.srgb >= 0 && importer.sRGBTexture != (e.srgb == 1)) { importer.sRGBTexture = e.srgb == 1; changed = true; }
                if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; changed = true; }
                if (changed) importer.SaveAndReimport();
            }
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (texture != null && material.HasProperty(e.prop)) material.SetTexture(e.prop, texture);
        }
    }
}

public class PorterParticleAuto : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string p in imported)
        {
            if (p.EndsWith(".particles.json"))
            {
                Schedule();
                return;
            }
        }
    }

    [InitializeOnLoadMethod]
    static void Schedule()
    {
        EditorApplication.delayCall += Scan;
    }

    [MenuItem("Tools/Porter Build Particle Prefabs")]
    static void BuildAll()
    {
        int made = 0;
        foreach (string guid in AssetDatabase.FindAssets("particles t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".particles.json")) continue;
            if (PorterParticleSetup.BuildPrefab(p, false) != null) made++;
        }
        Debug.Log("Porter: built " + made + " new particle prefab(s)");
    }

    static void Scan()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Scan;
            return;
        }
        foreach (string guid in AssetDatabase.FindAssets("particles t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith(".particles.json")) continue;
            string key = "PorterParticles:" + p;
            if (SessionState.GetBool(key, false)) continue;
            SessionState.SetBool(key, true);
            try
            {
                PorterParticleSetup.BuildPrefab(p, false);
            }
            catch (Exception ex)
            {
                Debug.LogError("Porter: could not build " + p + ": " + ex.Message);
            }
        }
    }
}
'''


# ---------------------------------------------------------------- the run

def convert(exports, name, source, out, seconds):
    asset = next((e for e in exports if e["type"] == "NiagaraSystem"), None)
    if asset is None:
        kinds = sorted({e["type"] for e in exports})
        sys.exit("%s is not a Niagara system (it holds: %s)" % (source, ", ".join(kinds[:8])))
    print("playing %s for %.0f seconds" % (name, seconds))
    system, recorders = play(exports, {}, seconds)
    skipped = [{"emitter": n, "why": why} for n, why in system.skipped]
    gates = find_gates(exports, system, recorders, min(seconds, 3.0))
    gated = {}
    if gates:
        params, woke = gates
        print("emitters that needed user parameters: %s need %s" % (", ".join(woke), ", ".join(params)))
        gsys, grec = play(exports, {n: [1.0] for n in params}, seconds)
        for n in woke:
            recorders[n] = grec[n]
            gated[n] = params
    approximate = set(system.approximate)
    emitters = []
    for e in system.emitters:
        source_emitter = next((x for x in gsys.emitters if x.name == e.name), e) if e.name in gated else e
        emitters.append(summarize(system, source_emitter, recorders[e.name], gated.get(e.name, []), approximate))
    warnings = []
    for em in emitters:
        warnings += warnings_for(em)
    for s in skipped:
        warnings.append("%s: not converted (%s)" % (s["emitter"], s["why"]))
    bones = sorted(system.reads)
    users = []
    for n, t, v in system.users():
        users.append({"name": n, "type": t, "default": str(v)})
    manifest = {"version": 1, "system": name, "source": source, "seconds": seconds, "bones": bones,
                "userParameters": users, "skipped": skipped, "warnings": warnings, "emitters": emitters}
    folder = os.path.join(out, name)
    os.makedirs(folder, exist_ok=True)
    os.makedirs(os.path.join(out, "Editor"), exist_ok=True)
    json.dump(manifest, open(os.path.join(folder, name + ".particles.json"), "w", encoding="utf-8"), indent=2)
    open(os.path.join(out, "Editor", "PorterParticleSetup.cs"), "w", encoding="utf-8").write(PARTICLE_CS)
    report(emitters, skipped, bones)
    materials = []
    for em in emitters:
        for r in em["renderers"]:
            for m in ([r["material"]] + r["materials"]):
                if m and m not in materials:
                    materials.append(m)
    for m in materials:
        print("material: %s" % m)
    meshes = []
    for em in emitters:
        for r in em["renderers"]:
            if r["kind"] == "Mesh" and r["enabled"] and r["mesh"] and r["mesh"] not in meshes:
                meshes.append(r["mesh"])
    for m in meshes:
        print("mesh: %s" % m)
    print("wrote %s.particles.json (%d emitters, %d warnings)" % (name, len(emitters), len(warnings)))
    print("folder: %s" % os.path.abspath(folder))
    for w in warnings:
        print("  warning:", w)
    return materials


def report(emitters, skipped, bones):
    for em in emitters:
        when = []
        for b in em["bursts"]:
            again = " every %.2fs" % em["loopPeriod"] if em["loopPeriod"] and b["time"] == 0.0 and b is em["bursts"][0] else ""
            when.append("burst of %d at %.2fs%s" % (b["count"], b["time"], again))
        if em["rate"]:
            when.append("%.2f per second" % em["rate"])
        if not when:
            when.append("nothing spawned")
        life = "stays alive" if em["persistent"] else "lives %.2f-%.2fs" % (em["lifetimeMin"], em["lifetimeMax"])
        kinds = ", ".join(sorted({r["kind"] for r in em["renderers"]})) or "no renderer"
        print("  %s: %s; %s; up to %d alive; %s" % (em["name"], " + ".join(when), life, em["maxAlive"], kinds))
    for s in skipped:
        print("  %s: left out (%s)" % (s["emitter"], s["why"]))
    if bones:
        print("  bones and sockets the effect reads: %s" % ", ".join(bones))


def run(bridge, path, out, name, seconds):
    source = path
    name = name or re.sub(r"\.[^./]*$", "", re.split(r"[\\/]", source.rstrip("/\\"))[-1])
    try:
        dump = fetch_dump(bridge, source)
    except RuntimeError as e:
        sys.exit(str(e))
    convert(to_exports(dump), name, source, out, seconds)


def main():
    ap = argparse.ArgumentParser(prog=APP_NAME)
    ap.add_argument("--bridge", default=DEFAULT_BRIDGE)
    ap.add_argument("--system", help="object path of the Niagara system, e.g. /Game/.../NS_Something")
    ap.add_argument("--file", help="a Niagara system's exports saved as JSON, instead of asking the app")
    ap.add_argument("--out", default=".")
    ap.add_argument("--seconds", type=float, default=6.0)
    a = ap.parse_args()
    if not a.system and not a.file:
        sys.exit("give --system or --file")
    source = a.system or a.file
    name = re.sub(r"\.[^./]*$", "", re.split(r"[\\/]", source.rstrip("/\\"))[-1])
    try:
        dump = json.loads(open(a.file, "rb").read().decode("utf-8-sig")) if a.file else fetch_dump(a.bridge, a.system)
    except RuntimeError as e:
        sys.exit(str(e))
    try:
        convert(to_exports(dump), name, source, a.out, a.seconds)
    except SystemExit:
        raise
    except Exception as e:
        import traceback
        traceback.print_exc()
        sys.exit("could not play %s: %s: %s" % (name, type(e).__name__, e))


if __name__ == "__main__":
    try:
        main()
    except SystemExit as e:
        if e.code not in (None, 0):
            print(e.code if isinstance(e.code, str) else "failed")
            sys.exit(1)
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
