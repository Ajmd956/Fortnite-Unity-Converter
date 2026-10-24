"""Baked particle replay: what the Niagara replay did, tick by tick, written as a binary file per renderer
for the Unity player (unity/PorterParticleReplay.cs) to draw. Format PRB2 (PRB1 is the same without the track fields), little endian:

    magic 'PRB2' | int32 frames | int32 stride (floats per particle row) | float32 tick seconds | int32 tracks | float32 cycle seconds
    int32 count[frames]                      particles in each tick
    float32 rows[sum(count)][stride]         tick after tick
    int32 track[sum(count)]                  the track (one particle's whole life) each row belongs to

A track's rows are in consecutive ticks, its first tick being when the particle was born. The player spawns tracks at their birth
times, again every `cycle` seconds when it loops, so an effect can play forever, be prewarmed, thinned or thickened.

Field order, per kind, is in the spec the bake returns ("fieldNames" with "fieldWidths"). Mesh rows carry rotation, scale and velocity;
sprite rows facing, alignment and sub-image; ribbon rows their ribbon, rank and UVs. Positions are
Unity metres (UE x, y, z in cm becomes x, z, y / 100), sizes and radii in metres, rotations Unity quaternions.
"""
import math
import os
import struct

import numpy as np

KINDS = ("Sprite", "Ribbon", "Light", "Decal", "Mesh")
MAGIC = b"PRB1"
DEFAULTS = {"Position": "Position", "Color": "Color", "NormalizedAge": "NormalizedAge", "RibbonWidth": "RibbonWidth",
            "RibbonUVDistance": "RibbonUVDistance", "VisibilityTag": "VisibilityTag"}
DP_NAMES = ("DynamicMaterialParameter", "DynamicMaterialParameter1", "DynamicMaterialParameter2", "DynamicMaterialParameter3")
DECAL_DOWN = (-0.5, 0.5, 0.5, 0.5)       # x, y, z, w
DP_KEYS = ("DynamicMaterialBinding", "DynamicMaterial1Binding", "DynamicMaterial2Binding", "DynamicMaterial3Binding")


def mode(default="auto"):
    """PORTER_BAKE: auto bakes what the Particle System cannot draw, all bakes every sprite too, off bakes nothing.
    `default` is what is used when PORTER_BAKE is not set (the Particle Converter asks for "all")."""
    m = os.environ.get("PORTER_BAKE", default).strip().lower()
    return m if m in ("auto", "all", "off") else default


def bound(props, key, default):
    b = props.get(key)
    return (b.get("RootName") if isinstance(b, dict) else None) or default


class Frames:
    """Reads named attributes out of the recorded ticks."""

    def __init__(self, recorder):
        self.rec, self.layout = recorder, recorder.layout

    def has(self, name):
        return self.layout is not None and name in self.layout.vars and self.layout.vars[name] is not None

    def get(self, name, floats, ints, count, width=None):
        """(components, count) float array, or zeros when the emitter has no such attribute."""
        v = self.layout.vars.get(name) if self.layout is not None else None
        if v is None:
            return np.zeros((width or 1, count))
        _, f0, i0, nf, ni = v
        if nf:
            return np.array(floats[f0:f0 + nf, :count], np.float64)
        return np.array(ints[i0:i0 + ni, :count], np.float64)


def ue_to_unity(p):
    """(3, n) UE centimetres to Unity metres."""
    return np.vstack([p[0], p[2], p[1]]) / 100.0


def quat_to_unity(q):
    """(4, n) UE quaternions (x, y, z, w) to Unity quaternions via the basis swap Y<->Z (columns Ux=swap(R ex), Uy=swap(R ez), Uz=swap(R ey))."""
    n = q.shape[1]
    x, y, z, w = q
    norm = np.sqrt(x * x + y * y + z * z + w * w)
    norm[norm == 0] = 1.0
    x, y, z, w = x / norm, y / norm, z / norm, w / norm
    R = np.empty((n, 3, 3))
    R[:, 0, 0] = 1 - 2 * (y * y + z * z); R[:, 0, 1] = 2 * (x * y - z * w); R[:, 0, 2] = 2 * (x * z + y * w)
    R[:, 1, 0] = 2 * (x * y + z * w); R[:, 1, 1] = 1 - 2 * (x * x + z * z); R[:, 1, 2] = 2 * (y * z - x * w)
    R[:, 2, 0] = 2 * (x * z - y * w); R[:, 2, 1] = 2 * (y * z + x * w); R[:, 2, 2] = 1 - 2 * (x * x + y * y)
    swap = lambda c: np.stack([c[:, 0], c[:, 2], c[:, 1]], axis=1)
    ux, uy, uz = swap(R[:, :, 0]), swap(R[:, :, 2]), swap(R[:, :, 1])
    M = np.stack([ux, uy, uz], axis=2)          # columns
    out = np.zeros((n, 4))
    t = M[:, 0, 0] + M[:, 1, 1] + M[:, 2, 2]
    for i in range(n):
        m = M[i]
        if t[i] > 0:
            s = np.sqrt(t[i] + 1) * 2
            out[i] = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
        elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
            s = np.sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
            out[i] = [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
        elif m[1, 1] > m[2, 2]:
            s = np.sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
            out[i] = [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
        else:
            s = np.sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
            out[i] = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]
    return out.T


def dp_components(props):
    """[(attribute name, component)] for each Dynamic Parameter component the material reads (MaterialParamValidMask bits)."""
    mask = int(props.get("MaterialParamValidMask", 0) or 0)
    found = []
    for bit in range(16):
        if mask & (1 << bit):
            found.append((bound(props, DP_KEYS[bit // 4], DP_NAMES[bit // 4]), bit % 4, bit))
    return found


def swap(v):
    """(3, n) UE direction/scale to Unity axes (x, z, y), no unit change."""
    return np.vstack([v[0], v[2], v[1]])


def ribbon_extra(props, fr, floats, ints, count):
    """What each ribbon point needs to be strung as the Blender add-on strings it: its ribbon, its rank in the frame (ribbon,
    then link order: the emitter's own, else the particle's age), and each of its two UV sets (U along the ribbon by the renderer's
    distribution mode, V at the two edges): UV0 as u, v0, v1 and UV1 as u1, v10, v11."""
    ribbon = fr.get(bound(props, "RibbonIdBinding", "RibbonID"), floats, ints, count)[0].astype(np.int64)
    link = bound(props, "RibbonLinkOrderBinding", "RibbonLinkOrder")
    order = fr.get(link if fr.has(link) else bound(props, "NormalizedAgeBinding", "NormalizedAge"), floats, ints, count)[0]
    pos = fr.get(bound(props, "PositionBinding", "Position"), floats, ints, count, 3)[:3].T.astype(np.float64)
    sort = np.lexsort((order, ribbon))
    r, p = ribbon[sort], pos[sort]
    first = np.ones(count, bool)
    first[1:] = r[1:] != r[:-1]
    step = np.zeros(count)
    step[1:] = np.linalg.norm(p[1:] - p[:-1], axis=1)
    step[first] = 0.0
    run = np.cumsum(first) - 1
    along = np.cumsum(step)
    along = along - along[first][run]
    runs = np.bincount(run)
    index = np.arange(count) - np.nonzero(first)[0][run]
    length = np.zeros(len(runs))
    np.maximum.at(length, run, along)
    cnt, ln = runs[run], length[run]
    rank = np.zeros(count)
    rank[sort] = np.arange(count)
    out = [("ribbon", ribbon.astype(np.float64)[None, :]), ("rank", rank[None, :])]
    for which, names in ((0, ("u", "v0", "v1")), (1, ("u1", "v10", "v11"))):
        settings = props.get("UV%dSettings" % which) or {}
        mode = str(settings.get("DistributionMode", "ScaledUsingRibbonSegmentLength"))
        tiling = float(settings.get("TilingLength", 100.0)) or 100.0
        dist = bound(props, "RibbonUVDistance", "RibbonUVDistance")
        with np.errstate(all="ignore"):
            if "TiledFromStart" in mode and fr.has(dist):
                u = fr.get(dist, floats, ints, count)[0][sort] / tiling
            elif "Tiled" in mode:
                u = along / tiling
            elif "ScaledUniformly" in mode:
                u = np.where(cnt > 1, index / np.maximum(cnt - 1, 1), 0.0)
            else:
                u = np.where(ln > 0, along / np.maximum(ln, 1e-9), 0.0)
        scale, offset = settings.get("Scale") or {}, settings.get("Offset") or {}
        sx, sy, ox, oy = float(scale.get("X", 1.0)), float(scale.get("Y", 1.0)), float(offset.get("X", 0.0)), float(offset.get("Y", 0.0))
        out_u = np.zeros(count)
        out_u[sort] = np.nan_to_num(u) * sx + ox
        v_lo, v_hi = np.full(count, oy), np.full(count, sy + oy)
        # the emitter's own U and V range, where the renderer takes it from the particle
        given = bound(props, "U%dOverrideBinding" % which, "U%dOverride" % which)
        if settings.get("bEnablePerParticleUOverride") and fr.has(given):
            out_u = fr.get(given, floats, ints, count)[0]
        given = bound(props, "V%dRangeOverrideBinding" % which, "V%dRangeOverride" % which)
        if settings.get("bEnablePerParticleVRangeOverride") and fr.has(given):
            both = fr.get(given, floats, ints, count, 2)
            v_lo, v_hi = both[0], both[1] if both.shape[0] > 1 else both[0]
        out += [(names[0], out_u[None, :]), (names[1], v_lo[None, :]), (names[2], v_hi[None, :])]
    out.append(("side", swap(fr.get(bound(props, "RibbonFacingBinding", "RibbonFacing"), floats, ints, count, 3)[:3])))
    return out


SORT_MODES = {"None": 0, "ViewDepth": 1, "ViewDistance": 2, "CustomAscending": 3, "CustomDecending": 4, "CustomDescending": 4}


def rotator_to_quat(pitch, yaw, roll):
    """An Unreal FRotator (degrees) as the quaternion (x, y, z, w) the engine builds from it."""
    sp, sy, sr = (math.sin(math.radians(a) / 2.0) for a in (pitch, yaw, roll))
    cp, cy, cr = (math.cos(math.radians(a) / 2.0) for a in (pitch, yaw, roll))
    return (cr * sp * sy - sr * cp * cy, -cr * sp * cy - sr * cp * sy, cr * cp * sy - sr * sp * cy, cr * cp * cy + sr * sp * sy)


def mesh_entries(props):
    """Each mesh of a mesh renderer with its own rotation and scale (the engine applies both to the mesh before the particle's own
    orientation and scale), as a Unity quaternion and Unity-axis scale."""
    out = []
    for m in props.get("Meshes") or []:
        if not m or not m.get("Mesh"):
            continue
        r = m.get("Rotation") or {}
        q = quat_to_unity(np.array(rotator_to_quat(float(r.get("Pitch", 0.0)), float(r.get("Yaw", 0.0)), float(r.get("Roll", 0.0))), np.float64)[:, None])[:, 0]
        s = m.get("Scale") or {}
        p = m.get("PivotOffset") or {}
        out.append({"rotation": [float(v) for v in q], "scale": [float(s.get("X", 1.0)), float(s.get("Z", 1.0)), float(s.get("Y", 1.0))],
                    "pivot": [float(p.get("X", 0.0)) / 100.0, float(p.get("Z", 0.0)) / 100.0, float(p.get("Y", 0.0)) / 100.0]})
    return out


def sort_mode(props):
    return SORT_MODES.get(str(props.get("SortMode") or "None").split("::")[-1], 0)


def spec_extras(kind, props):
    """What the player needs to know about a renderer that is not in its rows."""
    out = {}
    if kind in ("Sprite", "Mesh"):
        out["sortMode"] = sort_mode(props)
    if kind == "Sprite":
        facing = str(props.get("FacingMode") or "")
        out["facingMode"] = 1 if "CustomFacing" in facing else 2 if "FaceCameraPosition" in facing else 0
        align = str(props.get("Alignment") or "")
        out["lengthwise"] = "VelocityAligned" in align or "CustomAlignment" in align
        pivot = props.get("PivotInUVSpace") or {}
        out["pivotX"], out["pivotY"] = float(pivot.get("X", 0.5)), float(pivot.get("Y", 0.5))
        sub = props.get("SubImageSize") or {}
        out["subX"], out["subY"] = max(1, int(round(float(sub.get("X", 1))))), max(1, int(round(float(sub.get("Y", 1)))))
    elif kind == "Mesh":
        facing = str(props.get("FacingMode") or "")
        out["facingMode"] = 1 if "Velocity" in facing else 2 if "Camera" in facing else 0
        out["meshEntries"] = mesh_entries(props)
    elif kind == "Ribbon":
        # what the ribbon's width runs across: 0 the view, 1 the particles' own facing, 2 their side vector (a trail between two sockets)
        facing = str(props.get("FacingMode") or "")
        out["facingMode"] = 2 if "CustomSideVector" in facing else 1 if "Custom" in facing else 0
        shape = str(props.get("Shape") or "")
        out["shape"] = 1 if "MultiPlane" in shape else 2 if "Tube" in shape else 0       # a plane, several planes turned about it, a tube
        out["sides"] = max(1, int(props.get("TubeSubdivisions", 3))) if "Tube" in shape else max(1, int(props.get("MultiPlaneCount", 2)))
    return out


def needs_bake(kind, props, bake_mode):
    """(required, why). Ribbons, lights and decals cannot be Particle System renderers; a sprite needs it past 4 Dynamic Parameter values."""
    if bake_mode == "off":
        return False, "PORTER_BAKE=off"
    if kind in ("Ribbon", "Light", "Decal"):
        return True, kind.lower() + " renderer: the Particle System cannot draw it"
    dps = dp_components(props)
    if bake_mode == "all":
        return True, "PORTER_BAKE=all"
    if len(dps) > 4:
        return True, "reads %d Dynamic Parameter values, a Particle System carries 4" % len(dps)
    return False, ""


def system_enabled(system, emitter_name, props):
    """Whether the renderer's Renderer Enabled binding, where it names a system, emitter or user value, is on as the replay left it
    (a variant's System.IsGold, a User parameter); a binding to a particle attribute is the per-particle mask's, and one bound to nothing draws."""
    b = props.get("RendererEnabledBinding") or {}
    name = str(b.get("DataSetName") or (b.get("ParamMapVariable") or {}).get("Name") or b.get("RootName") or "")
    if not name or name == "None" or system is None:
        return True
    if name.startswith("Emitter."):
        name = emitter_name + name[len("Emitter"):]
    try:
        value = system.read(name)
        if value is None and name in system.user.offsets:
            import numpy as _np
            value = _np.frombuffer(system.user.raw(name)[:4], _np.int32)
    except Exception:
        return True
    if value is None or not len(value):
        return True
    return bool(int(np.asarray(value).view(np.int32)[0]) != 0)


def keep_mask(fr, props, floats, ints, count):
    """Which particles this renderer draws: its RendererVisibility against the particle's visibility tag, and its enabled flags."""
    keep = np.ones(count, bool)
    vis = int(props.get("RendererVisibility", 0) or 0)
    tag = bound(props, "RendererVisibilityTagBinding", "VisibilityTag")
    if fr.has(tag):
        # (an emitter with several renderers tags each particle with the renderer that draws it, the first renderer's being 0)
        keep &= fr.get(tag, floats, ints, count)[0].astype(np.int64) == vis
    for key in ("RendererEnabledBinding", "LightRenderingEnabledBinding", "DecalVisibleBinding"):
        b = props.get(key)
        name = b.get("RootName") if isinstance(b, dict) else None
        if name and fr.has(name):
            keep &= fr.get(name, floats, ints, count)[0] != 0
    return keep


def row_fields(kind, fr, props, floats, ints, count):
    """[(field name, (width, count) array)] for one tick of one renderer."""
    pos = ue_to_unity(fr.get(bound(props, "PositionBinding", "Position"), floats, ints, count, 3)[:3])
    ids = fr.get("UniqueID", floats, ints, count) if fr.has("UniqueID") else fr.get("ID", floats, ints, count)[:1]
    col = fr.get(bound(props, "ColorBinding", "Color"), floats, ints, count, 4)
    if col.shape[0] < 4:
        col = np.vstack([col, np.ones((4 - col.shape[0], count))])
    out = [("id", ids[:1]), ("pos", pos)]
    if kind == "Ribbon":
        out += [("age", fr.get(bound(props, "NormalizedAgeBinding", "NormalizedAge"), floats, ints, count)[:1]),
                ("width", fr.get(bound(props, "RibbonWidthBinding", "RibbonWidth"), floats, ints, count)[:1] / 100.0),
                ("color", col[:4]),
                ("uvDistance", fr.get(bound(props, "RibbonUVDistance", "RibbonUVDistance"), floats, ints, count)[:1] / 100.0)]
    elif kind == "Light":
        rad = fr.get(bound(props, "RadiusBinding", "LightRadius"), floats, ints, count)[:1] / 100.0
        exp = fr.get(bound(props, "LightExponentBinding", "LightExponent"), floats, ints, count)[:1]
        rgb = col[:3] * (col[3:4] if props.get("bAlphaScalesBrightness") else 1.0)
        add = props.get("ColorAdd") or {}
        rgb = np.maximum(rgb + np.array([[add.get("X", 0.0)], [add.get("Y", 0.0)], [add.get("Z", 0.0)]]), 0.0)
        out += [("color", np.vstack([rgb, np.ones((1, count))])), ("radius", rad), ("exponent", exp)]
    elif kind == "Decal":
        dq = bound(props, "DecalOrientationBinding", "DecalOrientation")
        # without an orientation of its own a decal projects straight down (the engine's FRotator(-90, 0, 90))
        q = fr.get(dq, floats, ints, count, 4) if fr.has(dq) else np.tile(np.array(DECAL_DOWN)[:, None], (1, count))
        size = fr.get(bound(props, "DecalSizeBinding", "DecalSize"), floats, ints, count, 3)
        if size.shape[0] < 3:
            size = np.vstack([size, np.ones((3 - size.shape[0], count)) * size[-1:]])
        dcol = fr.get(bound(props, "DecalColorBinding", "DecalColor"), floats, ints, count, 4)
        out += [("rotation", quat_to_unity(q[:4]) if q.shape[0] >= 4 else np.tile([[0], [0], [0], [1]], (1, count))),
                ("size", np.vstack([size[0], size[2], size[1]]) / 100.0),
                ("fade", fr.get(bound(props, "DecalFadeBinding", "DecalFade"), floats, ints, count)[:1]),
                ("color", dcol[:4] if dcol.shape[0] >= 4 else col[:4]),
                ("sortOrder", fr.get(bound(props, "DecalSortOrderBinding", "DecalSortOrder"), floats, ints, count)[:1])]
    elif kind == "Mesh":
        q = fr.get(bound(props, "MeshOrientationBinding", "MeshOrientation"), floats, ints, count, 4)
        scale = fr.get(bound(props, "ScaleBinding", "Scale"), floats, ints, count, 3) if fr.has(bound(props, "ScaleBinding", "Scale")) else np.ones((3, count))
        if scale.shape[0] < 3:
            scale = np.vstack([scale, np.ones((3 - scale.shape[0], count)) * scale[-1:]])
        out += [("rotation", quat_to_unity(q[:4]) if q.shape[0] >= 4 else np.tile([[0], [0], [0], [1]], (1, count))),
                ("scale", swap(scale[:3])), ("color", col[:4]),
                ("age", fr.get(bound(props, "NormalizedAgeBinding", "NormalizedAge"), floats, ints, count)[:1]),
                ("velocity", swap(fr.get(bound(props, "VelocityBinding", "Velocity"), floats, ints, count, 3)[:3])),
                ("meshIndex", fr.get(bound(props, "MeshIndexBinding", "MeshIndex"), floats, ints, count)[:1])]
    else:       # Sprite
        size = fr.get(bound(props, "SpriteSizeBinding", "SpriteSize"), floats, ints, count, 2)
        rot = fr.get(bound(props, "SpriteRotationBinding", "SpriteRotation"), floats, ints, count)[:1]
        out += [("size", size[:2] / 100.0), ("rotation", np.deg2rad(rot)), ("color", col[:4]),
                ("age", fr.get(bound(props, "NormalizedAgeBinding", "NormalizedAge"), floats, ints, count)[:1])]
    if kind == "Ribbon":
        out += ribbon_extra(props, fr, floats, ints, count)
    if kind == "Sprite":
        custom = "CustomAlignment" in str(props.get("Alignment") or "")
        along = bound(props, "SpriteAlignmentBinding", "SpriteAlignment") if custom else bound(props, "VelocityBinding", "Velocity")
        out += [("subimage", fr.get(bound(props, "SubImageIndexBinding", "SubImageIndex"), floats, ints, count)[:1]),
                ("facing", swap(fr.get(bound(props, "SpriteFacingBinding", "SpriteFacing"), floats, ints, count, 3)[:3])),
                ("align", swap(fr.get(along, floats, ints, count, 3)[:3]))]
    if kind in ("Sprite", "Mesh"):
        # how far it is drawn towards the camera from where it is (a glow out in front of what it sits in)
        out.append(("camOff", fr.get(bound(props, "CameraOffsetBinding", "CameraOffset"), floats, ints, count)[:1] / 100.0))
    if kind in ("Sprite", "Mesh") and sort_mode(props) >= 3:
        out.append(("sortKey", fr.get(bound(props, "CustomSortingBinding", "CustomSorting"), floats, ints, count)[:1]))
    if kind in ("Ribbon", "Sprite", "Mesh"):
        out.append(("random", fr.get(bound(props, "MaterialRandomBinding", "MaterialRandom"), floats, ints, count)[:1]))
    dps = dp_components(props) if kind in ("Ribbon", "Sprite", "Mesh") else []
    for name, comp, bit in dps:
        a = fr.get(name, floats, ints, count, 4)
        out.append(("dp%d" % bit, a[comp:comp + 1] if a.shape[0] > comp else np.zeros((1, count))))
    return out


def particle_keys(fr, floats, ints, count):
    """One int64 per particle that is its identity for as long as it lives (the engine's ID is an index plus an acquire tag), or None."""
    for name in ("UniqueID", "ID"):
        if fr.has(name):
            a = fr.get(name, floats, ints, count)
            if a.shape[0] == 0:
                continue
            a = a.astype(np.int64)
            return a[0] * 1000003 + (a[1] if a.shape[0] > 1 else 0)
    return None


def build_tracks(frame_keys, dt, window, cycle):
    """Give every particle's life one track number. Returns (track per row per frame, tracks, cycle seconds), the tracks born at or
    after the cycle (or the replay's spawn window) left out; or None when particles cannot be told apart."""
    last, spawn, per_frame = {}, [], []
    for fi, keys in enumerate(frame_keys):
        t = np.empty(len(keys), np.int64)
        seen = set()
        for j, k in enumerate(keys.tolist()):
            if k in seen:
                return None
            seen.add(k)
            e = last.get(k)
            if e is not None and e[1] == fi - 1:
                tr = e[0]
            else:
                tr = len(spawn)
                spawn.append(fi)
            last[k] = (tr, fi)
            t[j] = tr
        per_frame.append(t)
    if not spawn:
        return None
    total = len(frame_keys) * dt
    window = float(window) if window else total
    cycle = float(cycle) if cycle and 0.0 < cycle <= window else window
    born = np.array(spawn, np.float64) * dt
    keep = born < cycle - 1e-9
    remap = np.full(len(spawn), -1, np.int64)
    remap[keep] = np.arange(int(keep.sum()))
    out = [remap[t] if len(t) else t for t in per_frame]
    return out, int(keep.sum()), cycle


def bake_renderer(recorder, rtype, props, stem, notes, dt=1.0 / 60.0, bake_mode=None, meshes=False, system=None, emitter_name="", cycle=0.0):
    """Write one renderer's replay. Returns the spec dict ({file, blob, ...}) or None when it is not baked."""
    kind = rtype.replace("Niagara", "").replace("RendererProperties", "")
    if kind not in KINDS or not props.get("bIsEnabled", True) or (kind == "Mesh" and not meshes):
        return None
    bake_mode = bake_mode or mode()
    if not system_enabled(system, emitter_name, props):
        notes.append("%s: %s renderer is switched off by the system's own value, left out" % (stem, kind))
        return None
    required, why = needs_bake(kind, props, bake_mode)
    if not required:
        return None
    if recorder.truncated or not recorder.frames or recorder.layout is None:
        notes.append("%s: %s renderer not baked, the replay was cut off at %d particles" % (stem, kind, 400000) if recorder.truncated
                     else "%s: %s renderer not baked, the replay kept no particles" % (stem, kind))
        return None
    dt = dt * int(getattr(recorder, "step", 1) or 1)       # a long replay is stored every few ticks
    fr = Frames(recorder)
    counts, rows, fields, widths, keys = [], [], None, None, []
    for floats, ints in recorder.frames:
        n = floats.shape[1] if floats.shape[0] else ints.shape[1]
        if n == 0:
            counts.append(0)
            keys.append(np.zeros(0, np.int64))
            continue
        keep = keep_mask(fr, props, floats, ints, n)
        k_all = particle_keys(fr, floats, ints, n)
        keys.append(k_all[keep] if k_all is not None else None)
        fields_now = row_fields(kind, fr, props, floats, ints, n)
        if fields is None:
            fields = [f for f, _ in fields_now]
            widths = [a.shape[0] for _, a in fields_now]
        block = np.vstack([a for _, a in fields_now])[:, keep]
        counts.append(int(keep.sum()))
        if block.shape[1]:
            rows.append(block.T.astype(np.float32))
    if fields is None or not any(counts):
        notes.append("%s: %s renderer drew no particles in the replay, not baked" % (stem, kind))
        return None
    stride = int(sum(widths))
    tracks, cyc, window = 0, 0.0, getattr(recorder, "window", None)
    built = build_tracks(keys, dt, window, cycle) if all(k is not None for k in keys) else None
    if built is None:
        if not all(k is not None for k in keys):
            notes.append("%s: %s renderer's particles have no ID, played as one recording that cannot loop seamlessly" % (stem, kind))
        blob = bytearray(MAGIC + struct.pack("<iif", len(counts), stride, dt))
        blob += np.array(counts, "<i4").tobytes()
        blob += np.vstack(rows).astype("<f4").tobytes()
    else:
        per_frame, tracks, cyc = built
        # rows of the tracks that are left, frame by frame
        new_counts, new_rows, new_tracks, ri = [], [], [], 0
        for fi, c in enumerate(counts):
            if c == 0:
                new_counts.append(0)
                continue
            block, tr = rows[ri], per_frame[fi]
            ri += 1
            ok = tr >= 0
            new_counts.append(int(ok.sum()))
            if ok.any():
                new_rows.append(block[ok])
                new_tracks.append(tr[ok].astype("<i4"))
        while new_counts and new_counts[-1] == 0:
            new_counts.pop()
        if not new_rows:
            notes.append("%s: %s renderer drew no particles inside the replay's spawn window, not baked" % (stem, kind))
            return None
        counts, rows = new_counts, new_rows
        blob = bytearray(b"PRB2" + struct.pack("<iifif", len(counts), stride, dt, tracks, cyc))
        blob += np.array(counts, "<i4").tobytes()
        blob += np.vstack(rows).astype("<f4").tobytes()
        blob += np.concatenate(new_tracks).astype("<i4").tobytes()
    offsets, o = [], 0
    for w in widths:
        offsets.append(o)
        o += w
    extras = spec_extras(kind, props)
    return {**extras, "file": stem + ".bytes", "blob": bytes(blob), "kind": kind, "frames": len(counts), "rows": int(sum(counts)),
            "stride": stride, "tracks": tracks, "cycle": cyc, "format": 2 if built is not None else 1, "required": True, "why": why, "tickSeconds": dt, "fieldNames": fields,
            "fieldOffsets": offsets, "fieldWidths": widths,
            "dpComps": [b for _, _, b in dp_components(props)] if kind in ("Ribbon", "Sprite", "Mesh") else [],
            "dpUnavailable": [], "maxPerFrame": int(max(counts)),
            "radiusScale": float(props.get("RadiusScale", 1.0)) if kind == "Light" else None,
            "inverseSquared": bool(props.get("bUseInverseSquaredFalloff", False)) if kind == "Light" else None,
            "affectsTranslucency": bool(props.get("bAffectsTranslucency", False)) if kind == "Light" else None}


def read_blob(blob):
    """The reverse of the writer, for tests: (tick seconds, counts, rows (n, stride), track per row or None, cycle seconds or 0)."""
    if blob[:4] == b"PRB2":
        frames, stride, dt, tracks, cycle = struct.unpack_from("<iifif", blob, 4)
        head = 24
    else:
        assert blob[:4] == MAGIC
        frames, stride, dt = struct.unpack_from("<iif", blob, 4)
        tracks, cycle, head = 0, 0.0, 16
    counts = np.frombuffer(blob, "<i4", frames, head)
    n = int(counts.sum())
    rows = np.frombuffer(blob, "<f4", n * stride, head + 4 * frames).reshape(-1, stride)
    track = np.frombuffer(blob, "<i4", n, head + 4 * frames + 4 * n * stride) if blob[:4] == b"PRB2" else None
    return dt, counts, rows, track, cycle
