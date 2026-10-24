import argparse
import json
import math
import os
import re
import shutil
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
STREAM_POINTS = 16
STREAM_MIN_PARTICLES = 8    # complete particles wanted behind a colour or Dynamic Parameter curve
STREAM_MAX_SECONDS = 30.0   # the longest replay made only to read those curves
BAKE_FRAMES = False         # keep every tick's particles so ParticleBake can write the replay files
BAKE_TAIL = float(os.environ.get("PORTER_BAKE_TAIL", "4.0"))    # seconds recorded past the replay, so particles born near its end live out their life
BAKE_MOST_ROWS = 400000     # particles over all ticks, per emitter; past that its replay is not written
LAST_TRACKS = {}            # emitter name: (recorder, emitter) from the last replay()
BINDINGS = (("ColorBinding", "Color"), ("DynamicMaterialBinding", "DynamicMaterialParameter"),
            ("DynamicMaterial1Binding", "DynamicMaterialParameter1"), ("DynamicMaterial2Binding", "DynamicMaterialParameter2"),
            ("DynamicMaterial3Binding", "DynamicMaterialParameter3"))


def renderer_bindings(props):
    """The particle attributes a renderer reads: (colour attribute, [dynamic parameter 0..3 attributes])."""
    names = []
    for key, default in BINDINGS:
        b = props.get(key)
        names.append((b.get("RootName") if isinstance(b, dict) else None) or default)
    return names[0], names[1:]


def tracked_names(system, emitter):
    found = []
    for rtype, props in renderer_exports(system, emitter):
        if "Mesh" not in rtype and "Sprite" not in rtype:
            continue
        color, dps = renderer_bindings(props)
        for n in [color] + dps:
            if n not in found:
                found.append(n)
    return found


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

    def __init__(self, track=()):
        self.track = list(track)
        self.tracked = {}       # attribute: {particle: [(normalised age, components)]}
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
        self.frames = []        # every tick's particles as the emitter holds them: (floats, ints), for the baked replay
        self.frame_rows = 0     # particles over all of those ticks
        self.layout = None      # the emitter's data layout: where each attribute sits in those frames
        self.truncated = False  # the frames stopped at the limit and do not hold the whole replay (no longer set: see step)
        self.step = 1           # one stored tick in every `step` ticks; doubles instead of dropping the replay when it grows too long
        self.tickno = 0
        self.window = None      # seconds of the replay that count as spawning time; what comes after is only the tail

    def tick(self, e, t0):
        count = int(e.data.count)
        if self.layout is None:
            self.layout = e.layout
        if BAKE_FRAMES and not self.truncated:
            if self.tickno % self.step == 0:
                self.frames.append((e.data.floats[:, :count].copy(), e.data.ints[:, :count].copy()))
                self.frame_rows += count
                # too many particle-ticks: keep every second stored tick (the replay's tick length doubles) rather than lose the renderer
                while self.frame_rows > BAKE_MOST_ROWS and len(self.frames) > 4:
                    self.frames = self.frames[::2]
                    self.step *= 2
                    self.frame_rows = sum(int(f[0].shape[1] if f[0].shape[0] else f[1].shape[1]) for f in self.frames)
            self.tickno += 1
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
        norm = age / np.where(ok, life, 1.0)
        if self.track:
            ids = e.attribute("ID")
            if ids is None:
                ids = e.attribute("UniqueID")
            for name in self.track:
                a = e.attribute(name)
                if a is None or ids is None or a.shape[0] == 0:
                    continue
                a = np.array(a[:, :count], np.float64)
                book = self.tracked.setdefault(name, {})
                second = ids.shape[0] > 1
                for j in np.nonzero(ok)[0]:
                    key = (int(ids[0, j]), int(ids[1, j]) if second else 0)
                    book.setdefault(key, []).append((float(norm[j]), a[:, j]))
        self.ages.append(norm[ok])
        self.colors.append(None if rows["Color"] is None else rows["Color"][:, ok])
        self.sizes.append(None if size is None else size[:, ok])


def play(exports, user, seconds, seed=1):
    system = niagara.System(exports, seed=seed, user=user)
    recorders = {}
    for i in range(int(round(seconds / DT))):
        system.tick(DT)
        for e in system.emitters:
            if e.name not in recorders:
                recorders[e.name] = Recorder(tracked_names(system, e))
            recorders[e.name].tick(e, i * DT)
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


def stream_curves(r):
    """Each tracked attribute over normalised age on STREAM_POINTS points: the smallest, largest and mean value of
    every component across the particles, each particle's own samples interpolated onto the points."""
    out = {}
    r.incomplete = []
    grid = np.linspace(0.0, 1.0, STREAM_POINTS)
    for name, book in r.tracked.items():
        rows = []
        partial = []
        for samples in book.values():
            if len(samples) < 2:
                continue
            samples = sorted(samples, key=lambda x: x[0])
            ages = np.array([x[0] for x in samples])
            vals = np.array([x[1] for x in samples])
            if samples[-1][0] < 0.9:
                partial.append(vals)
                continue
            rows.append(np.stack([np.interp(grid, ages, vals[:, c]) for c in range(vals.shape[1])], axis=1))
        if not rows and partial:
            if all(float(np.max(np.abs(v - v[0]))) < 1e-9 for v in partial):
                rows = [np.tile(v[0], (STREAM_POINTS, 1)) for v in partial]
            else:
                r.incomplete.append(name)
        if not rows:
            continue
        cube = np.stack(rows)
        out[name] = {"width": int(cube.shape[2]), "particles": int(cube.shape[0]),
                     "min": cube.min(axis=0).tolist(), "max": cube.max(axis=0).tolist(), "mean": cube.mean(axis=0).tolist()}
    return out


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


def renderer_exports(system, emitter):
    """The properties of each renderer an emitter draws with: [(type name, properties)]."""
    version = getattr(emitter, "version", None)
    if version is None:
        version = (getattr(emitter, "export", None) or {}).get("props") or {}
    found = []
    for ref in version.get("RendererProperties") or []:
        export = system.export(ref)
        if export is not None:
            found.append((export["type"], export["props"]))
    return found


def euler_from_quaternion(q):
    """Quaternions (x, y, z, w; shape (4, n)) as Unreal rotators: (roll, pitch, yaw) in degrees, shape (3, n)."""
    x, y, z, w = (np.asarray(q[i], np.float64) for i in range(4))
    norm = np.sqrt(x * x + y * y + z * z + w * w)
    norm = np.where(norm > 0, norm, 1.0)
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    m20 = 2 * (x * z - w * y)
    m10 = 2 * (x * y + w * z)
    m00 = 1 - 2 * (y * y + z * z)
    m21 = 2 * (y * z + w * x)
    m22 = 1 - 2 * (x * x + y * y)
    pitch = np.arcsin(np.clip(m20, -1.0, 1.0))
    yaw = np.arctan2(m10, m00)
    roll = np.arctan2(-m21, m22)
    return np.degrees(np.stack([roll, pitch, yaw]))


def initial_ranges(r):
    """What each particle was born with, as the smallest and largest value of every component: position and
    velocity in cm and cm/s, sprite rotation in degrees, mesh orientation as roll, pitch and yaw in degrees."""
    out = {}
    for name, parts in r.initial.items():
        rows = np.hstack(parts)
        if not rows.shape[1]:
            continue
        if name == "MeshOrientation":
            if rows.shape[0] < 4:
                continue
            rows = euler_from_quaternion(rows)
        out[name] = {"min": [r4(v) for v in rows.min(axis=1)], "max": [r4(v) for v in rows.max(axis=1)]}
    return out


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
        "initial": initial_ranges(r),
        "streams": stream_curves(r),
        "incompleteStreams": list(getattr(r, "incomplete", [])),
        "noAge": bool(r.total and not r.ages),
        "renderers": renderers(system, e),
    }
    return out


# ---------------------------------------------------------------- the Unity script


# ---------------------------------------------------------------- the run

def replay(exports, name, source, seconds):
    asset = next((e for e in exports if e["type"] == "NiagaraSystem"), None)
    if asset is None:
        kinds = sorted({e["type"] for e in exports})
        sys.exit("%s is not a Niagara system (it holds: %s)" % (source, ", ".join(kinds[:8])))
    total = seconds + (BAKE_TAIL if BAKE_FRAMES else 0.0)
    print("playing %s for %.0f seconds%s" % (name, seconds, (" (+%.0f s tail for the baked replay)" % BAKE_TAIL) if total > seconds else ""))
    LAST_TRACKS.clear()
    system, recorders = play(exports, {}, total)
    for r in recorders.values():
        r.window = seconds
    skipped = [{"emitter": n, "why": why} for n, why in system.skipped]
    gates = find_gates(exports, system, recorders, min(seconds, 3.0))
    gated = {}
    if gates:
        params, woke = gates
        print("emitters that needed user parameters: %s need %s" % (", ".join(woke), ", ".join(params)))
        gsys, grec = play(exports, {n: [1.0] for n in params}, total)
        for r in grec.values():
            r.window = seconds
        for n in woke:
            recorders[n] = grec[n]
            gated[n] = params
    approximate = set(system.approximate)
    emitters = []
    for e in system.emitters:
        source_emitter = next((x for x in gsys.emitters if x.name == e.name), e) if e.name in gated else e
        emitters.append(summarize(system, source_emitter, recorders[e.name], gated.get(e.name, []), approximate))
        LAST_TRACKS[e.name] = (recorders[e.name], source_emitter)
    for em in emitters:
        em["streamSeconds"] = r4(seconds)
    extend_streams(exports, emitters, recorders, gated, seconds)
    return system, emitters, skipped


def extend_streams(exports, emitters, recorders, gated, seconds):
    """Colour and Dynamic Parameter curves need particles that lived out their whole life. Where too few did in the
    replay, play the system again for longer, only to read those curves. The baked replay keeps its own length."""
    need = []
    for em in emitters:
        r = recorders.get(em["name"])
        if r is None or not r.tracked or em["persistent"] or em["lifetimeMax"] <= 0:
            continue
        counts = [s["particles"] for s in em["streams"].values()]
        if em["incompleteStreams"] or not counts or min(counts) < STREAM_MIN_PARTICLES:
            need.append(em)
    if not need:
        return
    longest = max(em["lifetimeMax"] for em in need)
    longer = min(STREAM_MAX_SECONDS, longest + 2.0)
    if any(em["incompleteStreams"] for em in need):
        longer = min(STREAM_MAX_SECONDS, max(longer, seconds + longest + 1.0))
    if longer <= seconds + 0.25:
        return
    print("playing %s again for %.0f seconds to read curves over age" % (", ".join(em["name"] for em in need), longer))
    global BAKE_FRAMES
    saved = BAKE_FRAMES
    BAKE_FRAMES = False
    try:
        grouped = {}
        for em in need:
            key = tuple(gated.get(em["name"], []))
            if key not in grouped:
                _, grouped[key] = play(exports, {n: [1.0] for n in key}, longer)
            rec = grouped[key].get(em["name"])
            if rec is None:
                continue
            em["streams"] = stream_curves(rec)
            em["incompleteStreams"] = list(getattr(rec, "incomplete", []))
            em["streamSeconds"] = r4(longer)
    finally:
        BAKE_FRAMES = saved
