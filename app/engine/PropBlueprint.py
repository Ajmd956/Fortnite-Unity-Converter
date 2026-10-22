import json, os, re, sys

SKIP_TYPES = {"BoxComponent", "CapsuleComponent", "ChildActorComponent", "FortSoundIndicatorComponent", "MultiInteractActorComponent_C"}
DEFAULT_PITCH = (0.95, 1.05)
report = []
PATHS = {}


def note(kind, text):
    report.append(kind + ": " + text)


def ref_object_path(ref):
    if not isinstance(ref, dict):
        return None
    p = ref.get("AssetPathName") or ref.get("ObjectPath")
    if not p or p.startswith("/Script/"):
        return None
    if not p.startswith("/"):
        p = "/" + p
    name = ref.get("ObjectName") or ""
    m = re.search(r"'([^']*)'", name)
    name = (m.group(1) if m else name).split(":")[-1].split(".")[-1]
    head, dot, tail = p.rpartition(".")
    if dot and tail.isdigit():
        p = head + "." + (name or head.split("/")[-1])
    return p


def package_of(p):
    head, dot, tail = p.rpartition(".")
    return head if dot and "/" not in tail else p


def remember(name, ref):
    p = ref_object_path(ref)
    if name and p and "_GEN_VARIABLE" not in name and name not in PATHS:
        PATHS[name] = p


class Loader:
    """Hands out a package's exports by name: from the game through the app's bridge, or from a folder of JSON files."""

    def __init__(self, source=None, folder=None, home=None):
        self.source = source
        self.folder = folder
        self.home = home
        self.cache = {}
        self.fetched = {}

    def guesses(self, name):
        if name in PATHS:
            return [package_of(PATHS[name])]
        dirs = []
        for p in ([self.home] if self.home else []) + list(PATHS.values()):
            d = package_of(p).rpartition("/")[0]
            if d and d not in dirs:
                dirs.append(d)
        return [d + "/" + name for d in dirs]

    def get(self, name):
        if not name:
            return None
        if name in self.cache:
            return self.cache[name]
        data = None
        if self.folder:
            fp = os.path.join(self.folder, name + ".json")
            if os.path.exists(fp):
                with open(fp, encoding="utf-8-sig") as f:
                    data = json.load(f)
        elif self.source is not None:
            for g in self.guesses(name):
                f = self.source.asset_file(g)
                if f:
                    try:
                        with open(f, encoding="utf-8-sig") as fh:
                            data = json.load(fh)
                        self.fetched[name] = g
                        break
                    except (OSError, ValueError):
                        data = None
        self.cache[name] = data
        return data


def load(loader, name):
    return loader.get(name)


def short(ref):
    if not ref:
        return None
    n = ref.get("ObjectName") or ref.get("AssetPathName") or ""
    m = re.search(r"'([^']*)'", n)
    n = m.group(1) if m else n
    n = n.split(":")[-1].split(".")[-1]
    remember(n, ref)
    return n


def asset_name(ref):
    if not ref:
        return None
    if "AssetPathName" in ref:
        n = ref["AssetPathName"].split("/")[-1].split(".")[-1]
        remember(n, ref)
        return n
    return short(ref)


def vec(v, d=(0.0, 0.0, 0.0)):
    if not v:
        return list(d)
    return [v.get("X", d[0]), v.get("Y", d[1]), v.get("Z", d[2])]


def rot(v):
    if not v:
        return [0.0, 0.0, 0.0]
    return [v.get("Pitch", 0.0), v.get("Yaw", 0.0), v.get("Roll", 0.0)]


def rpy(v):
    if isinstance(v, dict) and "X" not in v and ("Roll" in v or "Pitch" in v or "Yaw" in v):
        return {"X": v.get("Roll", 0.0), "Y": v.get("Pitch", 0.0), "Z": v.get("Yaw", 0.0)}
    return v


def rpy_dist(d):
    """A rotation setting with its roll, pitch and yaw as X, Y and Z, the order the Unity builder reads."""
    if not d:
        return d
    out = dict(d)
    for k in ("min", "max"):
        if k in out:
            out[k] = rpy(out[k])
    if "values" in out:
        out["values"] = [rpy(x) for x in out["values"]]
    return out


def dist(d, default=None):
    if not d:
        return default
    out = {}
    if "Min" in d or "Max" in d:
        out["min"] = d.get("Min", d.get("Max"))
        out["max"] = d.get("Max", d.get("Min"))
    if "Values" in d:
        out["values"] = d["Values"]
    if "ValuesTimeRange" in d:
        out["timeRange"] = [d["ValuesTimeRange"].get("X"), d["ValuesTimeRange"].get("Y")]
    if "Mode" in d:
        out["mode"] = d["Mode"].split("::")[-1]
    pb = d.get("ParameterBinding", {}).get("Name")
    if pb and pb != "None":
        out["binding"] = pb
    return out if out else default


def merged_components(folder, bp_name, chain):
    comps = {}
    for fname in reversed(chain):
        data = load(folder, fname)
        if data is None:
            note("MISSING", "blueprint file " + fname + ".json")
            continue
        for o in data:
            if o["Type"].endswith("Component") or o["Type"].endswith("Component_C") or o["Type"] == "SceneComponent":
                key = o["Name"]
                cur = comps.setdefault(key, {"Type": o["Type"], "Name": key, "Properties": {}})
                cur["Properties"].update(o.get("Properties", {}))
    return comps


def scs_tree(folder, chain):
    nodes = {}
    for fname in reversed(chain):
        data = load(folder, fname)
        if data is None:
            continue
        for o in data:
            if o["Type"] == "SCS_Node":
                p = o["Properties"]
                tmpl = short(p.get("ComponentTemplate"))
                if tmpl is None:
                    continue
                nodes[tmpl] = {
                    "var": p.get("InternalVariableName"),
                    "template": tmpl,
                    "children": [o2 for o2 in p.get("ChildNodes", [])],
                    "scsName": o["Name"],
                }
    return nodes


def scs_children_map(folder, chain):
    result = {}
    for fname in reversed(chain):
        data = load(folder, fname)
        if data is None:
            continue
        byname = {}
        for o in data:
            if o["Type"] == "SCS_Node":
                byname[o["Name"]] = o
        for o in data:
            if o["Type"] == "SCS_Node":
                tmpl = short(o["Properties"].get("ComponentTemplate"))
                kids = []
                for c in o["Properties"].get("ChildNodes", []):
                    nm = c["ObjectName"].split(".")[-1].rstrip("'")
                    if nm in byname:
                        kids.append(short(byname[nm]["Properties"].get("ComponentTemplate")))
                result[tmpl] = kids
    return result


def attenuation_from_settings(a):
    if not a:
        return None
    return {
        "extent": a.get("AttenuationShapeExtents", {}).get("X", 0.0),
        "falloff": a.get("FalloffDistance", 0.0),
        "algorithm": a.get("DistanceAlgorithm", "EAttenuationDistanceModel::Linear").split("::")[-1],
        "curve": [[k["Time"], k["Value"]] for k in a.get("CustomAttenuationCurve", {}).get("EditorCurveData", {}).get("Keys", [])],
        "nonSpatializedRadiusStart": a.get("NonSpatializedRadiusStart"),
    }


def cue_spec(folder, cue_name):
    data = load(folder, cue_name)
    if data is None:
        note("MISSING", "sound cue " + cue_name + ".json")
        return {"name": cue_name, "waves": []}
    byname = {o["Name"]: o for o in data}
    cue = [o for o in data if o["Type"] == "SoundCue"][0]
    p = cue["Properties"]
    spec = {"name": cue_name, "volumeMultiplier": p.get("VolumeMultiplier", 1.0), "waves": []}
    att = None
    if p.get("bOverrideAttenuation"):
        att = attenuation_from_settings(p.get("AttenuationOverrides"))
    elif p.get("AttenuationSettings"):
        att_name = short(p["AttenuationSettings"])
        att = attenuation_asset(folder, att_name)
    spec["attenuation"] = att

    def walk(node_ref, vol, pitch, attn, env):
        nm = short(node_ref)
        n = byname.get(nm)
        if n is None:
            note("MISSING", cue_name + ": node " + str(nm))
            return
        t = n["Type"]
        pr = n.get("Properties", {})
        kids = [c for c in pr.get("ChildNodes", []) if c]
        if t == "SoundNodeWavePlayer":
            spec["waves"].append({
                "wave": asset_name(pr.get("SoundWaveAssetPtr")),
                "loop": bool(pr.get("bLooping", False)),
                "volume": vol,
                "pitch": pitch,
                "attenuation": attn,
                "envelope": env,
            })
        elif t == "SoundNodeMixer":
            iv = pr.get("InputVolume", [1.0] * len(kids))
            for i, k in enumerate(kids):
                walk(k, vol * (iv[i] if i < len(iv) else 1.0), pitch, attn, env)
        elif t == "SoundNodeAttenuation":
            a = attenuation_asset(folder, short(pr.get("AttenuationSettings"))) if pr.get("AttenuationSettings") else attn
            for k in kids:
                walk(k, vol, pitch, a, env)
        elif t == "SoundNodeModulator":
            pmin = pr.get("PitchMin", DEFAULT_PITCH[0])
            pmax = pr.get("PitchMax", DEFAULT_PITCH[1])
            if "PitchMin" not in pr or "PitchMax" not in pr:
                note("DEFAULT", cue_name + ": modulator pitch field missing in export, using engine default (" + str(DEFAULT_PITCH) + ") for the missing side")
            if "VolumeMin" not in pr and "VolumeMax" not in pr:
                note("DEFAULT", cue_name + ": modulator volume range not exported (engine default 0.95-1.05 random), using 1.0")
            pv = pmin if pmin == pmax else (pmin + pmax) * 0.5
            if pmin != pmax:
                note("APPROX", cue_name + ": random pitch range " + str(pmin) + "-" + str(pmax) + " averaged")
            for k in kids:
                walk(k, vol, pitch * pv, attn, env)
        elif t == "SoundNodeEnveloper":
            e = {
                "volume": [[k["Time"], k["Value"]] for k in pr.get("VolumeCurve", {}).get("EditorCurveData", {}).get("Keys", [])],
                "pitch": [[k["Time"], k["Value"]] for k in pr.get("PitchCurve", {}).get("EditorCurveData", {}).get("Keys", [])],
                "loop": {"loop": bool(pr.get("bLoop", False)), "indefinitely": bool(pr.get("bLoopIndefinitely", False)), "end": pr.get("LoopEnd"), "start": pr.get("LoopStart")},
            }
            note("UNSUPPORTED", cue_name + ": enveloper curves stored in spec (volume keys " + str(len(e["volume"])) + ") but not applied in Unity")
            for k in kids:
                walk(k, vol, pitch, attn, e)
        else:
            note("UNSUPPORTED", cue_name + ": sound node type " + t)
            for k in kids:
                walk(k, vol, pitch, attn, env)

    walk(p["FirstNode"], 1.0, 1.0, None, None)
    if att is None and any(w["attenuation"] is None for w in spec["waves"]):
        note("DEFAULT", cue_name + ": no attenuation exported for at least one wave, Unity default 3D rolloff left in place")
    return spec


_att_cache = {}


def attenuation_asset(folder, name):
    if name in _att_cache:
        return _att_cache[name]
    data = load(folder, name)
    if data is None:
        note("MISSING", "attenuation asset " + str(name) + ".json")
        _att_cache[name] = None
        return None
    a = attenuation_from_settings(data[0]["Properties"].get("Attenuation"))
    a["asset"] = name
    _att_cache[name] = a
    return a


HANDLED_MODULES = {"InitializeParticle", "ShapeLocation", "AddVelocity", "Drag", "ScaleColor", "ScaleSpriteSize", "ScaleMeshSize",
                   "CameraOffset", "DynamicMaterialParameters", "GravityForce", "AccelerationForce", "SpriteRotationRate",
                   "InitialMeshOrientation", "SubUVAnimation"}


def mesh_slots(folder, mesh):
    out = []
    md = load(folder, mesh) if mesh else None
    for o in md or []:
        if o.get("Type") == "StaticMesh":
            for sm in o.get("Properties", {}).get("StaticMaterials", []):
                out.append({"slot": sm.get("MaterialSlotName"), "material": asset_name(sm.get("MaterialInterface"))})
    return out


def renderer_spec(folder, emname, rtype_name, rp):
    import NiagaraPorter
    rs = {"type": rtype_name.replace("Niagara", "").replace("RendererProperties", "")}
    rs["colorAttr"], rs["dpAttrs"] = NiagaraPorter.renderer_bindings(rp)
    rs["sortMode"] = str(rp.get("SortMode", "")).split("::")[-1]
    if rs["type"] == "Sprite":
        rs.update({
            "material": asset_name(rp.get("Material")),
            "alignment": rp.get("Alignment", "ENiagaraSpriteAlignment::Unaligned").split("::")[-1],
            "facing": rp.get("FacingMode", "ENiagaraSpriteFacingMode::FaceCamera").split("::")[-1],
            "pivot": [rp.get("PivotInUVSpace", {}).get("X", 0.5), rp.get("PivotInUVSpace", {}).get("Y", 0.5)],
            "subImage": [rp.get("SubImageSize", {}).get("X", 1.0), rp.get("SubImageSize", {}).get("Y", 1.0)],
        })
    elif rs["type"] == "Mesh":
        mesh = rp["Meshes"][0]
        mesh_name = asset_name(mesh.get("Mesh"))
        ov = rp.get("OverrideMaterials", [])
        mats = [asset_name(x.get("ExplicitMat")) for x in ov if x] if rp.get("bOverrideMaterials") is not False else []
        slots = []
        if not any(mats):
            slots = mesh_slots(folder, mesh_name)
            mats = [sl.get("material") for sl in slots]
            if any(mats):
                note("DEFAULT", emname + ": no override material, using the mesh's own materials (" + ", ".join(str(m) for m in mats) + ")")
            else:
                note("MISSING", emname + ": mesh renderer has no override material and the mesh data lists none, the particle will have no material")
        rs.update({
            "mesh": mesh_name,
            "meshRotation": rot(mesh.get("Rotation")),
            "meshScale": vec(mesh.get("Scale"), (1, 1, 1)),
            "materials": mats,
            "materialSlots": [sl.get("slot") for sl in slots],
            "facing": str(rp.get("FacingMode", "ENiagaraMeshFacingMode::Default")).split("::")[-1],
            "lockedAxis": vec(rp.get("LockedAxis"), (0.0, 0.0, 1.0)) if rp.get("bLockedAxisEnable") else None,
            "lockedAxisSpace": str(rp.get("LockedAxisSpace", "")).split("::")[-1],
            "pivotOffset": vec(rp.get("PivotOffset")),
            "meshPivot": vec(mesh.get("PivotOffset")),
        })
    elif rs["type"] == "Light":
        rs.update({
            "radiusScale": rp.get("RadiusScale", 1.0),
            "inverseSquared": rp.get("bUseInverseSquaredFalloff", False),
        })
        note("DEFAULT", emname + ": light renderer radius not exported (per-particle LightRadius), Unity range uses 100 cm x RadiusScale")
    else:
        note("UNSUPPORTED", emname + ": renderer " + rs["type"])
    return rs


def emitter_spec(folder, sysdata, handle):
    em_ref = handle.get("StatelessEmitter")
    emname = short(em_ref)
    em = [o for o in sysdata if o["Type"] == "NiagaraStatelessEmitter" and o["Name"] == emname][0]
    ep = em["Properties"]

    def outer_ok(o):
        return o.get("Outer", {}).get("ObjectName", "").endswith(":" + emname + "'")

    mods = {}
    for o in sysdata:
        if o["Type"].startswith("NiagaraStatelessModule_") and outer_ok(o):
            mods[o["Type"].replace("NiagaraStatelessModule_", "")] = o.get("Properties", {})
    rends = [o for o in sysdata if "RendererProperties" in o["Type"] and outer_ok(o)]
    st = ep.get("EmitterState", {})
    spec = {
        "name": handle["Name"],
        "loop": {
            "duration": dist(st.get("LoopDuration")),
            "delay": dist(st.get("LoopDelay")),
            "delayEnabled": st.get("bLoopDelayEnabled", False),
            "delayFirstOnly": st.get("bDelayFirstLoopOnly", False),
            "behavior": st.get("LoopBehavior", "ENiagaraLoopBehavior::Infinite").split("::")[-1],
        },
        "spawn": [],
        "modules": {},
        "renderers": [],
    }
    if spec["loop"]["duration"] is None:
        note("DEFAULT", handle["Name"] + ": LoopDuration not exported, engine default 1.0 used")
    for s in ep.get("SpawnInfos", []):
        if not s.get("bEnabled", True):
            continue
        spec["spawn"].append({
            "type": s["Type"].split("::")[-1],
            "time": s.get("SpawnTime", 0.0),
            "amount": dist(s.get("Amount")),
            "rate": dist(s.get("Rate")),
        })
    init = mods.get("InitializeParticle", {})
    m = spec["modules"]
    m["lifetime"] = dist(init.get("LifetimeDistribution"))
    if m["lifetime"] is None:
        note("DEFAULT", handle["Name"] + ": lifetime not exported, engine default 1.0 used")
    m["color"] = dist(init.get("ColorDistribution"))
    if m["color"] is None:
        note("DEFAULT", handle["Name"] + ": initial color not exported, engine default white used")
    m["spriteSize"] = dist(init.get("SpriteSizeDistribution"))
    if m["spriteSize"] is None and any("Sprite" in r["Type"] for r in rends):
        note("DEFAULT", handle["Name"] + ": sprite size not exported, engine default 100x100 used")
    m["position"] = dist(init.get("InitialPositionDistribution"))
    m["rotation"] = dist(init.get("SpriteRotationDistribution"))
    m["meshScale"] = dist(init.get("MeshScaleDistribution"))
    if "ShapeLocation" in mods:
        s = mods["ShapeLocation"]
        m["shape"] = {
            "primitive": s.get("ShapePrimitive", "").split("::")[-1],
            "cylinderRadius": dist(s.get("CylinderRadius")),
            "cylinderHeight": dist(s.get("CylinderHeight")),
            "ringRadius": dist(s.get("RingRadius")),
            "heightMidpoint": dist(s.get("CylinderHeightMidpoint")),
            "boxSize": dist(s.get("BoxSize")),
            "planeSize": dist(s.get("PlaneSize")),
            "sphereRadius": dist(s.get("SphereRadius")),
            "discCoverage": dist(s.get("DiscCoverage")),
            "scale": dist(s.get("ShapeScale")),
            "offset": dist(s.get("TransformOffset")),
            "raw": [k for k in s.keys() if not k.endswith("Distribution")],
            "rawValues": {k: v for k, v in s.items() if isinstance(v, (str, int, float, bool)) or (isinstance(v, dict) and "Mode" in v)},
        }
    if "AddVelocity" in mods:
        av = mods["AddVelocity"]
        m["velocity"] = dist(av.get("LinearVelocityDistribution"))
        m["velocityType"] = str(av.get("VelocityType", "Linear")).split("::")[-1]
        m["velocityScale"] = dist(av.get("LinearVelocityScale"))
        m["cone"] = {
            "speed": dist(av.get("ConeVelocityDistribution")),
            "angle": av.get("ConeAngle", 45.0),
            "rotation": dist(av.get("ConeRotation")),
            "scale": dist(av.get("ConeVelocityScale")),
        }
        m["point"] = {
            "speed": dist(av.get("PointVelocityDistribution")),
            "origin": vec(av.get("PointOrigin")) if isinstance(av.get("PointOrigin"), dict) else None,
            "scale": dist(av.get("PointVelocityScale")),
        }
    if "Drag" in mods:
        m["drag"] = dist(mods["Drag"].get("DragDistribution"))
        if m["drag"] is None:
            note("DEFAULT", handle["Name"] + ": Drag module present without value, ignored")
    if "GravityForce" in mods:
        m["gravity"] = dist(mods["GravityForce"].get("GravityDistribution"),
                            {"min": {"X": 0.0, "Y": 0.0, "Z": -980.0}, "max": {"X": 0.0, "Y": 0.0, "Z": -980.0}})
    if "AccelerationForce" in mods:
        m["acceleration"] = dist(mods["AccelerationForce"].get("AccelerationDistribution"))
    if "SpriteRotationRate" in mods:
        m["rotationRate"] = dist(mods["SpriteRotationRate"].get("RotationRateDistribution"))
    if "InitialMeshOrientation" in mods:
        m["meshOrientation"] = rpy_dist(dist(mods["InitialMeshOrientation"].get("Rotation")))
    if "SubUVAnimation" in mods:
        sub = mods["SubUVAnimation"]
        m["subUV"] = {"frames": sub.get("NumFrames", 16), "mode": str(sub.get("AnimationMode", "")).split("::")[-1]}
    if "ScaleColor" in mods:
        m["scaleColor"] = dist(mods["ScaleColor"].get("ScaleDistribution"))
    if "ScaleSpriteSize" in mods:
        m["scaleSpriteSize"] = dist(mods["ScaleSpriteSize"].get("ScaleDistribution"))
    if "ScaleMeshSize" in mods:
        m["scaleMeshSize"] = dist(mods["ScaleMeshSize"].get("ScaleDistribution"))
    if "CameraOffset" in mods:
        m["cameraOffset"] = dist(mods["CameraOffset"].get("CameraOffsetDistribution"))
        note("UNSUPPORTED", handle["Name"] + ": camera offset " + str(m["cameraOffset"].get("values")) + " (shader-side in Unity, not applied)")
    if "DynamicMaterialParameters" in mods:
        dm = {}
        for k, v in mods["DynamicMaterialParameters"].items():
            ch = {}
            for c in "XYZW":
                dd = dist(v.get(c + "ChannelDistribution"))
                if dd:
                    ch[c] = dd
            if ch:
                dm[k] = ch
        m["dynamicParams"] = dm
        if any(("binding" in c or c.get("mode") in ("UniformCurve", "NonUniformCurve")) for p in dm.values() for c in p.values()):
            note("UNSUPPORTED", handle["Name"] + ": dynamic material parameters with curves/bindings (not applied; shader needs custom vertex streams)")
    spec["unhandledModules"] = sorted(k for k in mods if k not in HANDLED_MODULES)
    if spec["unhandledModules"]:
        note("UNSUPPORTED", handle["Name"] + ": Niagara modules not converted: " + ", ".join(spec["unhandledModules"]))
    for r in rends:
        spec["renderers"].append(renderer_spec(folder, handle["Name"], r["Type"], r.get("Properties", {})))
    return spec


def system_spec(folder, sysname):
    data = load(folder, sysname)
    if data is None:
        note("MISSING", "niagara system " + sysname + ".json")
        return None
    sysobj = [o for o in data if o["Type"] == "NiagaraSystem"][0]
    out = {"name": sysname, "emitters": []}
    for h in sysobj["Properties"]["EmitterHandles"]:
        if not h.get("bIsEnabled", True):
            continue
        if h.get("EmitterMode", "").endswith("Stateless") and h.get("StatelessEmitter"):
            out["emitters"].append(emitter_spec(folder, data, h))
        else:
            note("UNSUPPORTED", sysname + ": enabled non-stateless emitter " + h["Name"])
    return out


def build(folder, bp_name, chain):
    comps = merged_components(folder, bp_name, chain)
    kids = scs_children_map(folder, chain)
    tmpl_by_var = {}
    for fname in reversed(chain):
        d = load(folder, fname)
        if d:
            for o in d:
                if o["Type"] == "SCS_Node":
                    tmpl_by_var[short(o["Properties"].get("ComponentTemplate"))] = o["Properties"].get("InternalVariableName")
    root_tmpl = "DefaultSceneRoot_GEN_VARIABLE"
    nodes = [{"name": bp_name, "parent": -1, "kind": "empty", "loc": [0, 0, 0], "rot": [0, 0, 0], "scale": [1, 1, 1]}]
    all_children = set(c for v in kids.values() for c in v)
    tops = [t for t in kids if t not in all_children and t != root_tmpl]

    def comp_for(tmpl):
        key = tmpl.split(":")[-1] if ":" in tmpl else tmpl
        return comps.get(key)

    def add(tmpl, parent_idx):
        c = comp_for(tmpl)
        if c is None:
            note("MISSING", "component template " + tmpl)
            return
        p = c["Properties"]
        name = c["Name"].replace("_GEN_VARIABLE", "")
        base = {"name": name, "parent": parent_idx, "loc": vec(p.get("RelativeLocation")), "rot": rot(p.get("RelativeRotation")), "scale": vec(p.get("RelativeScale3D"), (1, 1, 1))}
        t = c["Type"]
        has_kids = bool(kids.get(tmpl))
        node = None
        if t in SKIP_TYPES:
            note("REMOVED", name + " (" + t + ", gameplay/navigation)")
        elif t == "StaticMeshComponent":
            hidden = p.get("bHiddenInGame") or p.get("bVisible") is False
            if hidden:
                if has_kids:
                    node = dict(base, kind="empty")
                    note("REMOVED", name + " mesh (hidden helper), kept as empty transform because it parents other components")
                else:
                    note("REMOVED", name + " (hidden helper mesh)")
            else:
                mesh = asset_name(p.get("StaticMesh"))
                node = dict(base, kind="mesh", mesh=mesh, slots=[])
                md = load(folder, mesh)
                if md:
                    for o in md:
                        if o["Type"] == "StaticMesh":
                            for sm in o["Properties"].get("StaticMaterials", []):
                                node["slots"].append({"slot": sm.get("MaterialSlotName"), "material": asset_name(sm.get("MaterialInterface"))})
                    ov = p.get("OverrideMaterials")
                    if ov:
                        for i, x in enumerate(ov):
                            if x and i < len(node["slots"]):
                                node["slots"][i]["material"] = asset_name(x)
                else:
                    note("MISSING", "static mesh data " + str(mesh) + ".json")
        elif t == "SpotLightComponent" or t == "PointLightComponent":
            col = p.get("LightColor", {})
            node = dict(base, kind="light", lightType="Spot" if t == "SpotLightComponent" else "Point",
                        intensity=p.get("Intensity"), color=[col.get("R", 255), col.get("G", 255), col.get("B", 255)],
                        outer=p.get("OuterConeAngle"), inner=p.get("InnerConeAngle"), radius=p.get("AttenuationRadius"),
                        shadows=bool(p.get("CastShadows", False)))
            if node["intensity"] is None:
                note("DEFAULT", name + ": light intensity not exported")
            note("APPROX", name + ": UE intensity " + str(p.get("Intensity")) + " (units unspecified in export) passed to Unity unchanged; inner cone angle unused in built-in pipeline")
        elif t == "AudioComponent":
            cue = short(p.get("Sound"))
            node = dict(base, kind="audio", cue=cue_spec(folder, cue) if cue else None, playOnAwake=bool(p.get("bAutoActivate", True)))
        elif t == "NiagaraComponent":
            sysname = short(p.get("Asset"))
            inferred = False
            if not sysname:
                sysname = c["Name"].replace("_GEN_VARIABLE", "")
                inferred = True
                note("INFERRED", name + ": Niagara asset not in export, system name taken from component name (" + sysname + ")")
            node = dict(base, kind="particles", system=system_spec(folder, sysname), inferred=inferred, playOnAwake=bool(p.get("bAutoActivate", True)))
        elif t == "SceneComponent":
            node = dict(base, kind="empty")
        else:
            note("UNSUPPORTED", name + " (" + t + ")")
        idx = parent_idx
        if node is not None:
            nodes.append(node)
            idx = len(nodes) - 1
        for k in kids.get(tmpl, []):
            add(k, idx)

    for t in tops:
        add(t, 0)
    dflt = None
    for fname in chain:
        d = load(folder, fname)
        if d:
            for o in d:
                if o["Name"].startswith("Default__"):
                    p = o.get("Properties", {})
                    if "HornAlertAudio" in p and dflt is None:
                        dflt = short(p["HornAlertAudio"])
    if dflt:
        nodes.append({"name": "HornAlert", "parent": 0, "kind": "audio", "loc": [0, 0, 0], "rot": [0, 0, 0], "scale": [1, 1, 1], "cue": cue_spec(folder, dflt), "playOnAwake": False})
    return {"blueprint": bp_name, "nodes": nodes, "report": sorted(set(report))}




COMPONENT_HINTS = ("SpotLightComponent", "PointLightComponent", "AudioComponent", "NiagaraComponent")


def walk_chain(loader, path):
    """The Blueprint's own package and its parent Blueprints' packages, child first: [(name, object path)]."""
    chain, seen = [], set()
    current = path
    while current and len(chain) < 8:
        pkg = package_of(current)
        name = pkg.rpartition("/")[2]
        if pkg in seen:
            break
        seen.add(pkg)
        PATHS.setdefault(name, current if "." in current.rpartition("/")[2] else current + "." + name)
        data = loader.get(name)
        if data is None:
            if not chain:
                return None
            note("MISSING", "parent blueprint " + name)
            break
        chain.append((name, pkg))
        sup = None
        for o in data:
            if o.get("Type") == "BlueprintGeneratedClass":
                s = o.get("SuperStruct") or o.get("Super") or {}
                sup = s.get("ObjectPath")
                break
        current = sup if sup and not sup.startswith("/Script/") else None
        if current and not current.startswith("/"):
            current = "/" + current
    return chain


def looks_like_prop(source, path):
    """True when the Blueprint (or a parent) holds lights, sounds or particle systems, which the sky dome converter drops."""
    loader = Loader(source=source)
    try:
        chain = walk_chain(loader, path)
    except Exception:
        return False
    for name, _ in chain or []:
        for o in loader.get(name) or []:
            if o.get("Type") in COMPONENT_HINTS:
                return True
    return False


def collect_assets(spec):
    meshes, materials, waves = [], [], []

    def add(lst, name):
        if name and name not in lst:
            lst.append(name)

    for n in spec["nodes"]:
        if n.get("kind") == "mesh":
            add(meshes, n.get("mesh"))
            for s in n.get("slots", []):
                add(materials, s.get("material"))
        elif n.get("kind") == "audio" and n.get("cue"):
            for w in n["cue"].get("waves", []):
                add(waves, w.get("wave"))
        elif n.get("kind") == "particles" and n.get("system"):
            for e in n["system"].get("emitters", []):
                for r in e.get("renderers", []):
                    add(materials, r.get("material"))
                    add(meshes, r.get("mesh"))
                    for m in r.get("materials", []) or []:
                        add(materials, m)
    return meshes, materials, waves


def run(source, path, out, name, scripts=None):
    loader = Loader(source=source, home=package_of(path).rpartition("/")[0])
    chain = walk_chain(loader, path)
    if not chain:
        sys.exit("the app did not give the Blueprint (%s)" % (source.why("asset", path) or "not found in the game files"))
    bp_name = name or re.sub(r"_C$", "", chain[0][0])
    spec = build(loader, bp_name, [c[0] for c in chain])
    finish(spec, out, bp_name, scripts)


def finish(spec, out, bp_name, scripts=None):
    import shutil
    folder = os.path.join(out, bp_name)
    os.makedirs(folder, exist_ok=True)
    os.makedirs(os.path.join(out, "Editor"), exist_ok=True)
    meshes, materials, waves = collect_assets(spec)
    spec["assets"] = {"meshes": meshes, "materials": materials, "waves": waves}
    with open(os.path.join(folder, bp_name + ".propspec.json"), "w", encoding="utf-8") as fh:
        json.dump(spec, fh, indent=1)
    with open(os.path.join(folder, bp_name + ".propreport.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(spec["report"]) + "\n")
    here = scripts or os.path.join(os.path.dirname(os.path.abspath(__file__)), "unity")
    for fname in ("PorterBlueprintSetup.cs", "PorterPropBuilder.cs"):
        srcf = os.path.join(here, fname)
        if not os.path.exists(srcf):
            sys.exit("the Unity scripts are missing from the app (%s)" % here)
        shutil.copyfile(srcf, os.path.join(out, "Editor", fname))
    kinds = {}
    for n in spec["nodes"]:
        kinds[n["kind"]] = kinds.get(n["kind"], 0) + 1
    print("propblueprint: yes")
    print("%d components (%s), %d meshes, %d materials, %d sounds" % (
        len(spec["nodes"]), ", ".join("%d %s" % (v, k) for k, v in sorted(kinds.items())), len(meshes), len(materials), len(waves)))
    for label, names in (("material", materials), ("mesh", meshes), ("sound", waves)):
        for n in names:
            p = PATHS.get(n)
            if p:
                print("%s: %s" % (label, p))
            else:
                print("note: no path for %s %s, it was not exported" % (label, n))
    for r in spec["report"]:
        if r.startswith(("MISSING", "UNSUPPORTED", "INFERRED")):
            print("note: " + r)
    print("wrote %s.propspec.json" % bp_name)
    print("folder: %s" % os.path.abspath(folder))


def xyz(v):
    return {"X": v[0], "Y": v[1], "Z": v[2]}


def span3(rng):
    return {"min": xyz(rng["min"]), "max": xyz(rng["max"])}


def span1(rng, index=0):
    return {"min": rng["min"][index], "max": rng["max"][index]}


def resample(points, keys, count):
    pts = sorted(points, key=lambda p: p["t"])
    out = []
    for i in range(count):
        t = i / (count - 1) if count > 1 else 0.0
        if t <= pts[0]["t"]:
            out.append({k: pts[0][k] for k in keys})
            continue
        if t >= pts[-1]["t"]:
            out.append({k: pts[-1][k] for k in keys})
            continue
        for a, b in zip(pts, pts[1:]):
            if a["t"] <= t <= b["t"]:
                f = (t - a["t"]) / (b["t"] - a["t"]) if b["t"] > a["t"] else 0.0
                out.append({k: a[k] + (b[k] - a[k]) * f for k in keys})
                break
    return out


def stream_spec(emname, em, rs):
    st = em.get("streams") or {}
    out = {"color": None, "dp": []}
    c = st.get(rs.get("colorAttr"))
    if c and c["width"] == 4:
        n = len(c["mean"])
        out["color"] = [{"t": i / float(n - 1), "r": m[0], "g": m[1], "b": m[2], "a": m[3]} for i, m in enumerate(c["mean"])]
        spread = max(max(abs(hi[k] - lo[k]) for k in range(3)) for lo, hi in zip(c["min"], c["max"]))
        if spread > 0.02 * max(max(m[:3]) for m in c["mean"]):
            note("APPROX", emname + ": colour attribute " + rs["colorAttr"] + " differs between particles (up to " + str(round(spread, 3)) + " apart), the average over all particles is used")
    for i, attr in enumerate(rs.get("dpAttrs") or []):
        s = st.get(attr)
        if not s or s["width"] != 4:
            continue
        comps = []
        for k in range(4):
            lo = [p[k] for p in s["min"]]
            hi = [p[k] for p in s["max"]]
            mid = [p[k] for p in s["mean"]]
            spread = max(b - a for a, b in zip(lo, hi))
            drift = max(mid) - min(mid)
            if spread < 1e-3 and drift < 1e-3:
                comps.append({"mode": "const", "value": sum(mid) / len(mid)})
            elif spread <= 0.1 * drift or spread < 1e-3:
                comps.append({"mode": "curve", "values": mid})
                if spread > 0.02 * drift:
                    note("APPROX", emname + ": " + attr + " component " + "xyzw"[k] + " varies slightly between particles (up to " + str(round(spread, 4)) + "), the average curve is used")
            elif drift <= 0.1 * spread:
                comps.append({"mode": "range", "min": min(lo), "max": max(hi)})
            else:
                comps.append({"mode": "curves", "min": lo, "max": hi})
                note("APPROX", emname + ": " + attr + " component " + "xyzw"[k] + " is a random range that also changes with age, Unity picks between the lowest and highest curve")
        out["dp"].append({"index": i, "attr": attr, "comps": comps})
    return out


def motion_notes(em):
    m = em.get("motion") or {}
    name = em["name"]
    drift, bound = float(m.get("driftCm", 0.0)), float(m.get("explainedCm", 0.0))
    if drift > 1.0 and drift > bound * 1.25 + 2.0:
        note("APPROX", "%s: in Unreal %d of %d particles move after they spawn, up to %.1f cm from where they were born, which is more than the spawn velocity explains (%.1f cm); the Unity particle system only reproduces the spawn position and velocity, so this movement is missing" % (name, int(m.get("movedParticles", 0)), int(m.get("tracked", 0)), drift, bound))
    spin = float(m.get("spinDeg", 0.0))
    if spin > 5.0:
        rate = float(m.get("spinRateDegPerSec", 0.0))
        note("APPROX", "%s: in Unreal particles keep turning after they spawn, by up to %.0f degrees%s; spin after spawn is not carried over to the Unity particle system" % (name, spin, (" at about %.0f degrees per second" % rate) if rate > 0.0 else ""))


def replay_position(em):
    """Where the replay's particles were born, as the smallest and largest value of each axis in cm (None if nothing spawned)."""
    pos = (em.get("initial") or {}).get("Position")
    if not pos or not em.get("totalBorn"):
        return None
    return {"min": [float(v) for v in pos["min"]], "max": [float(v) for v in pos["max"]], "births": int(em["totalBorn"])}


def scripted_emitter_spec(loader, system, em, seconds):
    """An emitter the replay played (one that is not stateless) as the same emitter spec the Unity builder reads."""
    import NiagaraPorter
    name = em["name"]
    if em["totalBorn"] == 0:
        note("UNSUPPORTED", name + ": spawned nothing in the " + str(seconds) + " seconds played, left out")
        return None
    bursts, rate, period = em["bursts"], em["rate"], em["loopPeriod"]
    one_shot = bool(bursts) and not rate and not period
    last = max([b["time"] for b in bursts] or [0.0])
    duration = period if period else max(float(seconds), last + 0.1)
    spawn = [{"type": "Burst", "time": b["time"], "amount": {"min": b["count"], "max": b["count"]}} for b in bursts]
    if rate:
        spawn.append({"type": "Rate", "rate": {"min": rate, "max": rate}})
    life_min, life_max = em["lifetimeMin"], em["lifetimeMax"]
    if em["persistent"] or life_max <= 0:
        life_min = life_max = float(seconds)
        note("APPROX", name + ": particles stay alive in the replay, lifetime set to the " + str(seconds) + " seconds played")
    init = em.get("initial") or {}
    mods = {"lifetime": {"min": life_min, "max": life_max}}
    sprite = em["sizeUnit"] == "cm"
    start = em["startSize"]
    if sprite:
        mods["spriteSize"] = {"min": xyz([start["x"], start["y"], 0.0]), "max": xyz([start["x"], start["y"], 0.0])}
    else:
        mods["meshScale"] = {"min": xyz([start["x"], start["y"], start["z"]]), "max": xyz([start["x"], start["y"], start["z"]])}
    curve = em["sizeCurve"]
    if curve and max(abs(p["s"] - 1.0) for p in curve) > 1e-3:
        values = [{"X": v["s"], "Y": v["s"]} for v in resample(curve, ("s",), len(curve))]
        mods["scaleSpriteSize" if sprite else "scaleMeshSize"] = {"values": values}
    if em["colorCurve"]:
        mods["scaleColor"] = {"values": [{"R": v["r"], "G": v["g"], "B": v["b"], "A": v["a"]} for v in resample(em["colorCurve"], ("r", "g", "b", "a"), len(em["colorCurve"]))]}
    if "Velocity" in init:
        mods["velocity"] = span3(init["Velocity"])
        mods["velocityType"] = "Linear"
    if "Position" in init:
        mods["position"] = span3(init["Position"])
        if em["local"] is False:
            note("APPROX", name + ": world-space emitter, positions treated as local to the object")
    if "SpriteRotation" in init and sprite:
        mods["rotation"] = span1(init["SpriteRotation"])
    if "MeshOrientation" in init and not sprite:
        mods["meshOrientation"] = span3(init["MeshOrientation"])
    emitter = next((e for e in system.emitters if e.name == name), None)
    renderers = []
    for rtype, props in (NiagaraPorter.renderer_exports(system, emitter) if emitter is not None else []):
        if props.get("bIsEnabled", True):
            rs = renderer_spec(loader, name, rtype, props)
            if rs["type"] in ("Mesh", "Sprite"):
                rs["streams"] = stream_spec(name, em, rs)
            renderers.append(rs)
    if not renderers:
        note("UNSUPPORTED", name + ": no enabled renderer")
        return None
    note("APPROX", name + ": scripted emitter, built from a replay of its scripts (spawn counts, lifetime, colour and size curves, and starting position, velocity and rotation as ranges)")
    if em["requires"]:
        note("INFERRED", name + ": only emits when the game sets " + ", ".join(em["requires"]) + " to 1, the replay set it")
    return {
        "name": name,
        "loop": {"duration": {"min": duration, "max": duration}, "delay": None, "delayEnabled": False, "delayFirstOnly": False,
                 "behavior": "Once" if one_shot else "Infinite"},
        "spawn": spawn,
        "modules": mods,
        "renderers": renderers,
        "unhandledModules": [],
    }


def run_system(source, path, out, name, scripts=None, seconds=6.0):
    """A Niagara system as a prefab spec: stateless emitters from their module settings, the rest from a replay."""
    import NiagaraPorter
    pkg = package_of(path)
    sysname = pkg.rpartition("/")[2]
    PATHS.setdefault(sysname, path if "." in path.rpartition("/")[2] else path + "." + sysname)
    loader = Loader(source=source, home=pkg.rpartition("/")[0])
    stateless = {}
    try:
        sysspec = system_spec(loader, sysname)
        for e in (sysspec or {}).get("emitters", []):
            stateless[e["name"]] = e
    except Exception as ex:
        note("MISSING", "stateless emitter settings could not be read (" + type(ex).__name__ + ": " + str(ex) + "), every emitter is built from the replay")
    try:
        dump = NiagaraPorter.fetch_dump(source.bridge, path)
    except RuntimeError as ex:
        sys.exit(str(ex))
    system, summaries, skipped = NiagaraPorter.replay(NiagaraPorter.to_exports(dump), sysname, path, seconds)
    emitters = []
    for em in summaries:
        motion_notes(em)
        if em["name"] in stateless:
            spec = stateless.pop(em["name"])
            replayed = replay_position(em)
            if replayed is not None:
                spec["replayPosition"] = replayed
            emitters.append(spec)
            continue
        spec = scripted_emitter_spec(loader, system, em, seconds)
        if spec is not None:
            emitters.append(spec)
    emitters += list(stateless.values())
    for s in skipped:
        note("UNSUPPORTED", s["emitter"] + ": not converted (" + str(s["why"]) + ")")
    report[:] = [r for r in report if "enabled non-stateless emitter" not in r]
    bp_name = name or sysname
    node = {"name": "Particles", "parent": 0, "kind": "particles", "loc": [0, 0, 0], "rot": [0, 0, 0], "scale": [1, 1, 1],
            "system": {"name": sysname, "emitters": emitters}, "inferred": False, "playOnAwake": True}
    root = {"name": bp_name, "parent": -1, "kind": "empty", "loc": [0, 0, 0], "rot": [0, 0, 0], "scale": [1, 1, 1]}
    spec = {"blueprint": bp_name, "nodes": [root, node], "report": sorted(set(report))}
    finish(spec, out, bp_name, scripts)
