import json, os, re, sys, math, argparse, shutil, struct, threading, urllib.request, urllib.parse, urllib.error
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal

sys.setrecursionlimit(20000)


WINDOWS_PATH_LIMIT = 259


def long_path(p):
    p = os.path.abspath(p)
    if os.name != "nt" or p.startswith("\\\\?\\"):
        return p
    if p.startswith("\\\\"):
        return "\\\\?\\UNC\\" + p[2:]
    return "\\\\?\\" + p


UE_DEFAULTS = {
    ("Add", "ConstA"): 0.0, ("Add", "ConstB"): 1.0,
    ("Subtract", "ConstA"): 1.0, ("Subtract", "ConstB"): 1.0,
    ("Multiply", "ConstA"): 0.0, ("Multiply", "ConstB"): 1.0,
    ("Divide", "ConstA"): 1.0, ("Divide", "ConstB"): 2.0,
    ("Max", "ConstA"): 0.0, ("Max", "ConstB"): 1.0,
    ("Min", "ConstA"): 0.0, ("Min", "ConstB"): 1.0,
    ("LinearInterpolate", "ConstA"): 0.0, ("LinearInterpolate", "ConstB"): 1.0,
    ("LinearInterpolate", "ConstAlpha"): 0.5,
    ("Power", "ConstExponent"): 2.0,
    ("Step", "ConstY"): 0.0, ("Step", "ConstX"): 1.0,
    ("SmoothStep", "ConstMin"): 0.0, ("SmoothStep", "ConstMax"): 1.0, ("SmoothStep", "ConstValue"): 0.0,
    ("Distance", "ConstA"): 0.0, ("Distance", "ConstB"): 0.0,
}
CONST_NAME = {"A": "ConstA", "B": "ConstB", "Alpha": "ConstAlpha", "Exponent": "ConstExponent", "X": "ConstX",
              "Y": "ConstY", "Min": "ConstMin", "Max": "ConstMax", "Value": "ConstValue"}
COMPILE_SWITCHES = {
    "QualitySwitch": ("Inputs[3]", "Default"), "FeatureLevelSwitch": ("Inputs[4]", "Default"),
    "ShadingPathSwitch": ("Inputs", "Default"), "ReflectionCapturePassSwitch": ("Default",),
    "ShaderStageSwitch": ("PixelShader",), "RayTracingQualitySwitch": ("Normal",),
    "PathTracingQualitySwitch": ("Normal",), "VirtualTextureFeatureSwitch": ("No",),
    "DistanceFieldsRenderingSwitch": ("No",), "PreviousFrameSwitch": ("CurrentFrame",),
    "NaniteReplace": ("Default",), "MaterialProxyReplace": ("Realtime",), "ShadowReplace": ("Default",),
    "RuntimeVirtualTextureReplace": ("Default",),
}
ATTRIBUTES = [
    ("BaseColor", "69B8D336-16ED4D49-9AA49729-2F050F7A", 3, (0.0, 0.0, 0.0)),
    ("Metallic", "57C3A161-7F064296-B00B24A5-A496F34C", 1, 0.0),
    ("Specular", "9FDAB399-25564CC9-8CD2D572-C12C8FED", 1, 0.5),
    ("Roughness", "D1DD967C-4CAD47D3-9E6346FB-08ECF210", 1, 0.5),
    ("Anisotropy", "55E2B4FB-C1C54DB2-9F11875F-7231EB1E", 1, 0.0),
    ("EmissiveColor", "B769B54D-D08D4440-ABC21BA6-CD27D0E2", 3, (0.0, 0.0, 0.0)),
    ("Opacity", "B8F50FBA-2A754EC1-9EF672CF-EB27BF51", 1, 1.0),
    ("OpacityMask", "679FFB17-2BB5422C-AD520483-166E0C75", 1, 1.0),
    ("Normal", "0FA2821A-200F4A4A-B719B789-C1259C64", 3, (0.0, 0.0, 1.0)),
    ("Tangent", "D5F8E9CF-CDC3468D-B10E4465-596A7BBA", 3, (1.0, 0.0, 0.0)),
    ("WorldPositionOffset", "F905F895-D5814314-916D2434-8C40CE9E", 3, (0.0, 0.0, 0.0)),
    ("SubsurfaceColor", "5B8FC679-51CE4082-9D777BEE-F4F72C44", 3, (1.0, 1.0, 1.0)),
    ("ClearCoat", "9E502E69-3C8F48FA-94645CFD-28E5428D", 1, 1.0),
    ("ClearCoatRoughness", "BE4F2FFD-12FC4296-B0124EEA-12C28D92", 1, 0.1),
    ("AmbientOcclusion", "E8EBD0AD-B1654CBE-B079C3A8-B39B9F15", 1, 1.0),
    ("Refraction", "D0B0FA03-14D74455-A851BAC5-81A0788B", 3, (1.0, 0.0, 0.0)),
    ("PixelDepthOffset", "0AC97EC3-E3D047BA-B610167D-C4D919FF", 1, 0.0),
    ("ShadingModel", "D9423FFF-D77E4D82-8FF9CF5E-055D1255", 1, 1.0),
    ("Displacement", "199A7166-C67041DC-A68EAD0D-7017D0AD", 1, 0.0),
]
ATTR_OF_GUID = {g: n for n, g, _, _ in ATTRIBUTES}
ATTR_W = {n: w for n, _, w, _ in ATTRIBUTES}
ATTR_DEFAULT = {n: d for n, _, _, d in ATTRIBUTES}
VERTEX_ATTRS = {"WorldPositionOffset", "Displacement"}
BREAK_ORDER = (["BaseColor", "Metallic", "Specular", "Roughness", "Anisotropy", "EmissiveColor", "Opacity",
                "OpacityMask", "Normal", "Tangent", "WorldPositionOffset", "SubsurfaceColor", "ClearCoat",
                "ClearCoatRoughness", "AmbientOcclusion", "Refraction"]
               + ["CustomizedUV%d" % i for i in range(8)] + ["PixelDepthOffset", "ShadingModel", "Displacement"])
MAKE_PINS = {n: n for n in BREAK_ORDER if not n.startswith("CustomizedUV")}
PIN_ATTR = dict(MAKE_PINS, CustomData0="ClearCoat", CustomData1="ClearCoatRoughness",
                ShadingModelFromMaterialExpression="ShadingModel")
SHADING_MODELS = ["MSM_Unlit", "MSM_DefaultLit"]
STATIC_BOOL_INPUT = "EFunctionInputType::FunctionInput_StaticBool"
ATTRIBUTES_INPUT = "EFunctionInputType::FunctionInput_MaterialAttributes"
XYZW = "xyzw"
KNOWN_UNSUPPORTED = {"TextureSampleParameterMovie", "FontSample", "FontSampleParameter", "LightmapUVs", "AtmosphericFogColor", "DepthOfFieldFunction"}


def T(x):
    return (x.get("Type") or "").replace("MaterialExpression", "")


def inner(s):
    return s.split("'")[1] if "'" in s else s


LOCAL_X_FLIP = True
UV_FOR_LOCAL_PANNING = True
LOCAL_UV_SPAN = 1600.0
LOCAL_TYPES = ("LocalPosition", "PreSkinnedPosition")
TEXTURE_TYPES = ("TextureSample", "TextureSampleParameter2D", "TextureSampleParameterSubUV", "TextureSampleParameterCube", "TextureSampleParameter2DArray", "TextureSampleParameterVolume", "AntialiasedTextureMask")


def linked(ref):
    return isinstance(ref, dict) and ref.get("ExpressionName") not in (None, "None")


def fl(x):
    x = float(x)
    if math.isnan(x) or math.isinf(x):
        x = 0.0
    s = format(Decimal(repr(x)), "f")
    return s if "." in s else s + ".0"


def pf(x):
    """A number as ShaderLab's Properties block can read it: plain decimal, never 1e-06."""
    x = float(x)
    if math.isnan(x) or math.isinf(x):
        x = 0.0
    t = ("%.9f" % x).rstrip("0")
    if t.endswith("."):
        t += "0"
    return "0.0" if t in ("-0.0", "") else t


def linear_to_gamma(x):
    """Same curve as Unity's Mathf.LinearToGammaSpace."""
    x = float(x)
    if x <= 0.0:
        return 0.0
    if x <= 0.0031308:
        return 12.92 * x
    if x < 1.0:
        return 1.055 * (x ** 0.4166667) - 0.055
    return x ** 0.45454545


COLOR_WORDS = re.compile(r"colou?r|tint|emissive|emission|glow|albedo|diffuse", re.I)
MOTION_WORDS = re.compile(r"speed|pann|scroll|velocity|rotat|spin|drift|flow", re.I)


def ident(s):
    s = re.sub(r"[^A-Za-z0-9_]", "_", str(s))
    return ("_" + s) if s[:1].isdigit() else s


def simple(e):
    return bool(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*|-?[0-9.eE+-]+|[A-Za-z_][A-Za-z0-9_]*\([^()]*\)", e))


def par(e):
    return e if simple(e) else "(" + e + ")"


BLEND_NAMES = {"opaque": "BLEND_Opaque", "masked": "BLEND_Masked", "translucent": "BLEND_Translucent", "additive": "BLEND_Additive",
               "modulate": "BLEND_Modulate", "alphacomposite": "BLEND_AlphaComposite", "alphaholdout": "BLEND_AlphaHoldout"}
BLEND_ORDER = ["opaque", "masked", "translucent", "additive", "modulate", "alphacomposite", "alphaholdout"]


def norm_blend(v):
    s = str(v).split("::")[-1].strip()
    if s.isdigit() and int(s) < len(BLEND_ORDER):
        return BLEND_NAMES[BLEND_ORDER[int(s)]], True
    low = s.lower().replace("blend_", "").replace(" ", "")
    if low in BLEND_NAMES:
        return BLEND_NAMES[low], True
    if low.startswith("translucent") or "transmittance" in low:
        return "BLEND_Translucent", True
    return "BLEND_Translucent", False


# Unreal feeds a Panner/Rotator with no Time input from the game clock, so by default they animate in Unity too.
# --hold-unwired-time freezes all of them; --hold-node freezes single ones (NAME or GRAPH:NAME).
ANIMATE_UNWIRED_TIME = True
# Unreal's game clock is never near zero, so effects timed from a moment (hit flashes, spawn fades) read as finished at rest.
TIME_OFFSET = 100.0
ANIMATE_NODES = set()
HOLD_NODES = set()


def node_ref(g, x):
    return "%s:%s" % (g.name.split("~")[-1], x.get("Name"))


def node_selected(names, g, x):
    return x.get("Name") in names or node_ref(g, x) in names

# Parameters that only matter inside Fortnite (first-person camera offset, hit flashes, derez, game-wide
# globals). They are kept in the shader as fixed values (static const) so the generated code is unchanged,
# but they are not Properties, so they never show in the inspector. Matched against the UE parameter name.
HARDCODE_PATTERNS = [
    r"^FP_", r"^IsSmallerCharacter$", r"^HitGlow", r"^Character_Derez", r"^NewDissolveGradient$",
    r"EyeAdaptation", r"^PrimitiveDataRim", r"^bEnableCharacterRimLight$",
    r"^Hide[_A-Z]", r"^(?:Head|Leg)_[LR]_", r"^Arm_[LR]_Radius", r"^Foot_(?:Hardness|Radius)", r"^NoHelmet$", r"^SpeedOffset$", r"Character_Vertex_Hide_Radius$", r"E,?mote", r"^TOD_", r"TimeOfDay", r"Time_?of_?Day", r"^Sunny", r"^Darkside$", r"SkyLight", r"Skybox", r"^SkyColor",
]
HARDCODE_GLOBALS = True      # every _C_ parameter-collection value (game-wide settings)
SHOW_ALL_PARAMS = False      # --show-all-params turns hiding off
SKY_MATERIAL = False         # --sky: the material is on a sky dome, so it is drawn first, behind everything, without writing depth
EXTRA_HIDE = []              # --hide REGEX (repeatable)
EXTRA_SHOW = []              # --show REGEX (repeatable): keep visible even if a rule above hides it
# Sky and atmosphere lighting only exists in the Fortnite world; these nodes are replaced by 0 on purpose.
SKY_IGNORED = {"SkyLightEnvMapSample", "SkyAtmosphereLightIlluminanceOnGround"}


def label_forms(label):
    """A parameter's name as written, with underscores for spaces, and with no spaces or underscores, so a rule
    written for "Hide_Element_00" also catches "Hide Element 00" and "HideElement00"."""
    s = str(label).strip()
    return (s, re.sub(r"\s+", "_", s), re.sub(r"[\s_]+", "", s))


def label_matches(patterns, label):
    forms = label_forms(label)
    return any(re.search(r, f, re.I) for r in patterns for f in forms)


def is_hardcoded(pname, label):
    if SHOW_ALL_PARAMS or pname.startswith("_O_"):
        return False
    if label_matches(EXTRA_SHOW, label):
        return False
    if label_matches(EXTRA_HIDE, label):
        return True
    if pname.startswith("_C_"):
        return HARDCODE_GLOBALS
    return label_matches(HARDCODE_PATTERNS, label)


def time_period_override(p):
    ov, per, seen = False, None, {}
    for k, v in p.items():
        lk = str(k).lower()
        if "period" in lk or "pause" in lk:
            seen[k] = v
        if "period" in lk and "override" in lk:
            ov = v is True or str(v).lower() in ("true", "1")
        elif lk == "period":
            try:
                per = float(v)
            except (TypeError, ValueError):
                per = None
    return ov, per, seen


class CI(dict):
    """A dict whose keys match without regard to case, like Unreal's parameter names."""

    def __init__(self, d=None):
        dict.__init__(self, d or {})
        self._l = {str(k).lower(): k for k in dict.keys(self)}

    def _k(self, k):
        return self._l.get(str(k).lower(), k)

    def __setitem__(self, k, v):
        self._l.setdefault(str(k).lower(), k)
        dict.__setitem__(self, self._k(k), v)

    def setdefault(self, k, d=None):
        if k in self:
            return self[k]
        self[k] = d
        return d

    def __contains__(self, k):
        return dict.__contains__(self, self._k(k))

    def __getitem__(self, k):
        return dict.__getitem__(self, self._k(k))

    def get(self, k, d=None):
        return dict.get(self, self._k(k), d)


class V:
    __slots__ = ("e", "w", "tex", "at", "const")

    def __init__(self, e, w, tex=None, at=None, const=False):
        self.e, self.w, self.tex, self.at, self.const = e, w, tex, at, const


class Attrs:
    def __init__(self, base=None, thunks=None):
        self.base, self.thunks, self.vals = base, dict(thunks or {}), {}

    def get(self, tr, name):
        key = (tr.mode, name)
        if key not in self.vals:
            if name in self.thunks:
                v = self.thunks[name]()
            elif self.base is not None:
                v = self.base.get(tr, name)
            else:
                v = tr.attr_default(name)
            self.vals[key] = v
        return self.vals[key]


class BlendAttrs(Attrs):
    def __init__(self, a, b, alpha, pixel, vertex):
        super().__init__()
        self.a, self.b, self.alpha, self.pixel, self.vertex = a, b, alpha, pixel, vertex

    def get(self, tr, name):
        key = (tr.mode, name)
        if key not in self.vals:
            mode = self.vertex if name in VERTEX_ATTRS else self.pixel
            if mode == "UseA":
                v = self.a.get(tr, name)
            elif mode == "UseB":
                v = self.b.get(tr, name)
            else:
                va, vb = self.a.get(tr, name), self.b.get(tr, name)
                if va.e == vb.e:
                    v = va
                else:
                    v = tr.lerp(va, vb, self.alpha())
            self.vals[key] = v
        return self.vals[key]


class Lazy:
    def __init__(self, g, ref, scope):
        self.g, self.ref, self.scope, self.v = g, ref, scope, {}

    def get(self, tr):
        if tr.mode not in self.v:
            self.v[tr.mode] = tr.input(self.g, self.ref, self.scope, None)
        return self.v[tr.mode]


class Graph:
    def __init__(self, path):
        self.path = path
        self.name = os.path.splitext(os.path.basename(path))[0]
        self.o = json.load(open(path, encoding="utf-8"))
        seen, self.dups = set(), set()
        for x in self.o:
            n = x.get("Name")
            (self.dups if n in seen else seen).add(n)
        self.by = {}
        for x in self.o:
            self.by[self.full_name(x) if x.get("Name") in self.dups else x.get("Name")] = x
        self.decl = {k: x for k, x in self.by.items() if T(x) == "NamedRerouteDeclaration"}
        self.inputs = {x["Properties"].get("Id"): x for x in self.o if T(x) == "FunctionInput"}
        self.outputs = {x["Properties"].get("Id"): x for x in self.o if T(x) == "FunctionOutput"}

    @staticmethod
    def full_name(x):
        outer = inner(str((x.get("Outer") or {}).get("ObjectName", "")))
        return "%s%s%s" % (outer, "." if ":" in outer else ":", x.get("Name"))

    def key(self, ref):
        n = ref.get("ExpressionName")
        if n in self.dups:
            return inner(str((ref.get("Expression") or {}).get("ObjectName", ""))) or n
        return n

    def editor_data(self):
        return next((x for x in self.o if T(x) == "MaterialEditorOnlyData"), None)

    def material_object(self):
        return next((x for x in self.o if x.get("Type") == "Material"), None)


class Source:
    def __init__(self, bridge=None, folder=None, cache=None):
        self.bridge = bridge.rstrip("/") if bridge else None
        self.folder = folder
        self.root = cache
        self.cache = os.path.join(cache, "_work") if cache else os.path.join(os.path.expanduser("~"), ".ue2unity_cache")
        os.makedirs(self.cache, exist_ok=True)
        self.touched = []
        self.graphs, self.index = {}, None
        self.errors = {}

    def file_away(self, shader_name):
        if not self.root:
            return 0
        dest = os.path.join(self.root, re.sub(r'[<>:"/\\|?*]', "_", shader_name))
        moved = 0
        for f in dict.fromkeys(self.touched):
            if not os.path.exists(f):
                continue
            sub = os.path.join(dest, "graphs") if os.path.basename(os.path.dirname(f)) == "graphs" else dest
            os.makedirs(sub, exist_ok=True)
            target = os.path.join(sub, os.path.basename(f))
            try:
                if os.path.exists(target):
                    os.remove(target)
                shutil.move(f, target)
                moved += 1
            except OSError:
                pass
        self.touched = []
        print("cache: %d file(s) saved in %s" % (moved, dest))
        return moved

    def _get(self, route, path):
        url = "%s/%s?%s" % (self.bridge, route, urllib.parse.urlencode({"path": path}))
        with urllib.request.urlopen(url, timeout=120) as r:
            return r.read()

    def material(self, path):
        return json.loads(self._get("material", path).decode("utf-8"))

    def local(self, path):
        if os.path.exists(path):
            return path
        dest = os.path.join(self.cache, re.sub(r"[^A-Za-z0-9._-]", "_", path)[-120:])
        if not os.path.exists(dest):
            tmp = "%s.%d.part" % (dest, threading.get_ident())
            with open(tmp, "wb") as fh:
                fh.write(self._get("file", path))
            os.replace(tmp, dest)
        self.touched.append(dest)
        return dest

    def _json(self, route, path):
        if not self.bridge or not path:
            return None
        key = (route, path)
        if key not in self.graphs:
            data, why = None, None
            for _ in range(2):
                try:
                    data = json.loads(self._get(route, path).decode("utf-8"))
                    why = None
                    break
                except urllib.error.HTTPError as e:
                    why = self._why(e)
                    if e.code == 404:
                        break
                except Exception as e:
                    why = "%s: %s" % (type(e).__name__, e)
            self.graphs[key] = data
            if data is None and why:
                self.errors[key] = why
        return self.graphs[key]

    @staticmethod
    def _why(e):
        msg = ""
        try:
            msg = str(json.loads(e.read().decode("utf-8")).get("error") or "")
        except Exception:
            pass
        return "HTTP %s%s" % (e.code, (": " + msg) if msg else "")

    def why(self, route, path):
        return self.errors.get((route, path))

    def collection(self, path):
        return self._json("collection", path)

    def texture_info(self, path):
        return self._json("texture", path)

    def prefetch_textures(self, paths):
        todo = [p for p in dict.fromkeys(paths) if p and ("texture", p) not in self.graphs]
        if len(todo) < 2 or not self.bridge:
            return
        with ThreadPoolExecutor(max_workers=min(8, len(todo))) as pool:
            list(pool.map(self.texture_info, todo))

    def named_json_files(self, name):
        files = []
        base = (name or "").strip()
        dirs = [d for d in (getattr(self, "curve_dirs", None) or []) if d]
        if self.root:
            dirs += [os.path.join(self.root, "Curves"), os.path.join(self.root, "curves"), os.path.join(self.root, "_work", "curves")]
        if self.folder:
            dirs.append(self.folder)
        for d in dirs:
            if not os.path.isdir(d):
                continue
            for r, _, fs in os.walk(d):
                for f in fs:
                    if f.lower() in (base.lower() + ".json", base.lower() + ".uasset.json"):
                        files.append(os.path.join(r, f))
        return files

    def asset_file(self, objpath):
        """An ordinary package (curve, curve atlas) dumped to JSON by the bridge: the file, or None."""
        if not (self.bridge and objpath):
            return None
        try:
            return self.local(json.loads(self._get("asset", objpath).decode("utf-8"))["file"])
        except Exception:
            return None

    def atlas(self, objpath, name):
        key = ("atlas", objpath or name)
        if key in self.graphs:
            return self.graphs[key]
        files = self.named_json_files(name)
        af = self.asset_file(objpath)
        if af:
            files.append(af)
        rows = None
        for f in files:
            try:
                raw = json.load(open(f, encoding="utf-8"))
            except Exception:
                continue
            for o in (raw if isinstance(raw, list) else [raw]):
                gc = ((o or {}).get("Properties") or {}).get("GradientCurves")
                if isinstance(gc, list) and gc:
                    rows = [c if isinstance(c, dict) else {} for c in gc]
                    break
            if rows:
                print(" atlas file: %s" % f)
                break
        self.graphs[key] = rows
        return rows

    def curve(self, objpath, name):
        key = ("curve", objpath or name)
        if key in self.graphs:
            return self.graphs[key]
        data = None
        files = self.named_json_files(name)
        af = self.asset_file(objpath)
        if af:
            files.append(af)
        for f in files:
            try:
                raw = json.load(open(f, encoding="utf-8"))
            except Exception:
                continue
            objs = raw if isinstance(raw, list) else [raw]
            for o in objs:
                pr = (o or {}).get("Properties") or {}
                fc = pr.get("FloatCurves")
                if fc is None and isinstance(pr.get("FloatCurve"), dict):
                    fc = [pr.get("FloatCurve")]
                if isinstance(fc, list):
                    chans = list(fc)
                else:
                    chans = [fc if isinstance(fc, dict) else None] + [pr.get("FloatCurves[%d]" % i) for i in (1, 2, 3)]
                chans = [c if isinstance(c, dict) else {} for c in chans[:4]]
                if any(c.get("Keys") for c in chans):
                    data = chans
                    break
            if data:
                print(" curve file: %s" % f)
                break
        self.graphs[key] = data
        return data

    def graph(self, objpath, name=None):
        key = objpath or name
        if key in self.graphs:
            return self.graphs[key]
        file = None
        if self.bridge:
            try:
                file = self.local(json.loads(self._get("graph", key).decode("utf-8"))["file"])
            except Exception:
                file = None
        if file is None and self.folder:
            if self.index is None:
                self.index = {}
                for r, _, fs in os.walk(self.folder):
                    for f in fs:
                        if f.endswith(".json"):
                            stem = os.path.splitext(f)[0]
                            self.index.setdefault(stem, os.path.join(r, f))
                            self.index.setdefault(stem.split("~")[-1], os.path.join(r, f))
            base = (name or key).split(".")[-1].split("/")[-1]
            file = self.index.get(base) or self.index.get(os.path.basename(key).split(".")[0])
        if file is None and os.path.exists(key):
            file = key
        if file and os.path.exists(file):
            keep = os.path.join(self.cache, "graphs")
            os.makedirs(keep, exist_ok=True)
            copy = os.path.join(keep, os.path.basename(file))
            try:
                if os.path.abspath(copy) != os.path.abspath(file):
                    shutil.copyfile(file, copy)
                self.touched.append(copy)
                print(" graph file: %s" % file)
                print(" graph copy: %s" % copy)
            except OSError:
                pass
        g = Graph(file) if file else None
        self.graphs[key] = g
        return g


class Translator:
    scope_ids = [0]

    def __init__(self, src, entry):
        self.src, self.entry = src, dict(entry or {})
        for _kind in ("scalars", "vectors", "textures"):
            self.entry[_kind] = CI(self.entry.get(_kind) or {})
        self.used_values = set()
        self.warnings = []
        self.textures = {}
        self.customs = []
        self.cache = {}
        self.stack = []
        self.n = 0
        self.mode = "ps"
        self.bodies = {"ps": [], "vs": []}
        self.uses = set()
        self.dpidx = set()
        self.slotprops = set()
        self.props = {}
        self.tex_trace = []
        self.override_hits = 0
        self.fn_over = {}
        self.notes = []
        self.lp_uv = None

    @property
    def body(self):
        return self.bodies[self.mode]

    def warn(self, m):
        if m not in self.warnings:
            self.warnings.append(m)

    def note(self, m):
        if m not in self.notes:
            self.notes.append(m)

    def const(self, x, w=1):
        if w == 1:
            if isinstance(x, (tuple, list)):
                x = x[0]
            return V(fl(x), 1, const=True)
        x = tuple(x) + (0.0,) * (w - len(x))
        return V("float%d(%s)" % (w, ", ".join(fl(c) for c in x[:w])), w, const=True)

    def scalar_prop(self, name, default, tag="_S_"):
        pname = re.sub(r"_+", "_", tag + ident(name))
        self.props.setdefault(pname, {"kind": "float", "default": float(default), "label": str(name)})
        return V(pname, 1)

    def vector_prop(self, name, rgba, tag="_V_"):
        rgba = [float(c) for c in list(rgba)[:4]] + [1.0] * (4 - len(list(rgba)[:4]))
        pname = re.sub(r"_+", "_", tag + ident(name))
        color = tag == "_V_" and bool(COLOR_WORDS.search(str(name))) and not MOTION_WORDS.search(str(name))
        self.props.setdefault(pname, {"kind": "vector", "default": rgba, "label": str(name), "color": color})
        return V(pname, 4)

    def collection_value(self, p):
        name = p.get("ParameterName")
        ref = p.get("Collection")
        key = nm = ""
        if isinstance(ref, dict):
            nm = inner(str(ref.get("ObjectName", "")))
            key = ref.get("ObjectPath") or nm
            m = re.match(r"(.*)\.(\d+)$", key)
            if m:
                key = "%s.%s" % (m.group(1), nm.split(":")[-1].split(".")[-1])
        c = self.src.collection(key) or self.src.collection(nm)
        if c:
            if name in (c.get("scalars") or {}):
                return self.scalar_prop(name, c["scalars"][name], "_C_")
            if name in (c.get("vectors") or {}):
                v = self.vector_prop(name, c["vectors"][name], "_C_")
                return self.tmp("%s.rgb" % v.e, 3)
        self.warn("stand-in 0: parameter collection %s (%s not read from the app)" % (name, nm or key))
        return self.const((0.0, 0.0, 0.0), 3)

    def tmp(self, e, w):
        self.n += 1
        name = "v%d" % self.n
        self.body.append("float%s %s = %s;" % ("" if w == 1 else w, name, e))
        return V(name, w)

    def fit(self, v, w):
        if v.tex is not None or v.at is not None:
            return self.const(0.0, w)
        if v.w == w:
            return v
        if v.w == 1:
            return V("float%d(%s)" % (w, ", ".join([par(v.e)] * w)) if w > 1 else v.e, w, const=v.const)
        if w < v.w:
            return V("%s.%s" % (par(v.e), XYZW[:w]), w, const=v.const)
        pad = ", ".join(["0.0"] * (w - v.w))
        return V("float%d(%s, %s)" % (w, v.e, pad), w, const=v.const)

    def mask(self, v, idx):
        if not idx:
            return self.const(0.0)
        idx = [i for i in idx if i < max(v.w, 1)] or [0]
        if v.w == 1:
            return v if len(idx) == 1 else self.fit(v, len(idx))
        return self.tmp("%s.%s" % (par(v.e), "".join(XYZW[i] for i in idx)), len(idx))

    def comp(self, v, i):
        return v.e if v.w == 1 else "%s.%s" % (par(v.e), XYZW[min(i, v.w - 1)])

    def combine(self, parts):
        w = len(parts)
        if w == 1:
            return parts[0]
        return self.tmp("float%d(%s)" % (w, ", ".join(p.e if p.w == 1 else self.comp(p, 0) for p in parts)), w)

    def match(self, a, b):
        a, b = self.fit(a, a.w) if a.tex is None else a, self.fit(b, b.w) if b.tex is None else b
        w = max(a.w, b.w) if (a.w == 1 or b.w == 1) else min(a.w, b.w)
        return (a if a.w == w or a.w == 1 else self.fit(a, w)), (b if b.w == w or b.w == 1 else self.fit(b, w)), w

    def binop(self, op, a, b):
        a, b, w = self.match(a, b)
        return self.tmp("%s %s %s" % (par(a.e), op, par(b.e)), w)

    def call2(self, fn, a, b):
        a, b, w = self.match(a, b)
        if a.w != w:
            a = self.fit(a, w)
        if b.w != w:
            b = self.fit(b, w)
        return self.tmp("%s(%s, %s)" % (fn, a.e, b.e), w)

    def call1(self, fn, a):
        return self.tmp("%s(%s)" % (fn, a.e), a.w)

    def lerp(self, a, b, t):
        a, b, w = self.match(a, b)
        a, b = self.fit(a, w), self.fit(b, w)
        t = t if t.w in (1, w) else self.fit(t, w)
        return self.tmp("lerp(%s, %s, %s)" % (a.e, b.e, t.e), w)

    def attr_default(self, name):
        d = ATTR_DEFAULT.get(name, 0.0)
        return self.const(d, ATTR_W.get(name, 1)) if isinstance(d, tuple) else self.const(d)

    def lflip(self, e):
        if self.mode == "ps" and LOCAL_X_FLIP:
            return self.tmp("float3(-%s.x, %s.y, %s.z)" % ((par(e),) * 3), 3)
        return None

    def lin(self, v):
        f = self.lflip(v.e)
        return f if f is not None else v

    def lout(self, v):
        return self.lin(v)

    def local_uv_pair(self, g, ref):
        if not UV_FOR_LOCAL_PANNING or self.mode != "ps" or not linked(ref):
            return None
        seen, found = set(), {"local": False, "time": False}

        def walk(r):
            n = g.key(r)
            if n in seen or n not in g.by:
                return
            seen.add(n)
            x = g.by[n]
            t = T(x)
            if t in TEXTURE_TYPES:
                return
            if t in LOCAL_TYPES:
                found["local"] = True
            elif t == "Time":
                found["time"] = True
            for v in (x.get("Properties") or {}).values():
                if linked(v):
                    walk(v)

        walk(ref)
        if not (found["local"] and found["time"]):
            return None
        # The two channels the coordinate really uses sit on a ComponentMask that can be several math nodes down
        # (Reroute, Add of a constant, Multiply by a scale ...). Only looking at the top node used to miss an .xz mask
        # and gave the pattern X and Y instead of X and Z, which left it constant in one direction.
        pass_through = ("Reroute", "Add", "Subtract", "Multiply", "Divide", "OneMinus", "Frac")

        def find_mask(r, depth):
            x = g.by.get(g.key(r)) if linked(r) else None
            if x is None or depth > 12:
                return None
            t = T(x)
            props = x.get("Properties") or {}
            if t == "ComponentMask":
                idx = [i for i, k in enumerate("RGB") if props.get(k)]
                if len(idx) == 2:
                    return tuple(idx)
                return None
            if t in pass_through:
                for v in props.values():
                    if linked(v):
                        m = find_mask(v, depth + 1)
                        if m is not None:
                            return m
            return None

        return find_mask(ref, 0) or (0, 1)

    def local_uv_surrogate(self):
        uv = self.uvs(0)
        c = ["0.0", "0.0", "0.0"]
        c[self.lp_uv[0]] = "%s.x * FPU_UV_SPAN" % par(uv.e)
        c[self.lp_uv[1]] = "%s.y * FPU_UV_SPAN" % par(uv.e)
        self.note("LocalPosition driving a time-animated texture lookup is replaced by the mesh UV (spanning %g cm), so the pattern keeps its size when the model is scaled in Unity" % LOCAL_UV_SPAN)
        return self.tmp("float3(%s)" % ", ".join(c), 3)

    def local_pos(self):
        if self.lp_uv is not None and self.mode == "ps":
            return self.local_uv_surrogate()
        if self.mode == "ps":
            if LOCAL_X_FLIP:
                return self.tmp("float3(-IN.objPos.x, IN.objPos.z, IN.objPos.y) * 100.0", 3)
            return self.tmp("IN.objPos.xzy * 100.0", 3)
        return self.tmp("vpos.xzy * 100.0", 3)

    def input(self, g, ref, scope, default):
        if not linked(ref):
            return default
        v = self.expr(g, g.key(ref), ref.get("OutputIndex", 0) or 0, scope)
        if ref.get("Mask"):
            v = self.mask(v, [i for i, k in enumerate(("MaskR", "MaskG", "MaskB", "MaskA")) if ref.get(k)])
        return v

    def pin(self, g, x, name, scope, fallback=0.0):
        p = x.get("Properties") or {}
        cn = CONST_NAME.get(name)
        d = p.get(cn) if cn and cn in p and not isinstance(p.get(cn), dict) else UE_DEFAULTS.get((T(x), cn), fallback)
        v = self.input(g, p.get(name), scope, self.const(d))
        if isinstance(v.at, Attrs):
            v = v.at.get(self, "BaseColor")
        return v

    def expr(self, g, name, out, scope):
        key = (self.mode, g.path, name, out, scope.get("_id", 0), self.lp_uv)
        if key in self.cache:
            return self.cache[key]
        if key in self.stack:
            self.warn("cycle in %s at %s" % (g.name, name))
            return self.const(0.0)
        self.stack.append(key)
        try:
            x = g.by[name]
            t = T(x)
            if t == "Reroute":
                v = self.input(g, x["Properties"].get("Input"), scope, self.const(0.0))
            elif t == "NamedRerouteUsage":
                d = self.declaration(g, x["Properties"])
                v = self.input(g, d["Properties"].get("Input"), scope, self.const(0.0)) if d else self.const(0.0)
            elif t == "NamedRerouteDeclaration":
                v = self.input(g, x["Properties"].get("Input"), scope, self.const(0.0))
            else:
                body = self.bodies[self.mode]
                start = len(body)
                v = self.evaluate(g, x, t, out, scope)
                if ANNOTATE:
                    for i in range(start, len(body)):
                        line = body[i]
                        if line and "//" not in line and not line.lstrip().startswith("#"):
                            body[i] = "%s  // %s %s [%s]" % (line, t, name, g.name)
        finally:
            self.stack.pop()
        self.cache[key] = v
        return v

    @staticmethod
    def declaration(g, p):
        if p.get("DeclarationGuid"):
            d = next((d for d in g.decl.values() if d["Properties"].get("VariableGuid") == p.get("DeclarationGuid")), None)
            if d is not None:
                return d
        if "Declaration" in p:
            full = inner(str(p["Declaration"].get("ObjectName", "")))
            return g.decl.get(full) or g.decl.get(full.split(":")[-1].split(".")[-1])
        return None

    def switch_flag(self, name, default):
        return bool(self.entry.get("switches", {}).get(name, default))

    def static_bool(self, g, ref, scope):
        if not linked(ref):
            return None
        x = g.by.get(g.key(ref))
        if x is None:
            return None
        t, p = T(x), x.get("Properties") or {}
        if t in ("Reroute", "NamedRerouteDeclaration"):
            return self.static_bool(g, p.get("Input"), scope)
        if t == "NamedRerouteUsage":
            d = self.declaration(g, p)
            return self.static_bool(g, d["Properties"].get("Input"), scope) if d else None
        if t == "StaticBool":
            return bool(p.get("Value", False))
        if t == "StaticBoolParameter":
            return self.switch_flag(p.get("ParameterName"), bool(p.get("DefaultValue", False)))
        if t in ("StaticSwitch", "StaticSwitchParameter"):
            return self.static_bool(g, p.get("A" if self.switch_value(g, x, scope) else "B"), scope)
        if t in COMPILE_SWITCHES:
            return self.static_bool(g, self.switch_pin(x), scope)
        if t == "FunctionInput":
            bound = scope.get(p.get("Id"))
            v = self.static_bool(bound.g, bound.ref, bound.scope) if isinstance(bound, Lazy) else None
            if v is None:
                v = self.static_bool(g, p.get("Preview"), scope)
            if v is None:
                v = float((p.get("PreviewValue") or {}).get("X", 0.0)) != 0.0
            return v
        if t == "MaterialFunctionCall":
            f = self.function_graph(x)
            if f is None:
                return None
            fg = f[1]
            fo = self.function_output(fg, p.get("FunctionOutputs", []), ref.get("OutputIndex", 0) or 0)
            if fo is None:
                return None
            sub = {fi.get("ExpressionInputId"): Lazy(g, fi.get("Input"), scope) for fi in p.get("FunctionInputs", [])}
            sub["_id"] = ("static", id(x), g.path, scope.get("_id", 0))
            return self.static_bool(fg, fo["Properties"].get("A"), sub)
        return None

    def switch_value(self, g, x, scope):
        p = x.get("Properties") or {}
        if T(x) == "StaticSwitchParameter":
            return self.switch_flag(p.get("ParameterName"), bool(p.get("DefaultValue", False)))
        v = self.static_bool(g, p.get("Value"), scope)
        return bool(p.get("DefaultValue", False)) if v is None else v

    @staticmethod
    def switch_pin(x):
        p = x.get("Properties") or {}
        prefs = COMPILE_SWITCHES[T(x)]
        for k in prefs:
            if linked(p.get(k)):
                return p[k]
        return p.get(prefs[-1])

    def function_graph(self, x):
        mf = x["Properties"].get("MaterialFunction") or {}
        fname = inner(mf.get("ObjectName", ""))
        path = mf.get("ObjectPath", "")
        g = self.src.graph(path, fname)
        over = {"scalars": CI(), "vectors": CI(), "textures": CI()}
        hops = 0
        while g is not None and not g.outputs and hops < 8:
            inst = next((o for o in g.o if "Parent" in (o.get("Properties") or {})), None)
            parent = ((inst or {}).get("Properties") or {}).get("Parent")
            if not isinstance(parent, dict) or not parent.get("ObjectName"):
                break
            self.collect_overrides(inst["Properties"], over)
            pname = inner(parent.get("ObjectName", ""))
            pg = self.src.graph(parent.get("ObjectPath", ""), pname)
            self.warn("function instance %s -> parent %s" % (fname, pname))
            fname, g, hops = pname, pg, hops + 1
        if (g is None or not g.outputs) and fname.endswith("_Inst"):
            base = fname[:-5]
            bg = self.src.graph(re.sub(r"_Inst(?=\.|$)", "", path), base)
            if bg is not None and bg.outputs:
                self.warn("APPROXIMATED: function instance %s has no usable data; used %s with its default values, any overrides the instance set are lost" % (fname, base))
                fname, g = base, bg
        if g is None or not g.outputs:
            return None
        for kind, vals in over.items():
            store = self.fn_over.setdefault(g.name, {"scalars": CI(), "vectors": CI(), "textures": CI()})[kind]
            for k, v in vals.items():
                store.setdefault(k, v)
        return (fname, g)

    @staticmethod
    def collect_overrides(props, over):
        def pname(e):
            info = e.get("ParameterInfo") or {}
            return info.get("Name") or e.get("ParameterName")
        for e in props.get("ScalarParameterValues") or []:
            if pname(e) is not None and isinstance(e.get("ParameterValue"), (int, float)):
                over["scalars"].setdefault(pname(e), float(e["ParameterValue"]))
        for e in props.get("VectorParameterValues") or []:
            v = e.get("ParameterValue")
            if pname(e) is not None and isinstance(v, dict):
                over["vectors"].setdefault(pname(e), (v.get("R", v.get("X", 0.0)), v.get("G", v.get("Y", 0.0)), v.get("B", v.get("Z", 0.0)), v.get("A", v.get("W", 1.0))))
        for e in props.get("TextureParameterValues") or []:
            v = e.get("ParameterValue")
            if pname(e) is not None and isinstance(v, dict):
                key = v.get("ObjectPath") or inner(str(v.get("ObjectName", "")))
                m = re.match(r"(.*)\.(\d+)$", key)
                if m:
                    key = "%s.%s" % (m.group(1), inner(str(v.get("ObjectName", ""))).split(":")[-1].split(".")[-1])
                if key:
                    over["textures"].setdefault(pname(e), key)

    @staticmethod
    def function_output(fg, outs, out):
        oid = outs[out]["ExpressionOutputId"] if out < len(outs) else None
        fo = fg.outputs.get(oid)
        if fo is None:
            fo = list(fg.outputs.values())[out] if out < len(fg.outputs) else None
        return fo

    def call(self, g, x, out, scope):
        p = x["Properties"]
        f = self.function_graph(x)
        if f is None:
            self.warn("NOT CONVERTED: function %s has no usable graph and was replaced by 0" % inner((p.get("MaterialFunction") or {}).get("ObjectName", "?")))
            return self.const(0.0)
        fname, fg = f
        fo = self.function_output(fg, p.get("FunctionOutputs", []), out)
        if fo is None:
            return self.const(0.0)
        sub = {fi.get("ExpressionInputId"): Lazy(g, fi.get("Input"), scope) for fi in p.get("FunctionInputs", [])}
        Translator.scope_ids[0] += 1
        sub["_id"] = (Translator.scope_ids[0], id(x))
        return self.input(fg, fo["Properties"].get("A"), sub, self.const(0.0))

    def uvs(self, i=0):
        self.uses.add("uv")
        if self.mode == "ps":
            base = "IN.tc01." + ("xy" if i == 0 else "zw")
        else:
            base = "uv%d" % (0 if i == 0 else 1)
        return self.tmp("float2(%s.x, 1.0 - %s.y)" % (base, base), 2)

    def tex_prop(self, key, param=None):
        key = str(key)
        short = key.rsplit("/", 1)[-1].split(".")[0]
        if key not in self.textures:
            prop = re.sub(r"_+", "_", "_" + ident(short))
            used = {v["prop"] for v in self.textures.values()}
            base, k = prop, 1
            while prop in used:
                k += 1
                prop = "%s_%d" % (base, k)
            self.textures[key] = {"prop": prop, "path": key, "param": param}
        return self.textures[key]["prop"]

    def tex_ref(self, p, node=None, graph=None):
        t = p.get("Texture")
        key = ""
        if isinstance(t, dict):
            key = t.get("ObjectPath") or inner(str(t.get("ObjectName", "")))
            nm = inner(str(t.get("ObjectName", "")))
            m = re.match(r"(.*)\.(\d+)$", key)
            if m:
                key = "%s.%s" % (m.group(1), nm.split(":")[-1].split(".")[-1])
        pn = p.get("ParameterName")
        declared = key
        if pn and pn in (self.entry.get("textures") or {}):
            key = self.entry["textures"][pn]
        elif pn and graph and pn in self.fn_over.get(graph, {}).get("textures", {}):
            key = self.fn_over[graph]["textures"][pn]
        rec = {"node": node, "graph": graph, "param": pn, "declared": declared, "used": key,
               "overridden": bool(declared and key != declared)}
        if rec not in self.tex_trace:
            self.tex_trace.append(rec)
        return key, pn

    def texture_report(self):
        """Which nodes feed each texture slot, and every place an override changed the texture."""
        lines, warns = [], []
        slots = {}
        for r in self.tex_trace:
            if r["used"]:
                slots.setdefault(r["used"], []).append(r)
        for key, rs in slots.items():
            prop = self.textures.get(key, {}).get("prop", "?")
            names = sorted({str(r["param"] or r["node"]) for r in rs})
            lines.append("  %s = %s  <- %s" % (prop, os.path.basename(key).split(".")[0], ", ".join(names)))
            params = {r["param"] for r in rs if r["param"]}
            if len(params) > 1:
                warns.append("texture slot %s is shared by different parameters (%s): check that they should be one texture"
                             % (prop, ", ".join(sorted(map(str, params)))))
            declared = {r["declared"] for r in rs if r["declared"]}
            if len(declared) > 1:
                warns.append("texture slot %s merges %d different graph textures (%s)"
                             % (prop, len(declared), ", ".join(os.path.basename(d).split(".")[0] for d in sorted(declared))))
        for r in self.tex_trace:
            if r["overridden"]:
                warns.append("override replaced texture %s with %s for parameter %s (%s)"
                             % (os.path.basename(r["declared"]).split(".")[0], os.path.basename(r["used"]).split(".")[0],
                                r["param"], r["graph"]))
        return lines, warns

    def sample(self, key, pn, coords, out, sampler, seam_safe=False):
        prop = self.tex_prop(key, pn)
        if "Normal" in (sampler or ""):
            self.textures[str(key)]["normal"] = True
        uv = self.fit(coords, 2)
        ck = ("raw", self.mode, prop, uv.e, bool(seam_safe))
        if ck in self.cache:
            raw = self.cache[ck]
        else:
            flip = self.tmp("float2(%s.x, 1.0 - %s.y)" % (par(uv.e), par(uv.e)), 2)
            if self.mode == "vs":
                raw = self.tmp("tex2Dlod(%s, float4(%s, 0.0, 0.0))" % (prop, flip.e), 4)
            elif seam_safe:
                gx = self.tmp("ddx(%s)" % flip.e, 2)
                gy = self.tmp("ddy(%s)" % flip.e, 2)
                gx = self.tmp("(dot(%s, %s) > 0.04) ? float2(0.0, 0.0) : %s" % (gx.e, gx.e, gx.e), 2)
                gy = self.tmp("(dot(%s, %s) > 0.04) ? float2(0.0, 0.0) : %s" % (gy.e, gy.e, gy.e), 2)
                raw = self.tmp("tex2Dgrad(%s, %s, %s, %s)" % (prop, flip.e, gx.e, gy.e), 4)
            else:
                raw = self.tmp("tex2D(%s, %s)" % (prop, flip.e), 4)
            self.cache[ck] = raw
        if "Normal" in (sampler or ""):
            rgb = self.tmp("UnpackNormal(%s)" % raw.e, 3)
            a = self.tmp("1.0", 1) if False else self.const(1.0)
            return self.rgba_out(rgb, a, out)
        return self.rgba_out(self.tmp("%s.rgb" % raw.e, 3), self.tmp("%s.a" % raw.e, 1), out)

    def texture_kind(self, t, p, key):
        if t == "TextureSampleParameterCube":
            return "cube"
        if t == "TextureSampleParameter2DArray":
            return "array"
        if t == "TextureSampleParameterVolume":
            return "volume"
        tex = p.get("Texture")
        name = str(tex.get("ObjectName", "")) if isinstance(tex, dict) else str(tex or "")
        for word, kind in (("TextureCube", "cube"), ("VolumeTexture", "volume"), ("Texture2DArray", "array")):
            if word in name:
                return kind
        if not key:
            return "2d"
        try:
            info = self.src.texture_info(key) or {}
        except Exception:
            info = {}
        label = str(info.get("Kind", "2d")).lower()
        for word, kind in (("cube", "cube"), ("vol", "volume"), ("array", "array")):
            if word in label:
                return kind
        return "2d"

    def sheet_depth(self, key, what, name):
        try:
            info = self.src.texture_info(key) or {}
            depth = int(info.get("Depth") or 0)
        except Exception:
            depth = 0
        if depth < 2:
            self.warn("stand-in: %s %s sampled as a flat image (the app gave no slice count for it)" % (what, name))
            return 0
        return depth

    def sheet_uv(self, x, y, slice_e, depth):
        v = self.tmp("(%s + frac(%s)) / %s" % (slice_e, y, fl(float(depth))), 1)
        return self.tmp("float2(%s, %s)" % (x, v.e), 2)

    def sample_sheet(self, kind, key, pn, coords, out, sampler, node):
        name = os.path.basename(str(key)).split(".")[0]
        prop = self.tex_prop(key, pn)
        self.textures[str(key)]["sheet"] = True
        if kind == "cube":
            self.note("cube texture %s is read as one flat panorama (longitude/latitude lookup), as the Blender add-on does" % name)
            if coords is None:
                n, c = self.pixel_normal(), self.camera_vector()
                d = self.tmp("2.0 * dot(%s, %s) * %s - %s" % (n.e, c.e, n.e, c.e), 3)
            else:
                d = self.fit(coords, 3)
            d = self.tmp("normalize(%s)" % d.e, 3)
            u = self.tmp("atan2(%s, %s) / 6.2831853 + 0.5" % (self.comp(d, 1), self.comp(d, 0)), 1)
            v = self.tmp("0.5 - asin(clamp(%s, -1.0, 1.0)) / 3.1415927" % self.comp(d, 2), 1)
            return self.sample(key, pn, self.tmp("float2(%s, %s)" % (u.e, v.e), 2), out, sampler)
        what = "texture array" if kind == "array" else "volume texture"
        depth = self.sheet_depth(key, what, name)
        uv = self.fit(coords, 3) if coords is not None else self.uvs(0)
        if depth == 0:
            return self.sample(key, pn, self.fit(uv, 2), out, sampler)
        self.note("%s %s is read as a vertical stack of %d slices, slice 0 on top, as the Blender add-on does" % (what, name, depth))
        uv = self.tmp(self.fit(uv, 3).e, 3)
        x, y, z = self.comp(uv, 0), self.comp(uv, 1), self.comp(uv, 2)
        top = fl(float(depth - 1))
        if kind == "array":
            sl = self.tmp("clamp(floor(%s + 0.5), 0.0, %s)" % (z, top), 1)
            return self.sample(key, pn, self.sheet_uv(x, y, sl.e, depth), out, sampler)
        zz = self.tmp("frac(%s) * %s - 0.5" % (z, fl(float(depth))), 1)
        lo = self.tmp("floor(%s)" % zz.e, 1)
        f = self.tmp("%s - %s" % (zz.e, lo.e), 1)
        s0 = self.tmp("fmod(%s + %s, %s)" % (lo.e, fl(float(depth)), fl(float(depth))), 1)
        s1 = self.tmp("fmod(%s + %s, %s)" % (lo.e, fl(float(depth + 1)), fl(float(depth))), 1)
        a = self.sample(key, pn, self.sheet_uv(x, y, s0.e, depth), 5, sampler)
        b = self.sample(key, pn, self.sheet_uv(x, y, s1.e, depth), 5, sampler)
        m = self.tmp("lerp(%s, %s, %s)" % (a.e, b.e, f.e), 4)
        return self.rgba_out(self.tmp("%s.rgb" % m.e, 3), self.tmp("%s.a" % m.e, 1), out)

    def rgba_out(self, rgb, a, out):
        if out == 5:
            return self.tmp("float4(%s, %s)" % (rgb.e, a.e), 4)
        if out == 4:
            return a
        if out == 0:
            return rgb
        return self.mask(rgb, [out - 1])

    def world_ue(self):
        self.uses.add("wpos")
        if self.mode == "vs":
            return self.tmp("mul(unity_ObjectToWorld, float4(vpos, 1.0)).xzy * 100.0", 3)
        return self.tmp("IN.worldPos.xzy * 100.0", 3)

    def camera_vector(self):
        self.uses.add("wpos")
        if self.mode == "vs":
            return self.tmp("normalize(_WorldSpaceCameraPos - mul(unity_ObjectToWorld, float4(vpos, 1.0)).xyz).xzy", 3)
        return self.tmp("normalize(_WorldSpaceCameraPos - IN.worldPos).xzy", 3)

    def pixel_normal(self):
        self.uses.add("wnrm")
        if self.mode == "vs":
            return self.tmp("normalize(UnityObjectToWorldNormal(vnrm)).xzy", 3)
        return self.tmp("normalize(WorldNormalVector(IN, float3(0.0, 0.0, 1.0)) + 0.000001).xzy", 3)

    def object_normal(self):
        if self.mode == "vs":
            return self.tmp("vnrm.xzy", 3)
        self.uses.add("tangent")
        wn = self.tmp("WorldNormalVector(IN, float3(0.0, 0.0, 1.0))", 3)
        on = self.tmp("normalize(mul(%s, (float3x3)unity_ObjectToWorld)).xzy" % wn.e, 3)
        return self.lout(on)

    def vertex_tangent(self):
        if self.mode == "vs":
            return self.tmp("normalize(UnityObjectToWorldDir(vtan.xyz)).xzy", 3)
        self.uses.add("tangent")
        wt = self.tmp("WorldNormalVector(IN, float3(1.0, 0.0, 0.0))", 3)
        return self.tmp("normalize(%s).xzy" % wt.e, 3)

    def screen_pos(self):
        self.uses.add("screen")
        if self.mode == "vs":
            return "ComputeScreenPos(UnityObjectToClipPos(float4(vpos, 1.0)))"
        return "IN.screenPos"

    def evaluate(self, g, x, t, out, scope):
        if isinstance(t, str) and (t.startswith("Particle") or t == "DynamicParameter"):
            self.uses.add("particle")
        p = x.get("Properties") or {}
        P = lambda n, **k: self.pin(g, x, n, scope, **k)
        C = self.const

        if t in ("Add", "Subtract", "Multiply"):
            return self.binop({"Add": "+", "Subtract": "-", "Multiply": "*"}[t], P("A"), P("B"))
        if t == "Divide":
            b = P("B")
            if b.const:
                try:
                    nums = re.findall(r"-?\d+(?:\.\d*)?(?:[eE][-+]?\d+)?", b.e)
                    if nums and all(float(n) == 0.0 for n in nums) and re.fullmatch(r"[\s\d.,()+\-eEfloat]*", b.e):
                        b = C(0.00001)
                except ValueError:
                    pass
            elif b.w >= 1:
                # UE turns x/0 into a harmless huge value and cleans NaN/Inf before it reaches the screen; Unity lets NaN/Inf
                # through (black squares, bloom flares) and warns "division by zero" when the compiler can see the zero.
                b = self.tmp("FPU_NZ(%s)" % b.e, b.w)
            return self.binop("/", P("A"), b)
        if t in ("Max", "Min"):
            return self.call2(t.lower(), P("A"), P("B"))
        if t == "Power":
            base = self.call2("max", P("Base", fallback=1.0), C(0.0))
            return self.call2("pow", base, P("Exponent"))
        if t == "LinearInterpolate":
            return self.lerp(P("A"), P("B"), P("Alpha"))
        if t == "OneMinus":
            a = P("Input")
            return self.tmp("1.0 - %s" % par(a.e), a.w)
        if t == "Saturate":
            a = P("Input")
            return self.call1("saturate", a)
        if t in ("Abs", "Floor", "Ceil", "Frac", "Round", "Sign"):
            return self.call1({"Abs": "abs", "Floor": "floor", "Ceil": "ceil", "Frac": "frac", "Round": "round", "Sign": "sign"}[t], P("Input"))
        if t in ("Truncate", "TruncateLWC"):
            return self.call1("trunc", P("Input"))
        if t in ("Sine", "Cosine", "Tangent"):
            a = P("Input")
            period = float(p.get("Period", 1.0))
            if period > 0:
                a = self.binop("*", a, C(2 * math.pi / period))
            return self.call1({"Sine": "sin", "Cosine": "cos", "Tangent": "tan"}[t], a)
        if t in ("Arcsine", "ArcsineFast"):
            return self.call1("asin", P("Input"))
        if t in ("Arccosine", "ArccosineFast"):
            return self.call1("acos", P("Input"))
        if t in ("Arctangent", "ArctangentFast"):
            return self.call1("atan", P("Input"))
        if t in ("Arctangent2", "Arctangent2Fast"):
            return self.call2("atan2", P("Y"), P("X"))
        if t == "SquareRoot":
            return self.call1("sqrt", self.call2("max", P("Input"), C(0.0)))
        if t == "Exponential":
            return self.call1("exp", P("Input"))
        if t == "Exponential2":
            return self.call1("exp2", P("Input"))
        if t in ("Logarithm", "Logarithm2", "Logarithm10"):
            v = self.input(g, p.get("X") if "X" in p else p.get("Input"), scope, C(1.0))
            return self.call1({"Logarithm": "log", "Logarithm2": "log2", "Logarithm10": "log10"}[t], v)
        if t == "Normalize":
            a = self.fit(P("VectorInput"), max(2, min(3, P("VectorInput").w)))
            if not re.fullmatch(r"\w+", a.e):
                a = self.tmp(a.e, a.w)
            return self.tmp("%s * rsqrt(max(dot(%s, %s), 1e-20))" % (a.e, a.e, a.e), a.w)
        if t == "DotProduct":
            a, b, w = self.match(P("A"), P("B"))
            w = min(w, 3)
            return self.tmp("dot(%s, %s)" % (self.fit(a, w).e, self.fit(b, w).e), 1)
        if t == "CrossProduct":
            return self.tmp("cross(%s, %s)" % (self.fit(P("A"), 3).e, self.fit(P("B"), 3).e), 3)
        if t == "Distance":
            a, b, w = self.match(P("A"), P("B"))
            return self.tmp("distance(%s, %s)" % (a.e, b.e), 1)
        if t == "Length":
            v = P("Input")
            return self.tmp("length(%s)" % v.e, 1)
        if t in ("Fmod", "Modulo"):
            return self.call2("fmod", P("A"), P("B"))
        if t == "Step":
            return self.call2("step", P("Y"), P("X")) if False else self.call2("step", P("Y"), P("X"))
        if t == "SmoothStep":
            mn, mx, v = P("Min"), P("Max"), P("Value")
            w = max(mn.w, mx.w, v.w)
            e_mn, e_mx, e_v = self.fit(mn, w).e, self.fit(mx, w).e, self.fit(v, w).e
            if e_mn == e_mx:        # equal edges: smoothstep divides by zero; UE's result is the same hard 0/1 jump step() gives
                return self.tmp("step(%s, %s)" % (e_mn, e_v), w)
            return self.tmp("smoothstep(%s, %s, %s)" % (e_mn, e_mx, e_v), w)
        if t == "Clamp":
            v = P("Input")
            mn = self.input(g, p.get("Min"), scope, C(float(p.get("MinDefault", 0.0))))
            mx = self.input(g, p.get("Max"), scope, C(float(p.get("MaxDefault", 1.0))))
            mode = str(p.get("ClampMode", "CMODE_Clamp")).split("::")[-1]
            if mode != "CMODE_ClampMax":
                v = self.call2("max", v, mn)
            if mode != "CMODE_ClampMin":
                v = self.call2("min", v, mx)
            return v
        if t == "InverseLinearInterpolate":
            a = self.input(g, p.get("A"), scope, C(float(p.get("ConstA", 0.0))))
            b = self.input(g, p.get("B"), scope, C(float(p.get("ConstB", 1.0))))
            v = self.input(g, p.get("Value"), scope, C(float(p.get("ConstValue", 0.0))))
            r = self.binop("/", self.binop("-", v, a), self.binop("-", b, a))
            return self.call1("saturate", r) if p.get("bClampResult") else r
        if t == "ConstantBiasScale":
            v = self.input(g, p.get("Input"), scope, C(0.0))
            return self.binop("*", self.binop("+", v, C(float(p.get("Bias", 1.0)))), C(float(p.get("Scale", 0.5))))
        if t == "Desaturation":
            c = self.fit(P("Input"), 3)
            lf = p.get("LuminanceFactors") or {}
            lum = self.const((lf.get("R", 0.3), lf.get("G", 0.59), lf.get("B", 0.11)), 3)
            grey = self.tmp("dot(%s, %s)" % (c.e, lum.e), 1)
            fr = self.input(g, p.get("Fraction"), scope, None)
            return grey if fr is None else self.lerp(c, grey, fr)
        if t == "ComponentMask":
            return self.mask(P("Input"), [i for i, k in enumerate("RGBA") if p.get(k)])
        if t == "AppendVector":
            a, b = P("A"), P("B")
            pa = [V(self.comp(a, i), 1) for i in range(a.w)]
            pb = [V(self.comp(b, i), 1) for i in range(b.w)]
            parts = (pa + pb)[:4]
            return self.tmp("float%d(%s)" % (len(parts), ", ".join(q.e for q in parts)), len(parts))
        if t == "Constant":
            return C(p.get("R", 0.0))
        if t == "Constant2Vector":
            return C((p.get("R", 0.0), p.get("G", 0.0)), 2)
        if t in ("Constant3Vector", "Constant4Vector"):
            c = p.get("Constant", {})
            if t == "Constant4Vector":
                return C((c.get("R", 0.0), c.get("G", 0.0), c.get("B", 0.0), c.get("A", 0.0)), 4)
            return C((c.get("R", 0.0), c.get("G", 0.0), c.get("B", 0.0)), 3)
        if t == "ScalarParameter":
            name = p.get("ParameterName")
            if name in (self.entry.get("scalars") or {}):
                self.override_hits += 1
                self.used_values.add(("scalar", str(name).lower()))
            fo = self.fn_over.get(g.name, {}).get("scalars", {})
            return self.scalar_prop(name, self.entry.get("scalars", {}).get(name, fo.get(name, p.get("DefaultValue", 0.0))))
        if t in ("VectorParameter", "DoubleVectorParameter"):
            d = p.get("DefaultValue", {})
            dflt = [d.get("R", d.get("X", 0.0)), d.get("G", d.get("Y", 0.0)), d.get("B", d.get("Z", 0.0)), d.get("A", d.get("W", 0.0))]
            if p.get("ParameterName") in (self.entry.get("vectors") or {}):
                self.override_hits += 1
                self.used_values.add(("vector", str(p.get("ParameterName")).lower()))
            fo = self.fn_over.get(g.name, {}).get("vectors", {})
            rgba = list(self.entry.get("vectors", {}).get(p.get("ParameterName"), fo.get(p.get("ParameterName"), dflt))) + [1.0, 1.0, 1.0, 1.0]
            vp = self.vector_prop(p.get("ParameterName"), rgba[:4])
            return self.rgba_out(self.tmp("%s.rgb" % vp.e, 3), self.tmp("%s.a" % vp.e, 1), out)
        if t == "RecordTextureStreamingInfo":
            return self.input(g, p.get("TextureObject"), scope, self.const(0.0))
        if t in ("TextureObject", "TextureObjectParameter"):
            key, pn = self.tex_ref(p, x.get("Name"), g.name)
            return V("", 0, tex=(key, pn))
        if t in ("TextureSample", "TextureSampleParameter2D", "TextureSampleParameterSubUV", "TextureSampleParameterCube", "TextureSampleParameter2DArray", "TextureSampleParameterVolume"):
            if t == "TextureSampleParameterSubUV":
                self.warn("stand-in: TextureSampleParameterSubUV %s samples the whole sheet (the sub-image index is set per particle by Niagara)" % x.get("Name"))
            ref = self.input(g, p.get("TextureObject"), scope, None)
            if ref is not None and ref.tex is not None:
                key, pn = ref.tex
            else:
                key, pn = self.tex_ref(p, x.get("Name"), g.name)
            _prev_lp, self.lp_uv = self.lp_uv, self.local_uv_pair(g, p.get("Coordinates"))
            try:
                coords = self.input(g, p.get("Coordinates"), scope, None)
            finally:
                self.lp_uv = _prev_lp
            kind = self.texture_kind(t, p, key)
            if kind != "2d" and key:
                return self.sample_sheet(kind, key, pn, coords, out, p.get("SamplerType", ""), x.get("Name"))
            if coords is None:
                # UE uses ConstCoordinate (the UV set) when the Coordinates pin is unconnected
                try:
                    uv_set = int(p.get("ConstCoordinate", 0) or 0)
                except (TypeError, ValueError):
                    uv_set = 0
                coords = self.uvs(min(max(uv_set, 0), 1))
                if uv_set > 1:
                    self.warn("texture sample %s uses UV set %d, only UV0/UV1 are supported" % (x.get("Name"), uv_set))
            if not key:
                self.warn("texture sample %s has no texture" % x.get("Name"))
                return self.rgba_out(C((0, 0, 0), 3), C(1.0), out)
            return self.sample(key, pn, coords, out, p.get("SamplerType", ""), "flipbook" in str(getattr(g, "name", "")).lower())
        if t == "AntialiasedTextureMask":
            ref = self.input(g, p.get("TextureObject"), scope, None)
            if ref is not None and ref.tex is not None:
                key, pn = ref.tex
            else:
                key, pn = self.tex_ref(p, x.get("Name"), g.name)
            if not key:
                self.warn("texture sample %s has no texture" % x.get("Name"))
                return C(0.0)
            coords = self.input(g, p.get("Coordinates"), scope, None)
            if coords is None:
                try:
                    uv_set = int(p.get("ConstCoordinate", 0) or 0)
                except (TypeError, ValueError):
                    uv_set = 0
                coords = self.uvs(min(max(uv_set, 0), 1))
            self.warn("APPROXIMATED: AntialiasedTextureMask %s uses a derivative-based soft threshold; Unreal's own coverage math is not published, so the edge softness can differ slightly" % x.get("Name"))
            chan = {"TCC_Red": 1, "TCC_Green": 2, "TCC_Blue": 3, "TCC_Alpha": 4}.get(str(p.get("Channel", "TCC_Alpha")).split("::")[-1], 4)
            s_ = self.mask(self.sample(key, pn, coords, chan, ""), [0])
            thr = C(float(p.get("Threshold", 0.5)))
            if self.mode != "ps":
                return self.tmp("step(%s, %s)" % (thr.e, s_.e), 1)
            return self.tmp("saturate((%s - %s) / max(fwidth(%s), 0.00001) + 0.5)" % (s_.e, thr.e, s_.e), 1)
        if t == "LandscapeLayerSwitch":
            self.warn("stand-in: LandscapeLayerSwitch %s takes its LayerNotUsed input (no painted layer weights here)" % x.get("Name"))
            return P("LayerNotUsed")
        if t == "TextureCoordinate":
            uv = self.uvs(int(p.get("CoordinateIndex", 0)))
            ut, vt = float(p.get("UTiling", 1.0)), float(p.get("VTiling", 1.0))
            if ut != 1.0 or vt != 1.0:
                uv = self.tmp("%s * float2(%s, %s)" % (par(uv.e), fl(ut), fl(vt)), 2)
            return uv
        if t == "Panner":
            uv = self.input(g, p.get("Coordinate"), scope, None) or self.uvs(0)
            tm = self.input(g, p.get("Time"), scope, None)
            if tm is None:
                if (ANIMATE_UNWIRED_TIME or node_selected(ANIMATE_NODES, g, x)) and not node_selected(HOLD_NODES, g, x):
                    self.note("Panner %s has no Time input: drifts with live time like Unreal's game clock; the Animate Drift value in the inspector turns it on or off (--hold-node %s makes it always static)" % (node_ref(g, x), node_ref(g, x)))
                    tm = self.tmp("%s * %s" % (self.time().e, self.scalar_prop("Animate Drift", 1.0 if ANIMATE_UNWIRED_TIME else 0.0).e), 1)
                else:
                    self.warn("Panner %s has no Time input: held static (--animate-node %s animates it)" % (node_ref(g, x), node_ref(g, x)))
                    tm = C(0.0)
            sp = self.input(g, p.get("Speed"), scope, None)
            sp = self.fit(sp, 2) if sp is not None else C((float(p.get("SpeedX", 0.0)), float(p.get("SpeedY", 0.0))), 2)
            off = self.tmp("%s * %s" % (par(tm.e if tm.w == 1 else self.comp(tm, 0)), par(sp.e)), 2)
            if p.get("bFractionalPart"):
                off = self.call1("frac", off)
            return self.tmp("%s + %s" % (off.e, par(self.fit(uv, 2).e)), 2)
        if t == "Rotator":
            uv = self.fit(self.input(g, p.get("Coordinate"), scope, None) or self.uvs(0), 2)
            tm = self.input(g, p.get("Time"), scope, None)
            if tm is None:
                if (ANIMATE_UNWIRED_TIME or node_selected(ANIMATE_NODES, g, x)) and not node_selected(HOLD_NODES, g, x):
                    self.note("Rotator %s has no Time input: drifts with live time like Unreal's game clock; the Animate Drift value in the inspector turns it on or off (--hold-node %s makes it always static)" % (node_ref(g, x), node_ref(g, x)))
                    tm = self.tmp("%s * %s" % (self.time().e, self.scalar_prop("Animate Drift", 1.0 if ANIMATE_UNWIRED_TIME else 0.0).e), 1)
                else:
                    self.warn("Rotator %s has no Time input: held static (--animate-node %s animates it)" % (node_ref(g, x), node_ref(g, x)))
                    tm = C(0.0)
            ang = self.tmp("%s * %s" % (par(tm.e if tm.w == 1 else self.comp(tm, 0)), fl(float(p.get("Speed", 0.25)))), 1)
            cx, cy = float(p.get("CenterX", 0.5)), float(p.get("CenterY", 0.5))
            d = self.tmp("%s - float2(%s, %s)" % (par(uv.e), fl(cx), fl(cy)), 2)
            c, s = self.tmp("cos(%s)" % ang.e, 1), self.tmp("sin(%s)" % ang.e, 1)
            return self.tmp("float2(%s * %s.x - %s * %s.y + %s, %s * %s.x + %s * %s.y + %s)" % (c.e, d.e, s.e, d.e, fl(cx), s.e, d.e, c.e, d.e, fl(cy)), 2)
        if t == "BumpOffset":
            uv = self.fit(self.input(g, p.get("Coordinate"), scope, None) or self.uvs(0), 2)
            h = self.mask(self.input(g, p.get("Height"), scope, C(0.0)), [0])
            ratio = self.input(g, p.get("HeightRatioInput"), scope, None)
            ratio = C(float(p.get("HeightRatio", 0.05))) if ratio is None else self.mask(ratio, [0])
            ref = float(p.get("ReferencePlane", 0.5))
            self.uses.add("tangent")
            if self.mode != "ps":
                self.warn("BumpOffset %s is in the vertex stage, where Unity has no view direction; coordinates left unshifted" % x.get("Name"))
                return uv
            # IN.viewDir is the interpolated mesh tangent frame, same as UE. Particle systems only send tangents when the
            # Tangent vertex stream is on, so the particle converter adds that stream.
            view = self.tmp("normalize(IN.viewDir)", 3)
            off = self.tmp("%s * %s - %s * %s" % (par(ratio.e), par(h.e), par(ratio.e), fl(ref)), 1)
            return self.tmp("%s + float2(%s.x, -%s.y) * %s" % (par(uv.e), view.e, view.e, off.e), 2)
        if t == "DeriveNormalZ":
            xy = self.fit(P("InXY"), 2)
            return self.tmp("float3(%s, sqrt(saturate(1.0 - dot(%s, %s))))" % (xy.e, xy.e, xy.e), 3)
        if t == "SphereMask":
            a, b = P("A"), P("B")
            radius = self.input(g, p.get("Radius"), scope, C(float(p.get("AttenuationRadius", 256.0))))
            hard = self.input(g, p.get("Hardness"), scope, C(float(p.get("HardnessPercent", 100.0))))
            a, b, w = self.match(a, b)
            d = self.tmp("distance(%s, %s)" % (a.e, b.e), 1)
            nd = self.tmp("%s / max(%s, 0.00001)" % (d.e, par(radius.e)), 1)
            soft = self.tmp("1.0 - %s * 0.01" % par(hard.e), 1)
            return self.tmp("saturate((1.0 - %s) / max(%s, 0.00001))" % (nd.e, soft.e), 1)
        if t == "Fresnel":
            n = self.input(g, p.get("Normal"), scope, None) or self.pixel_normal()
            cam = self.camera_vector()
            d = self.tmp("max(dot(%s, %s), 0.0)" % (self.fit(n, 3).e, cam.e), 1)
            base = self.tmp("max(abs(1.0 - %s), 0.0001)" % d.e, 1)
            ex = self.mask(self.input(g, p.get("ExponentIn"), scope, C(float(p.get("Exponent", 5.0)))), [0])
            f0 = self.mask(self.input(g, p.get("BaseReflectFractionIn"), scope, C(float(p.get("BaseReflectFraction", 0.04)))), [0])
            return self.tmp("pow(%s, %s) * (1.0 - %s) + %s" % (base.e, ex.e, par(f0.e), par(f0.e)), 1)
        if t in ("HsvToRgb", "RgbToHsv"):
            v = self.fit(P("Input"), 3)
            self.uses.add("hsv")
            return self.tmp("%s(%s)" % ("HsvToRgbUE" if t == "HsvToRgb" else "RgbToHsvUE", v.e), 3)
        if t == "If":
            a = self.mask(P("A"), [0])
            b = self.mask(self.input(g, p.get("B"), scope, C(float(p.get("ConstB", 0.0)))), [0])
            gt = self.input(g, p.get("AGreaterThanB"), scope, C(0.0))
            lt = self.input(g, p.get("ALessThanB"), scope, C(0.0))
            eq = self.input(g, p.get("AEqualsB"), scope, None)
            w = max(gt.w, lt.w, eq.w if eq is not None else 1)
            gt, lt = self.fit(gt, w), self.fit(lt, w)
            v = self.tmp("(%s >= %s) ? %s : %s" % (a.e, b.e, gt.e, lt.e), w)
            if eq is not None:
                thr = float(p.get("EqualsThreshold", 0.00001))
                v = self.tmp("(abs(%s - %s) > %s) ? %s : %s" % (a.e, b.e, fl(thr), v.e, self.fit(eq, w).e), w)
            return v
        if t == "IfThenElse":
            c = self.static_bool(g, p.get("Condition"), scope)
            if c is not None:
                return self.input(g, p.get("True" if c else "False"), scope, C(0.0))
            cv = self.mask(self.input(g, p.get("Condition"), scope, C(0.0)), [0])
            tv, fv = self.input(g, p.get("True"), scope, C(0.0)), self.input(g, p.get("False"), scope, C(0.0))
            w = max(tv.w, fv.w)
            return self.tmp("(%s != 0.0) ? %s : %s" % (cv.e, self.fit(tv, w).e, self.fit(fv, w).e), w)
        if t == "Switch":
            sv = self.mask(self.input(g, p.get("SwitchValue"), scope, C(float(p.get("ConstSwitchValue", 0.0)))), [0])
            res = self.input(g, p.get("Default"), scope, C(float(p.get("ConstDefault", 0.0))))
            items = [self.input(g, (ref.get("Input") if isinstance(ref, dict) and "Input" in ref else ref), scope, C(0.0)) for ref in (p.get("Inputs") or [])]
            w = max([res.w] + [i.w for i in items])
            res = self.fit(res, w)
            for i in range(len(items) - 1, -1, -1):
                res = self.tmp("(floor(%s) == %d.0) ? %s : %s" % (sv.e, i, self.fit(items[i], w).e, res.e), w)
            return res
        if t in ("StaticSwitch", "StaticSwitchParameter"):
            return self.input(g, p.get("A" if self.switch_value(g, x, scope) else "B"), scope, C(0.0))
        if t in ("StaticBool", "StaticBoolParameter"):
            v = bool(p.get("Value", False)) if t == "StaticBool" else self.switch_flag(p.get("ParameterName"), bool(p.get("DefaultValue", False)))
            return C(1.0 if v else 0.0)
        if t in COMPILE_SWITCHES:
            return self.input(g, self.switch_pin(x), scope, C(0.0))
        if t == "StaticComponentMaskParameter":
            dflt = tuple(bool(p.get("Default" + c, False)) for c in "RGBA")
            m = self.entry.get("masks", {}).get(p.get("ParameterName"), dflt)
            idx = [i for i, on in enumerate(m) if on]
            return self.mask(self.input(g, p.get("Input"), scope, C(0.0)), idx)
        if t == "ChannelMaskParameter":
            ch = str(p.get("MaskChannel", "Red")).split("::")[-1]
            i = {"Red": 0, "Green": 1, "Blue": 2, "Alpha": 3}.get(ch, 0)
            vec = list(self.entry.get("vectors", {}).get(p.get("ParameterName"), [1.0 if k == i else 0.0 for k in range(4)])) + [0.0] * 4
            v = self.input(g, p.get("Input"), scope, C(0.0))
            v4 = self.fit(v, 4) if v.w != 1 else self.fit(v, 4)
            return self.tmp("dot(%s, %s)" % (v4.e, self.const(vec[:4], 4).e), 1)
        if t == "FunctionInput":
            bound = scope.get(p.get("Id"))
            if isinstance(bound, Lazy):
                bound = bound.get(self)
            if bound is not None:
                return bound
            if p.get("InputType") == ATTRIBUTES_INPUT:
                pv = self.input(g, p.get("Preview"), scope, None)
                return pv if pv is not None and pv.at is not None else V("", 0, at=Attrs())
            if p.get("bUsePreviewValueAsDefault"):
                pv = self.input(g, p.get("Preview"), scope, None)
                if pv is not None:
                    return pv
                v = p.get("PreviewValue", {})
                it = p.get("InputType")
                if it == "EFunctionInputType::FunctionInput_Scalar":
                    return C(v.get("X", 0.0))
                w = {"EFunctionInputType::FunctionInput_Vector2": 2, "EFunctionInputType::FunctionInput_Vector4": 4}.get(it, 3)
                return C((v.get("X", 0.0), v.get("Y", 0.0), v.get("Z", 0.0), v.get("W", 0.0)), w)
            return C(0.0)
        if t == "FunctionOutput":
            return self.input(g, p.get("A"), scope, C(0.0))
        if t == "MaterialFunctionCall":
            return self.call(g, x, out, scope)
        if t == "Custom":
            return self.custom(g, x, scope, out)
        if t == "Time":
            ov, per, seen = time_period_override(p)
            if seen:
                self.warn("Time %s period properties in graph: %s" % (x.get("Name"), seen))
            if ov:
                per = per if per is not None else 0.0
                self.warn("Time %s overrides its period: %s" % (x.get("Name"), "time held at 0 (frozen)" if per <= 0.0 else "time wraps every %s s" % per))
                if per <= 0.0:
                    return C(0.0)
                return self.tmp("fmod(%s, %s)" % (self.time().e, fl(per)), 1)
            return self.time()
        if t == "VertexColor":
            self.uses.add("color")
            col = "IN.color" if self.mode == "ps" else "vcol"
            return self.rgba_out(self.tmp("%s.rgb" % col, 3), self.tmp("%s.a" % col, 1), out)
        if t in ("ParticleColor",):
            self.uses.add("color")
            self.uses.add("pstreams")
            self.uses.add("pcolor")
            if self.mode == "ps":
                col = self.tmp("lerp(IN.color, IN.pcol, _PorterParticleStreams)", 4).e
            else:
                col = self.tmp("lerp(vcol, v.texcoord1, _PorterParticleStreams)", 4).e
            use = self.scalar_prop("Use Vertex Color", 1.0)
            tint = self.vector_prop("Particle Tint", (1.0, 1.0, 1.0, 1.0))
            rgb = self.tmp("lerp(float3(1.0, 1.0, 1.0), %s.rgb, %s) * %s.rgb" % (col, use.e, tint.e), 3)
            a = self.tmp("lerp(1.0, %s.a, %s) * %s.a" % (col, use.e, tint.e), 1)
            return self.rgba_out(rgb, a, out)
        if t == "CameraVectorWS":
            return self.camera_vector()
        if t == "WorldPosition":
            return self.world_ue()
        if t == "CameraPositionWS":
            return self.tmp("_WorldSpaceCameraPos.xzy * 100.0", 3)
        if t in ("ObjectPositionWS", "ActorPositionWS", "ParticlePositionWS"):
            return self.tmp("float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m23, unity_ObjectToWorld._m13) * 100.0", 3)
        if t == "LocalPosition":
            self.uses.add("opos")
            return self.local_pos()
        if t in ("PixelNormalWS", "VertexNormalWS"):
            return self.pixel_normal()
        if t == "ReflectionVectorWS":
            n, c = self.pixel_normal(), self.camera_vector()
            return self.tmp("reflect(-%s, %s)" % (c.e, n.e), 3)
        if t == "TwoSidedSign":
            if self.mode == "vs":
                return C(1.0)
            self.uses.add("facing")
            return self.tmp("(IN.facing > 0.0 ? 1.0 : -1.0)", 1)
        if t == "ScreenPosition":
            sp = self.tmp(self.screen_pos(), 4).e
            if out == 1:
                return self.tmp("%s.xy / FPU_NZ(%s.w)" % (sp, sp), 2)
            return self.tmp("float2(%s.x / FPU_NZ(%s.w), 1.0 - %s.y / FPU_NZ(%s.w))" % (sp, sp, sp, sp), 2)
        if t == "ViewSize":
            return self.tmp("_ScreenParams.xy", 2)
        if t == "PixelDepth":
            self.uses.add("screen")
            return self.tmp("%s.w * 100.0" % self.screen_pos(), 1)
        if t == "SceneDepth":
            self.uses.add("depth")
            self.warn("SceneDepth uses _CameraDepthTexture")
            return self.tmp("SceneDepthUE(%s)" % self.screen_pos(), 1)
        if t == "DepthFade":
            op = self.input(g, p.get("InOpacity"), scope, C(float(p.get("InOpacityDefault", 1.0))))
            fd = self.input(g, p.get("FadeDistance"), scope, C(float(p.get("FadeDistanceDefault", 100.0))))
            self.uses.add("depth")
            return self.tmp("%s * DepthFadeUE(%s, %s)" % (par(op.e), self.screen_pos(), fd.e), 1)
        if t in ("ParticleRelativeTime", "ParticleRandom"):
            prop = "_PorterAgeSlot" if t == "ParticleRelativeTime" else "_PorterRandSlot"
            self.uses.add("pstreams")
            self.uses.add("dpstreams")
            self.slotprops.add(prop)
            self.note("%s is the particle's %s, sent per particle by the converted prefab in Custom Data 2; it reads 0 when the material is not on a converted particle system" % (t, "age from 0 to 1" if t == "ParticleRelativeTime" else "random number from 0 to 1"))
            return self.tmp("PorterDP1(0.0, %s, %s, %s)" % (prop, "IN.pdp" if self.mode == "ps" else "v.texcoord2", "IN.pdp2" if self.mode == "ps" else "v.texcoord3"), 1)
        if t in ("ParticleSpeed", "ParticleRadius", "ParticleMotionBlurFade", "DeltaTime", "PerInstanceRandom", "PerInstanceFadeAmount", "DistanceCullFade"):
            self.warn("stand-in 0: %s" % t)
            return C(1.0 if t in ("PerInstanceFadeAmount", "ParticleMotionBlurFade", "DistanceCullFade") else 0.0)
        if t in ("ParticleSize", "ParticleDirection", "ParticleMacroUV"):
            self.warn("stand-in: %s" % t)
            return C((1.0, 1.0), 2) if t in ("ParticleSize", "ParticleMacroUV") else C((0.0, 0.0, 1.0), 3)
        if t == "ParticleSubUV":
            return self.uvs(0)
        if t == "DynamicParameter":
            d = p.get("DefaultValue") or {}
            idx = int(p.get("ParameterIndex", 0) or 0)
            dflt = [float(d.get(k, 0.0)) for k in ("R", "G", "B", "A")]
            v = self.vector_prop("Dynamic Parameter %d" % idx, dflt, tag="_DP_")
            msg = "DynamicParameter %d is set per particle by the game's Niagara system; exposed as %s (default %s)" % (idx, v.e, dflt)
            if msg not in self.notes:
                self.notes.append(msg)
            self.uses.add("dpstreams")
            self.uses.add("pstreams")
            self.dpidx.add(idx)
            v = self.tmp("PorterDP4(%s, _PorterDPSlot%d, %s, %s)" % (v.e, idx, "IN.pdp" if self.mode == "ps" else "v.texcoord2", "IN.pdp2" if self.mode == "ps" else "v.texcoord3"), 4)
            if out < 4:
                return self.mask(v, [out])
            if out == 4:
                return self.mask(v, [0, 1, 2])
            return v
        if t == "EyeAdaptation":
            return C(1.0)
        if t == "EyeAdaptationInverse":
            return P("LightValueInput", fallback=1.0)
        if t in ("AtmosphericLightVector", "SkyAtmosphereLightDirection"):
            return self.tmp("normalize(_WorldSpaceLightPos0.xyz).xzy", 3)
        if t in ("AtmosphericLightColor",):
            return self.tmp("_LightColor0.rgb", 3)
        if t == "LightVector":
            self.warn("approximated: LightVector %s uses the main light's direction" % x.get("Name"))
            return self.tmp("normalize(_WorldSpaceLightPos0.xyz).xzy", 3)
        if t == "BlackBody":
            self.warn("stand-in: BlackBody %s is Unreal's colour-temperature approximation written from the published formula; its overall brightness scale is unverified" % x.get("Name"))
            return self.tmp("BlackBodyUE(%s)" % self.fit(P("Temp"), 1).e, 3)
        if t == "CollectionParameter":
            return self.collection_value(p)
        if t == "TransformPosition" or t == "Transform":
            v = self.fit(self.input(g, p.get("Input"), scope, C((0, 0, 0), 3)), 3)
            kinds = ("ParticleWorld", "Tangent", "Local", "World", "View", "Camera", "Instance")
            kind = lambda s: next((k for k in kinds if k in str(s)), str(s))
            src, dst = kind(p.get("TransformSourceType", "")), kind(p.get("TransformType", ""))
            if not str(p.get("TransformType", "")):
                dst = "World" if t == "Transform" else "Local"
            if not str(p.get("TransformSourceType", "")):
                src = "Tangent" if t == "Transform" else "Local"
            if t == "Transform" and src == "Tangent" and dst == "World" and self.mode == "ps":
                self.uses.add("tangent")
                tu = self.tmp("float3(%s.x, -%s.y, %s.z)" % ((par(v.e),) * 3), 3)
                wu = self.tmp("WorldNormalVector(IN, %s)" % tu.e, 3)
                return self.tmp("%s.xzy" % wu.e, 3)
            if t == "Transform" and src == "Tangent" and dst in ("Local", "Instance", "View", "Camera") and self.mode == "ps":
                self.uses.add("tangent")
                tu = self.tmp("float3(%s.x, -%s.y, %s.z)" % ((par(v.e),) * 3), 3)
                wu = self.tmp("WorldNormalVector(IN, %s)" % tu.e, 3)
                v = self.tmp("%s.xzy" % wu.e, 3)
                src = "World"
            if t == "Transform" and src == "Tangent" and dst == "World" and self.mode == "vs":
                wn = self.tmp("UnityObjectToWorldNormal(vnrm)", 3)
                wt = self.tmp("UnityObjectToWorldDir(vtan.xyz)", 3)
                wb = self.tmp("cross(%s, %s) * vtan.w * unity_WorldTransformParams.w" % (wn.e, wt.e), 3)
                tu = self.tmp("float3(%s.x, -%s.y, %s.z)" % ((par(v.e),) * 3), 3)
                wu = self.tmp("%s.x * %s + %s.y * %s + %s.z * %s" % (tu.e, wt.e, tu.e, wb.e, tu.e, wn.e), 3)
                return self.tmp("%s.xzy" % par(wu.e), 3)
            if t == "Transform" and src == "World" and dst == "Tangent" and self.mode == "ps":
                self.uses.add("tangent")
                wu = self.tmp("%s.xzy" % par(v.e), 3)
                tx = self.tmp("WorldNormalVector(IN, float3(1.0, 0.0, 0.0))", 3)
                ty = self.tmp("WorldNormalVector(IN, float3(0.0, 1.0, 0.0))", 3)
                tz = self.tmp("WorldNormalVector(IN, float3(0.0, 0.0, 1.0))", 3)
                tu = self.tmp("float3(dot(%s, %s), dot(%s, %s), dot(%s, %s))" % (wu.e, tx.e, wu.e, ty.e, wu.e, tz.e), 3)
                return self.tmp("float3(%s.x, -%s.y, %s.z)" % ((tu.e,) * 3), 3)
            objs = ("Local", "Instance")
            if src != dst and src in ("World",) + objs and dst in ("World",) + objs:
                pos = t == "TransformPosition"
                if src in objs and dst in objs:
                    return v
                if src in objs:
                    vi = self.lin(v)
                    if pos:
                        w = self.tmp("mul(unity_ObjectToWorld, float4(%s.xzy * 0.01, 1.0)).xyz" % par(vi.e), 3)
                        return self.tmp("%s.xzy * 100.0" % par(w.e), 3)
                    w = self.tmp("mul((float3x3)unity_ObjectToWorld, %s.xzy)" % par(vi.e), 3)
                    return self.tmp("%s.xzy" % par(w.e), 3)
                if pos:
                    l = self.tmp("mul(unity_WorldToObject, float4(%s.xzy * 0.01, 1.0)).xyz" % par(v.e), 3)
                    lo = self.tmp("%s.xzy * 100.0" % par(l.e), 3)
                    return self.lout(lo)
                l = self.tmp("mul((float3x3)unity_WorldToObject, %s.xzy)" % par(v.e), 3)
                lo = self.tmp("%s.xzy" % par(l.e), 3)
                return self.lout(lo)
            views, wsp = ("View", "Camera"), ("World",) + objs
            if src != dst and ((src in views and dst in wsp) or (dst in views and src in wsp)):
                pos = t == "TransformPosition"
                if src in views:
                    vu = self.tmp("float3(%s.x, %s.y, -%s.z)" % ((par(v.e),) * 3), 3)
                    if pos:
                        w = self.tmp("mul(UNITY_MATRIX_I_V, float4(%s * 0.01, 1.0)).xyz" % par(vu.e), 3)
                    else:
                        w = self.tmp("mul((float3x3)UNITY_MATRIX_I_V, %s)" % vu.e, 3)
                    isobj = dst in objs
                    if isobj:
                        if pos:
                            w = self.tmp("mul(unity_WorldToObject, float4(%s, 1.0)).xyz" % w.e, 3)
                        else:
                            w = self.tmp("mul((float3x3)unity_WorldToObject, %s)" % w.e, 3)
                    res = self.tmp("%s.xzy%s" % (par(w.e), " * 100.0" if pos else ""), 3)
                    return self.lout(res) if isobj else res
                if src in objs:
                    vi = self.lin(v)
                    if pos:
                        wu = self.tmp("mul(unity_ObjectToWorld, float4(%s.xzy * 0.01, 1.0)).xyz" % par(vi.e), 3)
                    else:
                        wu = self.tmp("mul((float3x3)unity_ObjectToWorld, %s.xzy)" % par(vi.e), 3)
                else:
                    wu = self.tmp("%s.xzy%s" % (par(v.e), " * 0.01" if pos else ""), 3)
                if pos:
                    vw = self.tmp("mul(UNITY_MATRIX_V, float4(%s, 1.0)).xyz" % wu.e, 3)
                else:
                    vw = self.tmp("mul((float3x3)UNITY_MATRIX_V, %s)" % wu.e, 3)
                return self.tmp("float3(%s.x, %s.y, -%s.z)%s" % (par(vw.e), par(vw.e), par(vw.e), " * 100.0" if pos else ""), 3)
            if src != dst:
                self.warn("NOT CONVERTED: %s %s (%s) from %s to %s was passed through unchanged" % (t, x.get("Name"), self.mode, src or "?", dst or "?"))
            return v
        if t in ("PreSkinnedLocalBounds", "ObjectLocalBounds", "Bounds", "ObjectBounds"):
            if t == "ObjectBounds":
                out = 1
            bmin = self.fit(self.vector_prop("BoundsMin", (-60.0, -35.0, -10.0, 0.0), "_O_"), 3)
            bmax = self.fit(self.vector_prop("BoundsMax", (60.0, 35.0, 190.0, 0.0), "_O_"), 3)
            self.uses.add("objradius")
            note = "PreSkinnedLocalBounds is filled in per object by PorterObjectBounds (menu Porter > Add Object Bounds To Selection); without it the shader uses a generic character-sized default box"
            if note not in self.notes:
                self.notes.append(note)
            if out == 0:
                return self.tmp("(%s - %s) * 0.5" % (bmax.e, bmin.e), 3)
            if out == 1:
                return self.tmp("%s - %s" % (bmax.e, bmin.e), 3)
            if out == 2:
                return bmin
            return bmax
        if t == "ObjectRadius":
            r = self.scalar_prop("ObjectRadius", 100.0, "_O_")
            self.uses.add("objradius")
            note = "ObjectRadius is filled in per object by PorterObjectBounds (menu Porter > Add Object Bounds To Selection); without it the shader uses the 100 cm default"
            if note not in self.notes:
                self.notes.append(note)
            m = "unity_ObjectToWorld"
            return self.tmp("%s * max(length(%s._m00_m10_m20), max(length(%s._m01_m11_m21), length(%s._m02_m12_m22)))" % (r.e, m, m, m), 1)
        if t == "Noise":
            pos = self.fit(self.input(g, p.get("Position"), scope, None) if linked(p.get("Position")) else self.world_ue(), 3)
            levels = max(1, min(8, int(p.get("Levels", 6) or 6)))
            self.uses.add("noise")
            self.warn("approximate Noise %s (gradient noise, not UE's exact function)" % x.get("Name"))
            return self.tmp("NoiseUE(%s, %s, %s, %s, %s, %s, %s)" % (pos.e, fl(float(p.get("Scale", 1.0))), fl(float(levels)), fl(float(p.get("LevelScale", 2.0))), "1.0" if p.get("bTurbulence", True) else "0.0", fl(float(p.get("OutputMin", -1.0))), fl(float(p.get("OutputMax", 1.0)))), 1)
        if t == "VectorNoise":
            pos = self.fit(self.input(g, p.get("Position"), scope, None) if linked(p.get("Position")) else self.world_ue(), 3)
            fn = str(p.get("NoiseFunction", "VNF_CellnoiseALU")).split("::")[-1]
            self.uses.add("noise")
            self.warn("approximate VectorNoise %s (%s, not UE's exact function)" % (x.get("Name"), fn))
            if fn == "VNF_CellnoiseALU":
                return self.tmp("CellNoiseUE(%s)" % pos.e, 3)
            if fn == "VNF_VoronoiALU":
                vo = self.tmp("VoronoiNoiseUE(%s)" % pos.e, 4)
                if out == 1:
                    return self.tmp("%s.w" % vo.e, 1)
                return self.rgba_out(self.tmp("%s.xyz" % vo.e, 3), self.tmp("%s.w" % vo.e, 1), out)
            return self.tmp("GradientNoise3UE(%s)" % pos.e, 3)
        if t in ("MakeMaterialAttributes", "SetMaterialAttributes", "GetMaterialAttributes", "BreakMaterialAttributes", "BlendMaterialAttributes"):
            return self.attributes(g, x, t, out, scope, p)
        if t == "ShadingModel":
            sm = str(p.get("ShadingModel", "EMaterialShadingModel::MSM_DefaultLit")).split("::")[-1]
            return C(float(SHADING_MODELS.index(sm)) if sm in SHADING_MODELS else 1.0)
        if t == "Convert":
            width = {"Scalar": 1, "Vector2": 2, "Vector3": 3, "Vector4": 4}
            ins = []
            for ci in p.get("ConvertInputs", []):
                d = ci.get("DefaultValue", {})
                dv = C((d.get("R", 0.0), d.get("G", 0.0), d.get("B", 0.0)), width.get(ci.get("Type", "").split("::")[-1], 1))
                v = self.input(g, ci.get("ExpressionInput"), scope, dv)
                ins.append(v)
            outs = []
            for co in p.get("ConvertOutputs", []):
                d = co.get("DefaultValue", {})
                outs.append([fl(d.get(k, 0.0)) for k in "RGBA"][:width.get(co.get("Type", "").split("::")[-1], 1)])
            for mp in p.get("ConvertMappings", []):
                oi, oc = mp.get("OutputIndex", 0), mp.get("OutputComponentIndex", 0)
                ii, ic = mp.get("InputIndex", 0), mp.get("InputComponentIndex", 0)
                if oi < len(outs) and oc < len(outs[oi]) and ii < len(ins):
                    outs[oi][oc] = self.comp(ins[ii], ic)
            sel = outs[min(out, len(outs) - 1)] if outs else ["0.0"]
            return self.tmp("float%d(%s)" % (len(sel), ", ".join(sel)) if len(sel) > 1 else sel[0], len(sel))
        if t in ("MaterialXRemap",):
            v = P("Input")
            il = self.input(g, p.get("InputLow"), scope, C(float(p.get("InputLowDefault", 0.0))))
            ih = self.input(g, p.get("InputHigh"), scope, C(float(p.get("InputHighDefault", 1.0))))
            tl = self.input(g, p.get("TargetLow"), scope, C(float(p.get("TargetLowDefault", 0.0))))
            th = self.input(g, p.get("TargetHigh"), scope, C(float(p.get("TargetHighDefault", 1.0))))
            k = self.binop("/", self.binop("-", th, tl), self.binop("-", ih, il))
            return self.binop("+", tl, self.binop("*", self.binop("-", v, il), k))
        if t == "MaterialXAppend3Vector":
            return self.combine([self.mask(P(k), [0]) for k in "ABC"])
        if t == "RotateAboutAxis":
            axis = self.fit(self.input(g, p.get("NormalizedRotationAxis"), scope, C((0.0, 0.0, 1.0), 3)), 3)
            ang = self.binop("*", self.mask(self.input(g, p.get("RotationAngle"), scope, C(0.0)), [0]), C(2 * math.pi / float(p.get("Period", 1.0)) if float(p.get("Period", 1.0)) > 0 else 1.0))
            pivot = self.fit(self.input(g, p.get("PivotPoint"), scope, C((0.0, 0.0, 0.0), 3)), 3)
            pos = self.fit(self.input(g, p.get("Position"), scope, C((0.0, 0.0, 0.0), 3)), 3)
            return self.tmp("RotateAboutAxisUE(%s, %s, %s, %s)" % (axis.e, ang.e, pivot.e, pos.e), 3)
        if t == "VertexInterpolator":
            return P("Input")
        if t == "PreSkinnedNormal":
            return self.object_normal()
        if t == "VertexTangentWS":
            return self.vertex_tangent()
        if t == "PreSkinnedPosition":
            self.uses.add("opos")
            return self.local_pos()
        if t in ("DDX", "DDY"):
            v = self.input(g, p.get("Value") or p.get("Input"), scope, C(0.0))
            v = self.fit(v, v.w)
            if self.mode != "ps" or v.const:
                return C((0.0,) * v.w, v.w)
            return self.tmp("%s(%s)" % ("ddx" if t == "DDX" else "ddy", v.e), v.w)
        if t == "ObjectOrientation":
            return self.tmp("normalize(unity_ObjectToWorld._m01_m11_m21).xzy", 3)
        if t == "SceneTexelSize":
            return self.tmp("_ScreenParams.zw - 1.0", 2)
        if t == "LightmassReplace":
            return P("Realtime")
        if t in ("CurveAtlasRowParameter", "CurveAtlasRow"):
            cv = p.get("Curve") or {}
            cname = inner(str(cv.get("ObjectName", ""))) or "?"
            curve = None
            pname = p.get("ParameterName")
            at = p.get("Atlas") or {}
            row = (self.entry.get("scalars") or {}).get(pname, p.get("DefaultValue")) if pname else None
            if row is not None and at and hasattr(self.src, "atlas"):
                rows = self.src.atlas(str(at.get("ObjectPath", "")), inner(str(at.get("ObjectName", ""))))
                if rows:
                    pick = rows[min(max(int(round(float(row))), 0), len(rows) - 1)]
                    pick_name = inner(str(pick.get("ObjectName", "")))
                    curve = self.src.curve(str(pick.get("ObjectPath", "")), pick_name)
                    if curve:
                        cname = pick_name
                        self.warn("note: curve atlas row %s uses row %s of %s: curve %s" % (pname, row, inner(str(at.get("ObjectName", ""))), pick_name))
                    else:
                        self.warn("APPROXIMATED: curve atlas row %s is row %s of %s, which is curve %s, but that curve was not found, so the node's own curve %s is used; export %s as JSON (FModel: Save Properties) into Cache\\Curves" % (pname, row, inner(str(at.get("ObjectName", ""))), pick_name, cname, pick_name))
                elif pname in (self.entry.get("scalars") or {}):
                    self.warn("APPROXIMATED: curve atlas row %s is set to row %s by the instance, but the atlas %s was not found, so the node's own curve %s is used; export the atlas as JSON (FModel: Save Properties) into Cache\\Curves" % (pname, row, inner(str(at.get("ObjectName", ""))), cname))
            if not curve:
                curve = self.src.curve(str(cv.get("ObjectPath", "")), cname) if hasattr(self.src, "curve") else None
            if not curve:
                self.warn("APPROXIMATED: %s (%s) curve %s was not found and is replaced by neutral 1.0; export that curve as JSON (FModel: Save Properties) into the Curves folder next to Cache to convert it for real" % (t, x.get("Name"), cname))
                return C(1.0)
            tv = self.fit(self.input(g, p.get("InputTime"), scope, C(float(p.get("DefaultValue", 0.0) or 0.0))), 1)
            fn = self.curve_fn(cname, curve)
            col = self.tmp("%s(%s)" % (fn, tv.e), 4)
            if out == 0:
                return self.tmp("%s.rgb" % col.e, 3)
            return self.tmp("%s.%s" % (col.e, "xyzw"[min(max(out - 1, 0), 3)]), 1)
        if t == "TextureProperty":
            ref = self.input(g, p.get("TextureObject"), scope, None)
            size = None
            if ref is not None and ref.tex is not None:
                try:
                    info = self.src.texture_info(ref.tex[0]) or {}
                    size = (float(info.get("Width") or 0.0), float(info.get("Height") or 0.0))
                except Exception:
                    size = None
            if not size or not size[0] or not size[1]:
                self.warn("stand-in: TextureProperty %s could not read the texture size, using 1 x 1" % x.get("Name"))
                size = (1.0, 1.0)
            if str(p.get("Property", "")).endswith("TexelSize"):
                return C((1.0 / size[0], 1.0 / size[1]), 2)
            return C(size, 2)
        if t == "ViewProperty":
            name = str(p.get("Property", "MEVP_FieldOfView")).split("::")[-1].replace("MEVP_", "")
            if name == "FieldOfView":
                v = self.tmp("2.0 * atan(1.0 / unity_CameraProjection._m00)", 1)
            elif name == "TanHalfFieldOfView":
                v = self.tmp("float2(1.0 / unity_CameraProjection._m00, 1.0 / unity_CameraProjection._m11)", 2)
            elif name in ("BufferSize", "ViewSize"):
                v = self.tmp("_ScreenParams.xy", 2)
            elif name in ("WorldSpaceViewPosition", "WorldSpaceCameraPosition"):
                v = self.tmp("_WorldSpaceCameraPos.xzy * 100.0", 3)
            elif name == "PreExposure":
                v = C(1.0)
            elif name == "TemporalSampleCount":
                v = C(1.0)
            elif name in ("ViewportOffset", "TemporalSampleOffset", "RuntimeVirtualTextureOutputDerivative"):
                v = C((0.0, 0.0), 2)
            else:
                self.warn("stand-in 0: ViewProperty %s (%s)" % (name, x.get("Name")))
                v = C(0.0)
            return v if out == 0 else self.tmp("1.0 / %s" % par(v.e), v.w)
        if t == "IsOrthographic":
            return self.tmp("unity_OrthoParams.w", 1)
        if t in ("PerInstanceCustomData", "PerInstanceCustomData3Vector"):
            wdt = 3 if t.endswith("3Vector") else 1
            dflt = self.input(g, p.get("DefaultValue"), scope, None)
            if dflt is None:
                d = p.get("ConstDefaultValue", 0.0)
                if isinstance(d, dict):
                    dflt = C((float(d.get("R", 0.0)), float(d.get("G", 0.0)), float(d.get("B", 0.0))), 3)
                else:
                    dflt = C(float(d or 0.0))
            self.warn("stand-in: %s %s uses its default value (per-instance floats are not read)" % (t, x.get("Name")))
            return self.fit(dflt, wdt)
        if t == "ScalarBlueNoise":
            sp = self.screen_pos()
            self.warn("stand-in: ScalarBlueNoise %s uses interleaved gradient noise (close to blue noise)" % x.get("Name"))
            return self.tmp("frac(52.9829189 * frac(dot(floor(%s.xy / max(%s.w, 0.0001) * _ScreenParams.xy), float2(0.06711056, 0.00583715))))" % (sp, sp), 1)
        if t == "SceneTexture":
            if out in (1, 2):
                return self.tmp("_ScreenParams.xy" if out == 1 else "1.0 / _ScreenParams.xy", 2)
            sid = str(p.get("SceneTextureId", "ESceneTextureId::PPI_SceneColor")).split("::")[-1]
            if sid == "PPI_WorldNormal":
                rgb = self.pixel_normal()
            elif sid == "PPI_SceneDepth":
                self.uses.add("depth")
                d = self.tmp("SceneDepthUE(%s)" % self.screen_pos(), 1)
                rgb = self.tmp("float3(%s, %s, %s)" % (d.e, d.e, d.e), 3)
            else:
                vals = {"PPI_Roughness": 0.5, "PPI_Specular": 0.5, "PPI_BaseColor": 0.5, "PPI_DiffuseColor": 0.5,
                        "PPI_CustomDepth": 1e6, "PPI_Opacity": 1.0, "PPI_AmbientOcclusion": 1.0}
                v = vals.get(sid, 0.0)
                self.warn("stand-in: SceneTexture %s is a fixed %s (Unity has no such buffer here)" % (sid, v))
                rgb = C((v, v, v), 3)
            return self.rgba_out(rgb, C(1.0), out)
        if t == "SceneDepthWithoutWater":
            self.uses.add("depth")
            return self.tmp("SceneDepthUE(%s)" % self.screen_pos(), 1)
        if t == "SceneColor":
            self.warn("stand-in: SceneColor is black (Unity has no scene color here)")
            return C((0.0, 0.0, 0.0), 3)
        if t == "DistanceFieldApproxAO":
            self.warn("stand-in: DistanceFieldApproxAO is 1 (no occlusion)")
            return C(1.0)
        if t == "LandscapeLayerSample":
            self.warn("stand-in 0: LandscapeLayerSample %s (no painted layer weights here)" % x.get("Name"))
            return C(0.0)
        if t == "LandscapeVisibilityMask":
            return C(1.0)
        if t == "LandscapeLayerWeight":
            base = self.input(g, p.get("Base"), scope, None)
            if base is None:
                cb = p.get("ConstBase") or {}
                base = C((float(cb.get("X", 0.0)), float(cb.get("Y", 0.0)), float(cb.get("Z", 0.0))), 3)
            self.warn("stand-in: LandscapeLayerWeight %s returns its Base (layer weight 0)" % x.get("Name"))
            return base
        if t == "LandscapeLayerBlend":
            self.warn("stand-in 0: LandscapeLayerBlend %s (no painted layer weights here)" % x.get("Name"))
            return C(0.0)
        if t == "LandscapeLayerCoords":
            self.warn("stand-in: LandscapeLayerCoords %s uses world XY in 1 m quads" % x.get("Name"))
            sc = float(p.get("MappingScale", 1.0) or 1.0)
            return self.tmp("%s.xy / %s" % (par(self.world_ue().e), fl(100.0 * sc)), 2)
        if t in ("RuntimeVirtualTextureSampleParameter", "RuntimeVirtualTextureSample"):
            vals = [C((0.5, 0.5, 0.5), 3), C(0.5), C(0.5), C((0.0, 0.0, 1.0), 3), C(-1e6), C(0.0), C(0.0)]
            self.warn("stand-in: runtime virtual texture %s is a neutral surface" % x.get("Name"))
            return vals[min(out, len(vals) - 1)]
        if t in ("ShellMeshNormalizedShellLayer", "ShellMeshShellLayerIndex", "CloudSampleAttribute"):
            self.warn("stand-in 0: %s" % t)
            return C(0.0)
        if t in ("ShellMeshShellCount", "SphericalParticleOpacity"):
            self.warn("stand-in 1: %s" % t)
            return C(1.0)
        if t in ("SamplePhysicsVectorField", "ShellMeshLocalShellOffset"):
            self.warn("stand-in 0: %s" % t)
            return C((0.0, 0.0, 0.0), 3)
        if t == "ParticleSpriteRotation":
            self.warn("stand-in 0: ParticleSpriteRotation")
            return C((0.0, 0.0), 2)
        if t == "VolumetricAdvancedMaterialInput":
            return C((1.0, 1.0, 1.0), 3)
        if t == "DecalColor":
            return self.rgba_out(C((1.0, 1.0, 1.0), 3), C(1.0), out)
        if t == "DecalLifetimeOpacity":
            return C(1.0)
        if t == "SkyAtmosphereLightIlluminance":
            return self.tmp("_LightColor0.rgb", 3)
        if t == "SkyAtmosphereViewLuminance":
            d = self.input(g, p.get("WorldDirection"), scope, None)
            if d is None:
                d = self.tmp("-%s" % par(self.camera_vector().e), 3)
            dv = self.tmp("%s.xzy" % par(self.fit(d, 3).e), 3)
            self.warn("approximation: SkyAtmosphereViewLuminance is a single-scattering sky with an isotropic second-order term; the Time of Day controller supplies the sun and moon directions and the per-hour scattering colors, heights and anisotropy from the day sequence, while the scattering scales stay at Unreal's defaults")
            self.uses.add("atmosphere")
            return self.tmp("PorterAtmosphereView(%s)" % dv.e, 3)
        if t == "SkyAtmosphereLightDiskLuminance":
            diam = self.input(g, p.get("DiskAngularDiameterOverride"), scope, None)
            half = "(%s * 0.0087266463)" % (par(self.fit(diam, 1).e) if diam is not None else "0.5357") if diam is not None else "(0.5357 * 0.0087266463)"
            cv = self.camera_vector()
            return self.tmp("_LightColor0.rgb * step(cos(%s), dot(-%s, normalize(_WorldSpaceLightPos0.xyz).xzy)) / max(6.2831853 * (1.0 - cos(%s)), 0.0000001)" % (half, par(cv.e), half), 3)
        if t == "SkyAtmosphereAerialPerspective":
            self.warn("stand-in: SkyAtmosphereAerialPerspective is none (0 luminance, full transmittance)")
            return self.rgba_out(C((0.0, 0.0, 0.0), 3), C(1.0), out)
        if t == "SkyAtmosphereDistantLightScatteredLuminance":
            self.warn("stand-in 0: SkyAtmosphereDistantLightScatteredLuminance")
            return C((0.0, 0.0, 0.0), 3)
        if t in SKY_IGNORED:
            msg = "sky lighting (%s) ignored on purpose: it only exists in the Fortnite world, so it is a fixed 0" % t
            if msg not in self.notes:
                self.notes.append(msg)
            return C(0.0)
        if t in KNOWN_UNSUPPORTED:
            self.warn("NOT CONVERTED: %s (%s) has no Unity conversion yet and was replaced by 0" % (t, x.get("Name")))
            return C(0.0)
        self.warn("unsupported %s (%s) -> 0" % (t, x.get("Name")))
        return C(0.0)

    def curve_fn(self, cname, curves):
        cache = self.__dict__.setdefault("curve_fns", {})
        if cname in cache:
            return cache[cname]
        fn = "Curve%d" % (len(cache) + 1)
        cache[cname] = fn
        parts = []
        defaults = (0.0, 0.0, 0.0, 1.0)
        for ci in range(4):
            c = curves[ci] if ci < len(curves) else {}
            keys = sorted([k for k in ((c or {}).get("Keys") or []) if isinstance(k, dict)], key=lambda k: float(k.get("Time", 0.0)))
            dv = float((c or {}).get("DefaultValue", defaults[ci]) if (c or {}).get("DefaultValue") not in (None, 3.4028234663852886e+38) else defaults[ci])
            if not keys:
                parts.append("float %s_%d(float t)\n{\n    return %s;\n}" % (fn, ci, repr(dv)))
                continue
            ks = [(float(k.get("Time", 0.0)), float(k.get("Value", 0.0)), str(k.get("InterpMode", "RCIM_Linear")).split("::")[-1], float(k.get("ArriveTangent", 0.0) or 0.0), float(k.get("LeaveTangent", 0.0) or 0.0)) for k in keys]
            body = ["    if (t <= %s) return %s;" % (repr(ks[0][0]), repr(ks[0][1]))]
            for i in range(len(ks) - 1):
                t0, v0, mode, _, lt = ks[i]
                t1, v1, _, at1, _ = ks[i + 1]
                h = max(t1 - t0, 1e-6)
                if mode == "RCIM_Constant":
                    seg = "return %s;" % repr(v0)
                elif mode == "RCIM_Cubic":
                    seg = ("float u = (t - %s) / %s; float u2 = u * u; float u3 = u2 * u; "
                           "return (2.0 * u3 - 3.0 * u2 + 1.0) * %s + (u3 - 2.0 * u2 + u) * %s + (-2.0 * u3 + 3.0 * u2) * %s + (u3 - u2) * %s;") % (
                        repr(t0), repr(h), repr(v0), repr(lt * h), repr(v1), repr(at1 * h))
                else:
                    seg = "return lerp(%s, %s, (t - %s) / %s);" % (repr(v0), repr(v1), repr(t0), repr(h))
                body.append("    if (t < %s) { %s }" % (repr(t1), seg))
            body.append("    return %s;" % repr(ks[-1][1]))
            parts.append("float %s_%d(float t)\n{\n%s\n}" % (fn, ci, "\n".join(body)))
        parts.append("float4 %s(float t)\n{\n    t = saturate(t);\n    return float4(%s_0(t), %s_1(t), %s_2(t), %s_3(t));\n}" % (fn, fn, fn, fn, fn))
        self.customs.append("\n".join(parts))
        self.warn("note: curve %s baked from its key data as %s (time range 0..1, like the Unreal atlas)" % (cname, fn))
        return fn

    def time(self):
        return V("(_Time.y + %s)" % fl(TIME_OFFSET), 1) if TIME_OFFSET else V("_Time.y", 1)

    def attributes(self, g, x, t, out, scope, p):
        if t == "MakeMaterialAttributes":
            th = {}
            for pin, ref in p.items():
                name = MAKE_PINS.get(pin)
                if name and linked(ref):
                    th[name] = (lambda ref=ref, name=name: self.attr_value(self.input(g, ref, scope, self.attr_default(name)), name))
            return V("", 0, at=Attrs(None, th))
        if t == "SetMaterialAttributes":
            ins = p.get("Inputs") or []
            base = self.attrs_of(self.input(g, ins[0], scope, None)) if ins else Attrs()
            th = {}
            for k, guid in enumerate(p.get("AttributeSetTypes") or []):
                ref = ins[k + 1] if k + 1 < len(ins) else None
                if linked(ref):
                    name = ATTR_OF_GUID.get(guid) or re.sub(r"\s", "", str(ref.get("InputName", guid)))
                    th[name] = (lambda ref=ref, name=name: self.attr_value(self.input(g, ref, scope, self.attr_default(name)), name))
            return V("", 0, at=Attrs(base, th))
        if t == "GetMaterialAttributes":
            a = self.attrs_of(self.input(g, p.get("MaterialAttributes"), scope, None))
            if out == 0:
                return V("", 0, at=a)
            types = p.get("AttributeGetTypes") or []
            if out - 1 >= len(types):
                return self.const(0.0)
            outs = p.get("Outputs") or []
            label = outs[out].get("OutputName") if out < len(outs) and isinstance(outs[out], dict) else None
            name = ATTR_OF_GUID.get(types[out - 1]) or re.sub(r"\s", "", str(label or types[out - 1]))
            return a.get(self, name)
        if t == "BreakMaterialAttributes":
            a = self.attrs_of(self.input(g, p.get("MaterialAttributes"), scope, None))
            return a.get(self, BREAK_ORDER[out]) if out < len(BREAK_ORDER) else self.const(0.0)
        a = self.attrs_of(self.input(g, p.get("A"), scope, None))
        b = self.attrs_of(self.input(g, p.get("B"), scope, None))
        mode = lambda k: str(p.get(k, "EMaterialAttributeBlend::Blend")).split("::")[-1]
        cell = {}

        def alpha():
            if self.mode not in cell:
                cell[self.mode] = self.mask(self.input(g, p.get("Alpha"), scope, self.const(0.0)), [0])
            return cell[self.mode]
        return V("", 0, at=BlendAttrs(a, b, alpha, mode("PixelAttributeBlendType"), mode("VertexAttributeBlendType")))

    def attr_value(self, v, name):
        if isinstance(v.at, Attrs):
            return v.at.get(self, "BaseColor")
        return self.fit(v, ATTR_W.get(name, v.w if v.w else 1)) if name in ATTR_W else v

    @staticmethod
    def attrs_of(v):
        return v.at if v is not None and isinstance(v.at, Attrs) else Attrs()

    def custom(self, g, x, scope, out=0):
        p = x.get("Properties") or {}
        code = str(p.get("Code", ""))
        ins = []
        for i in p.get("Inputs", []):
            name = i.get("InputName")
            v = self.input(g, i.get("Input"), scope, self.const(0.0))
            ins.append((name, v))
        ow = {"CMOT_Float1": 1, "CMOT_Float2": 2, "CMOT_Float3": 3, "CMOT_Float4": 4}.get(str(p.get("OutputType", "CMOT_Float3")).split("::")[-1], 3)
        if p.get("AdditionalOutputs"):
            self.warn("Custom node %s: additional outputs not supported" % x.get("Name"))
        args, params = [], []
        for name, v in ins:
            if v.tex is not None:
                prop = self.tex_prop(*v.tex) if v.tex[0] else None
                if prop:
                    code = re.sub(r"\b%s\.Sample\(\s*%sSampler\s*,\s*" % (re.escape(name), re.escape(name)), "tex2D(%s, " % prop, code)
                    code = re.sub(r"\b%s\.SampleLevel\(\s*%sSampler\s*,\s*([^,]+),\s*([^)]+)\)" % (re.escape(name), re.escape(name)),
                                  r"tex2Dlod(%s, float4(\1, 0.0, \2))" % prop, code)
                    code = ue_texture_macros(code, name, prop)
                continue
            if v.at is not None:
                continue
            if v.w > 1 and v.w < ow and re.search(r"return\s+%s\s*;" % re.escape(name), code):
                self.warn("APPROXIMATED: Custom node %s returns its float%d input as float%d: padded with 0" % (x.get("Name"), v.w, ow))
                v = self.fit(v, ow)
            if v.w == 1:
                used = re.findall(r"\b%s\s*\.\s*([xyzwrgba]+)\b" % re.escape(name), code)
                need = max([("xyzw".find(c) if c in "xyzw" else "rgba".find(c)) + 1 for sw in used for c in sw] or [1])
                if need > 1:
                    self.warn("Custom node %s input %s is a single value but its code reads it as float%d: widened" % (x.get("Name"), name, need))
                    v = self.fit(v, need)
            params.append("float%s %s" % ("" if v.w == 1 else v.w, ident(name)))
            args.append(v.e)
        if re.search(r"LocalObjectBounds(?:Min|Max)\b", code):
            self.vector_prop("BoundsMin", (-60.0, -35.0, -10.0, 0.0), "_O_")
            self.vector_prop("BoundsMax", (60.0, 35.0, 190.0, 0.0), "_O_")
            self.uses.add("objradius")
            note = "PreSkinnedLocalBounds is filled in per object by PorterObjectBounds (menu Porter > Add Object Bounds To Selection); without it the shader uses a generic character-sized default box"
            if note not in self.notes:
                self.notes.append(note)
        Translator.scope_ids[0] += 1
        fname = "Custom%d" % Translator.scope_ids[0]
        code = code.replace("\r\n", "\n")
        code = re.sub(r"\bfloat(\d)\s*\(\s*([^()]*?)\s*\)\s*\.\s*rgb\b", r"float\1(\2).rgb", code)
        decls, code, ok = ue_custom_code(code, ow)
        if not ok:
            left = sorted(set(m.group(0) for m in re.finditer(r"\b(?:Parameters|ResolvedView|GetPrimitiveData|GetMaterialParameters|View\.\w+|\w+\.Sample\w*|Texture2DSample\w*|TextureCubeSample\w*|GetMaterialSharedSampler|ProcessMaterial\w*|View_\w+)", str(p.get("Code", "")))))
            self.warn("NOT CONVERTED: Custom node %s uses Unreal-only code that has no Unity equivalent (%s): replaced by 0" % (x.get("Name"), ", ".join(left) or "texture sampling"))
        code = "\n".join(decls + [code])
        self.customs.append("float%s %s(%s)\n{\n%s\n}" % ("" if ow == 1 else ow, fname, ", ".join(params), code))
        return self.tmp("%s(%s)" % (fname, ", ".join(args)), ow)


VIEW_MATRICES = {"WorldToClip": "ViewWorldToClipUE", "WorldToView": "ViewWorldToViewUE", "ViewToWorld": "ViewViewToWorldUE",
                 "ViewToClip": "ViewViewToClipUE", "ClipToView": "ViewClipToViewUE"}

VIEW_VALUES = {"ViewOrigin": "(_WorldSpaceCameraPos.xzy * 100.0)", "WorldCameraOrigin": "(_WorldSpaceCameraPos.xzy * 100.0)",
               "ViewSizeAndInvSize": "float4(_ScreenParams.xy, 1.0 / _ScreenParams.xy)",
               "BufferSizeAndInvSize": "float4(_ScreenParams.xy, 1.0 / _ScreenParams.xy)",
               "GameTime": "_Time.y", "RealTime": "_Time.y"}

CUSTOM_UE_SYMBOLS = re.compile(r"\b(?:ResolvedView|Parameters|GetPrimitiveData|GetMaterialParameters|LWCToFloat|MaterialFloat|Mod|BRANCH|LOOP|UNROLL|FLATTEN|"
                               r"Texture2DSample\w*|TextureCubeSample\w*|GetMaterialSharedSampler|ProcessMaterial\w*|UnpackNormalMap|View_\w+)\b|\bView\s*\.|\bMaterialFloat\d")

UE_ATTRIBUTE_MACROS = re.compile(r"\b(?:BRANCH|LOOP|UNROLL|FLATTEN|ALLOW_UAV_CONFLICT)\b[ \t]*")

NORMAL_HELPERS = """
float4 UnpackNormalMapUE(float4 s)
{
    float2 xy = s.rg * 2.0 - 1.0;
    return float4(xy, sqrt(saturate(1.0 - dot(xy, xy))), 0.0);
}
"""


def ue_split_args(text):
    args, depth, cur = [], 0, ""
    for ch in text:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            args.append(cur)
            cur = ""
        else:
            cur += ch
    args.append(cur)
    return args


def ue_texture_macros(code, name, prop):
    """Texture2DSample(Tex, sampler, uv) style calls on the Custom node input `name`, rewritten to sample the texture property `prop`."""
    forms = {"Texture2DSample": 3, "Texture2DSampleLevel": 4, "Texture2DSampleBias": 4, "Texture2DSampleGrad": 5}
    out, i = "", 0
    pat = re.compile(r"\b(Texture2DSampleLevel|Texture2DSampleBias|Texture2DSampleGrad|Texture2DSample)\s*\(")
    while True:
        m = pat.search(code, i)
        if not m:
            return out + code[i:]
        depth, j = 1, m.end()
        while j < len(code) and depth:
            depth += {"(": 1, ")": -1}.get(code[j], 0)
            j += 1
        args = [a.strip() for a in ue_split_args(code[m.end():j - 1])]
        out += code[i:m.start()]
        if len(args) == forms[m.group(1)] and args[0] == name:
            uv = args[2]
            if m.group(1) == "Texture2DSample":
                out += "tex2D(%s, %s)" % (prop, uv)
            elif m.group(1) == "Texture2DSampleLevel":
                out += "tex2Dlod(%s, float4((%s).xy, 0.0, %s))" % (prop, uv, args[3])
            elif m.group(1) == "Texture2DSampleBias":
                out += "tex2Dbias(%s, float4((%s).xy, 0.0, %s))" % (prop, uv, args[3])
            else:
                out += "tex2Dgrad(%s, %s, %s, %s)" % (prop, uv, args[3], args[4])
        else:
            out += code[m.start():j]
        i = j

VIEW_HELPERS = """
float4x4 ViewWorldToClipUE()
{
    float4x4 w = float4x4(0.01, 0.0, 0.0, 0.0, 0.0, 0.0, 0.01, 0.0, 0.0, 0.01, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0);
    return transpose(mul(UNITY_MATRIX_VP, w) * 100.0);
}

float4x4 ViewWorldToViewUE()
{
    float4x4 w = float4x4(0.01, 0.0, 0.0, 0.0, 0.0, 0.0, 0.01, 0.0, 0.0, 0.01, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0);
    float4x4 d = float4x4(100.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0.0, 0.0, 0.0, 0.0, -100.0, 0.0, 0.0, 0.0, 0.0, 1.0);
    return transpose(mul(d, mul(UNITY_MATRIX_V, w)));
}

float4x4 ViewViewToWorldUE()
{
    float4x4 wi = float4x4(100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0.0, 0.0, 100.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0);
    float4x4 di = float4x4(0.01, 0.0, 0.0, 0.0, 0.0, 0.01, 0.0, 0.0, 0.0, 0.0, -0.01, 0.0, 0.0, 0.0, 0.0, 1.0);
    return transpose(mul(wi, mul(UNITY_MATRIX_I_V, di)));
}

float4x4 ViewViewToClipUE()
{
    float4x4 di = float4x4(0.01, 0.0, 0.0, 0.0, 0.0, 0.01, 0.0, 0.0, 0.0, 0.0, -0.01, 0.0, 0.0, 0.0, 0.0, 1.0);
    return transpose(mul(UNITY_MATRIX_P, di) * 100.0);
}

float4x4 ViewClipToViewUE()
{
    float4x4 d = float4x4(100.0, 0.0, 0.0, 0.0, 0.0, 100.0, 0.0, 0.0, 0.0, 0.0, -100.0, 0.0, 0.0, 0.0, 0.0, 1.0);
    return transpose(mul(d, unity_CameraInvProjection) * 0.01);
}
"""


def ue_custom_code(code, ow):
    """Makes the code of an Unreal Custom node valid Unity code. Returns (declarations, body, ok)."""
    decls = []

    def view_member(m):
        n = m.group(1)
        if n in VIEW_MATRICES:
            v = "FPU_" + n
            d = "float4x4 %s = %s();" % (v, VIEW_MATRICES[n])
            if d not in decls:
                decls.append(d)
            return v
        return VIEW_VALUES.get(n, m.group(0))

    code = UE_ATTRIBUTE_MACROS.sub("", code)
    code = re.sub(r"\bProcessMaterial(?:Linear)?ColorTextureLookup\s*\(", "(", code)
    code = re.sub(r"\bUnpackNormalMap\s*\(", "UnpackNormalMapUE(", code)
    code = re.sub(r"\bLWCToFloat\s*\(", "(", code)
    code = re.sub(r"\bGetPrimitiveData\s*\(\s*Parameters\s*\)\s*\.\s*LocalObjectBounds(Min|Max)\b", r"_O_Bounds\1", code)
    code = re.sub(r"\bMod\s*\(", "fmod(", code)
    code = re.sub(r"\b(?:ResolvedView|View)\s*\.\s*(\w+)", view_member, code)
    code = re.sub(r"\bMaterialFloat(?=\d|\b)", "float", code)
    if not re.search(r"\breturn\b", code):
        code = "return " + code.strip().rstrip(";").strip() + ";"
    left = CUSTOM_UE_SYMBOLS.search(code) or re.search(r"\.Sample\w*\(", code)
    if left:
        return [], "return (float%s)0;" % ("" if ow == 1 else ow), False
    return decls, code, True


PORTER_DP_HELPERS = """
float PorterDP1(float b, float s, float4 d, float4 e)
{
    // slots 0..3 ride in the first custom stream (d), 4..7 in the second (e); a negative slot keeps the property's own value
    return s < -0.5 ? b : (s < 3.5 ? (s < 1.5 ? (s < 0.5 ? d.x : d.y) : (s < 2.5 ? d.z : d.w))
                                   : (s < 5.5 ? (s < 4.5 ? e.x : e.y) : (s < 6.5 ? e.z : e.w)));
}
float4 PorterDP4(float4 b, float4 s, float4 d, float4 e)
{
    return float4(PorterDP1(b.x, s.x, d, e), PorterDP1(b.y, s.y, d, e), PorterDP1(b.z, s.z, d, e), PorterDP1(b.w, s.w, d, e));
}
"""

HELPERS = """
float3 RgbToHsvUE(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    float e = 0.0000000001;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 HsvToRgbUE(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

float3 RotateAboutAxisUE(float3 axis, float angle, float3 pivot, float3 pos)
{
    float3 closest = pivot + axis * dot(axis, pos - pivot);
    float3 u = pos - closest;
    float3 v = cross(axis, u);
    return closest + u * cos(angle) + v * sin(angle) - pos;
}

float3 BlackBodyUE(float temp)
{
    float u = (0.860117757 + 1.54118254e-4 * temp + 1.28641212e-7 * temp * temp) / (1.0 + 8.42420235e-4 * temp + 7.08145163e-7 * temp * temp);
    float v = (0.317398726 + 4.22806245e-5 * temp + 4.20481691e-8 * temp * temp) / (1.0 - 2.89741816e-5 * temp + 1.61456053e-7 * temp * temp);
    float d = 2.0 * u - 8.0 * v + 4.0;
    float x = 3.0 * u / d;
    float y = 2.0 * v / d;
    float3 xyz = float3(x / y, 1.0, (1.0 - x - y) / y);
    float3 rgb = float3(dot(float3(3.2404542, -1.5371385, -0.4985314), xyz), dot(float3(-0.9692660, 1.8760108, 0.0415560), xyz), dot(float3(0.0556434, -0.2040259, 1.0572252), xyz));
    return rgb * pow(0.0004 * temp, 4.0);
}
"""

TONE_HELPERS = r"""
float3 PorterFilm(float3 c)
{
    const float slope = 0.88;
    const float toe = 0.55;
    const float shoulder = 0.26;
    const float whiteClip = 0.04;
    const float blackClip = 0.0;
    const float toeScale = 1.0 + blackClip - toe;
    const float shoulderScale = 1.0 + whiteClip - shoulder;
    const float bt = (0.18 + blackClip) / toeScale - 1.0;
    const float toeMatch = log10(0.18) - 0.5 * log((1.0 + bt) / (1.0 - bt)) * (toeScale / slope);
    const float straightMatch = (1.0 - toe) / slope - toeMatch;
    const float shoulderMatch = shoulder / slope - straightMatch;
    float3 lc = log10(max(c, 1e-6));
    float3 straight = slope * (lc + straightMatch);
    float3 toeColor = -blackClip + 2.0 * toeScale / (1.0 + exp(-2.0 * (slope / toeScale) * (lc - toeMatch)));
    float3 shoulderColor = (1.0 + whiteClip) - 2.0 * shoulderScale / (1.0 + exp(2.0 * (slope / shoulderScale) * (lc - shoulderMatch)));
    toeColor = lerp(straight, toeColor, step(lc, toeMatch));
    shoulderColor = lerp(straight, shoulderColor, step(shoulderMatch, lc));
    float3 t = saturate((lc - toeMatch) / (shoulderMatch - toeMatch));
    if (shoulderMatch < toeMatch)
        t = 1.0 - t;
    t = (3.0 - 2.0 * t) * t * t;
    float3 o = saturate(lerp(toeColor, shoulderColor, t));
    o = lerp(float3(0.0, 0.0, 0.0), o, step(1e-5, c));
#ifdef UNITY_COLORSPACE_GAMMA
    o = pow(o, 1.0 / 2.2);
#endif
    return o;
}
"""

CLOUD_HELPERS = r"""
float3 PorterCloudFilm(float3 c)
{
    float peak = max(c.r, max(c.g, c.b));
    float3 o = PorterFilm(c);
    float l = dot(o, float3(0.2126, 0.7152, 0.0722));
    return lerp(o, float3(l, l, l), 0.6 * saturate((peak - 1.0) / 8.0));
}
"""

ATMOSPHERE_HELPERS = r"""
float4 _PorterSunDir;
float4 _PorterMoonDir;
float4 _AtmoRayleigh;
float4 _AtmoMie;
float4 _AtmoMieAbs;
float4 _AtmoOzone;
float4 _AtmoShape;
float4 _AtmoTent;
float4 _AtmoFactor;
float4 _AtmoLight;
float _AtmoSun;
float _AtmoMoon;
float _AtmoMieScale;
float PorterRaySphere(float3 o, float3 d, float r)
{
    float b = dot(o, d);
    float c = dot(o, o) - r * r;
    float disc = b * b - c;
    return disc < 0.0 ? -1.0 : -b + sqrt(disc);
}

float3 PorterExtinction(float h)
{
    float rd = exp(-h / max(_AtmoShape.y, 0.01));
    float md = exp(-h / max(_AtmoShape.z, 0.01));
    float od = _AtmoTent.x * max(1.0 - abs(h - 25.0) / max(_AtmoTent.y, 0.01), 0.0);
    return _AtmoRayleigh.rgb * (0.0331 * rd) + _AtmoMie.rgb * (0.003996 * _AtmoMieScale * md) + _AtmoMieAbs.rgb * (0.00444 * _AtmoMieScale * md) + _AtmoOzone.rgb * (0.001881 * od);
}

float3 PorterScatterLight(float3 viewDir, float3 lightDir, float illuminance)
{
    float3 o = float3(0.0, 6360.05, 0.0);
    float tTop = PorterRaySphere(o, viewDir, 6420.0);
    float tGround = PorterRaySphere(o, viewDir, 6360.0);
    float tEnd = tTop;
    if (tGround > 0.0)
    {
        float b = dot(o, viewDir);
        float c = dot(o, o) - 6360.0 * 6360.0;
        float disc = b * b - c;
        tEnd = min(tTop, -b - sqrt(max(disc, 0.0)));
    }
    tEnd = max(tEnd, 0.0);
    float mu = dot(viewDir, lightDir);
    float g = clamp(_AtmoShape.x, -0.99, 0.99);
    float phaseR = 0.0596831 * (1.0 + mu * mu);
    float phaseM = 0.0795775 * (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * mu, 0.0001), 1.5);
    float3 optical = float3(0.0, 0.0, 0.0);
    float3 sum = float3(0.0, 0.0, 0.0);
    [loop] for (int i = 0; i < 16; i++)
    {
        float f = (i + 0.5) / 16.0;
        float tMid = tEnd * f * f;
        float ds = tEnd * 2.0 * f / 16.0;
        float3 p = o + viewDir * tMid;
        float h = max(length(p) - 6360.0, 0.0);
        float3 ext = PorterExtinction(h);
        optical += ext * ds;
        float3 sunOptical = float3(0.0, 0.0, 0.0);
        float tl = PorterRaySphere(p, lightDir, 6420.0);
        float lit = 1.0;
        float pl = dot(p, lightDir);
        float cl = dot(p, p) - 6360.0 * 6360.0;
        if (pl < 0.0 && pl * pl - cl > 0.0) lit = 0.0;
        [loop] for (int j = 0; j < 8; j++)
        {
            float fj = (j + 0.5) / 8.0;
            float3 q = p + lightDir * (max(tl, 0.0) * fj * fj);
            sunOptical += PorterExtinction(max(length(q) - 6360.0, 0.0)) * (max(tl, 0.0) * 2.0 * fj / 8.0);
        }
        float rd = exp(-h / max(_AtmoShape.y, 0.01));
        float md = exp(-h / max(_AtmoShape.z, 0.01));
        float3 scatterR = _AtmoRayleigh.rgb * (0.0331 * rd);
        float3 scatterM = _AtmoMie.rgb * (0.003996 * _AtmoMieScale * md);
        float3 scatter = scatterR * phaseR + scatterM * phaseM + (scatterR + scatterM) * (0.0795775 * _AtmoTent.z);
        sum += scatter * exp(-(optical + sunOptical)) * lit * ds;
    }
    return sum * illuminance * _AtmoLight.rgb;
}

float3 PorterAtmosphereView(float3 viewDir)
{
    float3 sunDir = dot(_PorterSunDir.xyz, _PorterSunDir.xyz) > 0.25 ? normalize(_PorterSunDir.xyz) : normalize(_WorldSpaceLightPos0.xyz);
    float3 col = PorterScatterLight(normalize(viewDir), sunDir, _AtmoSun);
    if (dot(_PorterMoonDir.xyz, _PorterMoonDir.xyz) > 0.25)
        col += PorterScatterLight(normalize(viewDir), normalize(_PorterMoonDir.xyz), _AtmoMoon);
    return col * _AtmoFactor.rgb;
}
"""

NOISE_HELPERS = """
float3 NoiseHashUE(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx);
}

float NoiseCornerUE(float3 i, float3 f, float3 o)
{
    return dot(NoiseHashUE(i + o) * 2.0 - 1.0, f - o);
}

float NoiseGradUE(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float x0 = lerp(NoiseCornerUE(i, f, float3(0.0, 0.0, 0.0)), NoiseCornerUE(i, f, float3(1.0, 0.0, 0.0)), u.x);
    float x1 = lerp(NoiseCornerUE(i, f, float3(0.0, 1.0, 0.0)), NoiseCornerUE(i, f, float3(1.0, 1.0, 0.0)), u.x);
    float x2 = lerp(NoiseCornerUE(i, f, float3(0.0, 0.0, 1.0)), NoiseCornerUE(i, f, float3(1.0, 0.0, 1.0)), u.x);
    float x3 = lerp(NoiseCornerUE(i, f, float3(0.0, 1.0, 1.0)), NoiseCornerUE(i, f, float3(1.0, 1.0, 1.0)), u.x);
    return clamp(lerp(lerp(x0, x1, u.y), lerp(x2, x3, u.y), u.z) * 1.4, -1.0, 1.0);
}

float NoiseUE(float3 pos, float scale, float levels, float levelScale, float turbulence, float lo, float hi)
{
    float3 p = pos * scale;
    float sum = 0.0;
    float wsum = 0.0;
    float w = 1.0;
    for (int i = 0; i < 8; i++)
    {
        if (i < (int)levels)
        {
            float n = NoiseGradUE(p);
            sum += (turbulence > 0.5 ? abs(n) : n * 0.5 + 0.5) * w;
            wsum += w;
            p *= levelScale;
            w *= 0.5;
        }
    }
    return lo + (sum / max(wsum, 0.0001)) * (hi - lo);
}

float3 CellNoiseUE(float3 p)
{
    return NoiseHashUE(floor(p));
}

float4 VoronoiNoiseUE(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float best = 8.0;
    float3 bestPos = float3(0.0, 0.0, 0.0);
    for (int z = -1; z <= 1; z++)
    {
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                float3 o = float3(x, y, z);
                float3 c = NoiseHashUE(i + o);
                float3 d = o + c - f;
                float dist = dot(d, d);
                if (dist < best)
                {
                    best = dist;
                    bestPos = i + o + c;
                }
            }
        }
    }
    return float4(bestPos, sqrt(best));
}

float3 GradientNoise3UE(float3 p)
{
    return float3(NoiseGradUE(p), NoiseGradUE(p + 17.31), NoiseGradUE(p + 41.73));
}
"""


DEPTH_HELPERS = """
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
float _PorterDepthFade;

float SceneDepthUE(float4 sp)
{
    if (_PorterDepthFade < 0.5)
        return _ProjectionParams.z * 100.0;
    float d = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(sp)));
    return d * 100.0;
}

float DepthFadeUE(float4 sp, float dist)
{
    if (_PorterDepthFade < 0.5)
        return 1.0;
    float raw = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(sp));
    if (raw < 0.000001)
        return 1.0;
    float scene = LinearEyeDepth(raw) * 100.0;
    float pix = sp.w * 100.0;
    return saturate((scene - pix) / max(dist, 0.0001));
}
"""


def prop_group(label):
    parts = [t for t in re.split(r"[\s_\-\.,]+|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", str(label).strip()) if t]
    if not parts:
        return ""
    w = re.sub(r"[^A-Za-z0-9 ]", "", parts[0])
    return w if w.isupper() else w.title()


def nice_label(label):
    t = re.sub(r"[,]+", "", str(label))
    t = re.sub(r"[_]+", " ", t)
    t = re.sub(r"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ", t)
    return re.sub(r"\s+", " ", t).strip() or str(label)


def group_props(props):
    """Group shader properties by the first word of their UE parameter name; singletons go to General."""
    own = [(k, v) for k, v in props.items() if not k.startswith("_C_")]
    counts = {}
    for k, v in own:
        counts[prop_group(v["label"])] = counts.get(prop_group(v["label"]), 0) + 1
    groups = {}
    for k, v in own:
        g = prop_group(v["label"])
        groups.setdefault(g if g and counts[g] >= 2 else "General", []).append((k, v))
    out = []
    for g in sorted(groups, key=lambda x: (x != "General", x)):
        out.append((g, sorted(groups[g], key=lambda kv: (kv[1]["kind"] != "vector", kv[1]["label"].lower()))))
    glob = [(k, v) for k, v in props.items() if k.startswith("_C_")]
    if glob:
        out.append(("Globals", sorted(glob, key=lambda kv: kv[1]["label"].lower())))
    return out


def truthy(v):
    if isinstance(v, str):
        return v.strip().lower() in ("true", "1", "yes")
    return bool(v)


def find_two_sided(entry, asset, mp):
    keys = ("TwoSided", "bTwoSided", "IsTwoSided", "two_sided", "twoSided")

    def read(d):
        if not isinstance(d, dict):
            return None
        bpo = d.get("BasePropertyOverrides")
        if isinstance(bpo, dict) and truthy(bpo.get("bOverride_TwoSided", False)) and "TwoSided" in bpo:
            return truthy(bpo["TwoSided"])
        for k in keys:
            if k in d:
                return truthy(d[k])
        return None

    def deep(d, depth):
        if depth > 5:
            return None
        if isinstance(d, dict):
            r = read(d)
            if r is not None:
                return r
            for v in d.values():
                r = deep(v, depth + 1)
                if r is not None:
                    return r
        elif isinstance(d, list):
            for v in d[:50]:
                r = deep(v, depth + 1)
                if r is not None:
                    return r
        return None

    for label, d in (("master material asset settings", asset), ("material data", entry), ("master material", mp)):
        r = read(d)
        if r is not None:
            return r, label
    r = deep(entry, 0)
    return bool(r), ("nested material data" if r is not None else "no two-sided setting found in the material data")


def build(src, entry, name, graph_path, blend=None, two_sided=None):
    g = src.graph(graph_path, os.path.basename(graph_path))
    if g is None:
        raise SystemExit("cannot load graph " + graph_path)
    tr = Translator(src, entry)
    ed = g.editor_data()
    pins = (ed or {}).get("Properties") or {}
    vals = {}
    tr.mode = "ps"

    def pin_attr(attr):
        for k, a in PIN_ATTR.items():
            if a == attr and linked(pins.get(k)):
                return pins[k]
        return None

    use_attrs = linked(pins.get("MaterialAttributes"))
    if use_attrs:
        root = tr.input(g, pins["MaterialAttributes"], {}, None)
        attrs = Translator.attrs_of(root)
        get = lambda n: attrs.get(tr, n)
    else:
        get = lambda n: (tr.attr_value(tr.input(g, pin_attr(n), {}, None), n) if pin_attr(n) else tr.attr_default(n))
    for n in ("BaseColor", "Metallic", "Roughness", "EmissiveColor", "Opacity", "OpacityMask", "Normal", "AmbientOcclusion", "ShadingModel"):
        vals[n] = get(n)
    ps_body = list(tr.bodies["ps"])
    tr.bodies["ps"] = []
    tr.mode = "vs"
    wpo = get("WorldPositionOffset")
    vs_body = list(tr.bodies["vs"])
    has_wpo = not (wpo.const and wpo.e.replace(" ", "").replace("0.0,", "").replace("0.0", "").replace("float3()", "") in ("", "float3()"))
    tr.mode = "ps"
    tr.bodies["ps"] = ps_body

    mat = g.material_object()
    mp = (mat or {}).get("Properties") or {}
    asset = entry.get("asset") or {}
    ov = entry.get("overrides") or {}
    bm_raw = blend or ov.get("BlendMode") or asset.get("BlendMode") or mp.get("BlendMode") or "BLEND_Opaque"
    bm, bm_known = norm_blend(bm_raw)
    tr.notes.append("blend mode: read %r -> %s%s%s" % (bm_raw, bm, " (set by the material instance)" if (not blend and ov.get("BlendMode")) else "", "" if bm_known else " (unrecognised value, treated as Translucent)"))
    sm_c = vals["ShadingModel"]
    unlit = False
    try:
        unlit = float(sm_c.e) == 0.0
    except ValueError:
        pass
    sm_name = str(asset.get("ShadingModel") or (mp.get("ShadingModel") or "")).split("::")[-1]
    ms = str(mp.get("ShadingModel", "")).split("::")[-1]
    sm_over = str(ov.get("ShadingModel") or "").split("::")[-1]
    if sm_over and pin_attr("ShadingModel") is None and not use_attrs:
        unlit = "Unlit" in sm_over
        tr.notes.append("shading model: %s (set by the material instance)" % sm_over)
    elif "Unlit" in sm_name or "Unlit" in ms:
        unlit = True
    elif not use_attrs and not sm_name and not ms:
        unlit = False
    if two_sided is not None:
        two, two_from = two_sided, "set by the caller"
    elif "TwoSided" in ov:
        two, two_from = truthy(ov["TwoSided"]), "set by the material instance"
    else:
        two, two_from = find_two_sided(entry, asset, mp)
    clip = ov.get("OpacityMaskClipValue", asset.get("OpacityMaskClipValue", mp.get("OpacityMaskClipValue", 0.3333)))
    try:
        clip = float(clip)
    except (TypeError, ValueError):
        clip = 0.3333
    tr.notes.append("two-sided: %s, from %s" % ("yes (Cull Off, back faces lit with flipped normal)" if two else "no (back faces culled; the Cull setting on the material can override this)", two_from))
    tangent_space_normal = True
    for src_d in (ov, asset, mp):
        if isinstance(src_d, dict) and "bTangentSpaceNormal" in src_d:
            tangent_space_normal = truthy(src_d["bTangentSpaceNormal"])
            break
    if not tangent_space_normal:
        tr.notes.append("normal output: world space (Tangent Space Normal is off), converted to tangent space for Unity")

    f3 = lambda n: tr.fit(vals[n], 3).e
    f1 = lambda n: tr.fit(vals[n], 1).e
    o = []
    o.append('Shader "UEPort/%s"' % name)
    o.append("{")
    o.append("    Properties")
    o.append("    {")
    hard = {k: v for k, v in tr.props.items() if is_hardcoded(k, v["label"])}
    for k, v in hard.items():
        v["hidden"] = True
    for title, items in group_props({k: v for k, v in tr.props.items() if k not in hard}):
        o.append("        [Header(%s)]" % title)
        for k, v in items:
            label = nice_label(v["label"]).replace('"', "'")
            if v["kind"] == "float":
                o.append('        %s ("%s", Float) = %s' % (k, label, pf(v["default"])))
            else:
                if v.get("color"):
                    # Real Color property: Unity draws the picker itself, no script needed. Unity treats Color values as sRGB and converts them to
                    # linear for the shader in a Linear project, so the default is stored as the gamma value that converts back to Unreal's linear one.
                    d = v["default"]
                    o.append('        [HDR] %s ("%s", Color) = (%s)' % (k, label, ", ".join(pf(c) for c in (linear_to_gamma(d[0]), linear_to_gamma(d[1]), linear_to_gamma(d[2]), d[3]))))
                else:
                    o.append('        %s ("%s", Vector) = (%s)' % (k, label, ", ".join(pf(c) for c in v["default"])))
        o.append("        [Space(6)]")
    if "pstreams" in tr.uses:
        o.append('        [HideInInspector] _PorterParticleStreams ("Particle Streams", Float) = 0')
    if "depth" in tr.uses:
        o.append('        [Toggle] _PorterDepthFade ("Use Scene Depth (soft particles; needs a depth texture, which VRChat only gives with a real-time shadowed light and never in mirrors)", Float) = 0')
    if has_wpo:
        o.append('        [Toggle] _PorterWpoScale ("Scale Vertex Animation (World Position Offset) With Object Size", Float) = 1')
    if bm == "BLEND_Masked":
        o.append('        [HideInInspector] _Cutoff ("Mask Clip", Range(0, 1)) = %s' % pf(clip))
    for i in sorted(tr.dpidx):
        o.append('        [HideInInspector] _PorterDPSlot%d ("Particle Data Slots %d", Vector) = (-1, -1, -1, -1)' % (i, i))
    for prop in sorted(tr.slotprops):
        o.append('        [HideInInspector] %s ("Particle Data Slot", Float) = -1' % prop)
    cloud_tone = unlit and bm == "BLEND_Translucent" and "bakedclouds" in name.lower()
    if cloud_tone:
        o.append("        [Header(Brightness)]")
        o.append('        _CloudExposure ("Cloud Exposure (multiplier)", Float) = 1.0')
        o.append("        [Space(6)]")
    sky_exposure = unlit and SKY_MATERIAL and bm == "BLEND_Opaque"
    if sky_exposure:
        o.append("        [Header(Brightness)]")
        o.append('        _SkyExposure ("Sky Exposure", Float) = 1.0')
        o.append("        [Space(6)]")
    if "atmosphere" in tr.uses:
        o.append("        [Header(Atmosphere)]")
        o.append('        _AtmoSun ("Sun Brightness", Float) = 12')
        o.append('        _AtmoMoon ("Moon Brightness", Float) = 12')
        o.append('        _AtmoMieScale ("Mie Scale (sun halo size)", Float) = 2.0')
        o.append("        [HideInInspector] _PorterSunDir (\"Sun Direction\", Vector) = (0, 0, 0, 0)")
        o.append("        [HideInInspector] _PorterMoonDir (\"Moon Direction\", Vector) = (0, 0, 0, 0)")
        o.append("        [HideInInspector] _AtmoRayleigh (\"Rayleigh\", Vector) = (0.175287, 0.409607, 1, 0)")
        o.append("        [HideInInspector] _AtmoMie (\"Mie\", Vector) = (1, 1, 1, 0)")
        o.append("        [HideInInspector] _AtmoMieAbs (\"Mie Absorption\", Vector) = (1, 1, 1, 0)")
        o.append("        [HideInInspector] _AtmoOzone (\"Ozone\", Vector) = (0.345561, 1, 0.045189, 0)")
        o.append("        [HideInInspector] _AtmoShape (\"Shape\", Vector) = (0.8, 8, 1.2, 0)")
        o.append("        [HideInInspector] _AtmoTent (\"Ozone Tent and Multi Scattering\", Vector) = (1, 15, 1, 0)")
        o.append("        [HideInInspector] _AtmoFactor (\"Luminance Factor\", Vector) = (1, 1, 1, 0)")
        o.append("        [HideInInspector] _AtmoLight (\"Light Color\", Vector) = (1, 1, 1, 0)")
        o.append("        [Space(6)]")
    sun_cap = unlit and bm == "BLEND_Additive" and (SKY_MATERIAL or "sundandmoon" in name.lower() or "sunandmoon" in name.lower())
    if sun_cap:
        o.append("        [Header(Brightness)]")
        o.append('        _SkyExposure ("Sun Exposure", Float) = 1.0')
        o.append("        [Space(6)]")
    if not unlit:
        o.append("        [Header(Lighting)]")
        o.append('        [Toggle] _FlipNormalY ("Flip Normal Y (DirectX normal maps)", Float) = 0')
    o.append("        [Header(Rendering)]")
    o.append('        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull (Off = two-sided)", Float) = %d' % (0 if (two or sun_cap) else 2))
    if tr.textures:
        o.append("        [Header(Textures)]")
    for k, v in tr.textures.items():
        o.append('        %s%s ("%s", 2D) = "%s" {}' % ("[Normal] " if v.get("normal") else "", v["prop"], os.path.basename(k).split(".")[0], "bump" if v.get("normal") else "white"))
    while o and re.fullmatch(r"\s*(?:\[[^\]\n]*\]\s*)+", o[-1]):
        o.pop()
    o.append("    }")
    o.append("    SubShader")
    o.append("    {")
    tags = {"BLEND_Opaque": "Opaque", "BLEND_Masked": "TransparentCutout"}.get(bm, "Transparent")
    queue = {"BLEND_Opaque": "Geometry", "BLEND_Masked": "AlphaTest"}.get(bm, "Transparent")
    sky_first = SKY_MATERIAL and bm == "BLEND_Opaque"
    if sky_first:
        # Unreal draws a sky dome at the far plane whatever its size. Drawing it first without writing depth does the same here, so the
        # clouds and everything else always draw over it (the converted prefab also keeps the dome centred on the camera).
        queue = "Background"
    o.append('        Tags { "RenderType"="%s" "Queue"="%s" "IgnoreProjector"="True" }' % (tags, queue))
    if sky_first:
        o.append("        ZWrite Off")
    o.append("        Cull [_Cull]")
    if bm == "BLEND_Additive":
        o.append("        Blend One One")
        o.append("        ZWrite Off")
    elif bm == "BLEND_Modulate":
        o.append("        Blend DestColor Zero")
        o.append("        ZWrite Off")
    elif bm in ("BLEND_Translucent", "BLEND_AlphaComposite", "BLEND_AlphaHoldout"):
        o.append("        Blend SrcAlpha OneMinusSrcAlpha" if bm != "BLEND_AlphaComposite" else "        Blend One OneMinusSrcAlpha")
        o.append("        ZWrite Off")
    o.append("        CGPROGRAM")
    pragma = "#pragma surface surf %s" % ("Unlit vertex:vert noshadow addshadow" if unlit else "Standard fullforwardshadows vertex:vert addshadow")
    if bm == "BLEND_Masked":
        pragma += " alphatest:_Cutoff"
    elif bm in ("BLEND_Translucent", "BLEND_AlphaHoldout"):
        pragma += " alpha:fade"
    elif bm == "BLEND_AlphaComposite":
        pragma += " alpha:premul"
    # Unlit shaders never use lightmaps, spherical harmonics, vertex lights or the additive light pass. Without these keywords Unity still
    # adds a lightmap-UV and an SH interpolator to ForwardBase, which pushes particle shaders (tc01, color, pcol, objPos, screenPos and
    # the 3 tangent-to-world rows from INTERNAL_DATA) from 9 to 11 and fails "Too many texture interpolators" on target 3.0 (max 10).
    pragma += " nofog noforwardadd nolightmap nodynlightmap nodirlightmap novertexlights noambient" if unlit else ""
    o.append("        " + pragma)
    o.append("        #pragma target %s" % ("4.0" if "atmosphere" in tr.uses else ("3.5" if ("pcolor" in tr.uses or "dpstreams" in tr.uses) else "3.0")))
    o.append("        #include \"UnityCG.cginc\"")
    o.append("        #include \"Lighting.cginc\"")
    if "atmosphere" in tr.uses:
        o.extend("        " + l if l else "" for l in ATMOSPHERE_HELPERS.strip("\n").split("\n"))
    if sky_exposure or sun_cap or cloud_tone:
        o.extend("        " + l if l else "" for l in TONE_HELPERS.strip("\n").split("\n"))
    if cloud_tone:
        o.extend("        " + l if l else "" for l in CLOUD_HELPERS.strip("\n").split("\n"))
    if "depth" in tr.uses:
        o.extend("        " + l if l else "" for l in DEPTH_HELPERS.strip("\n").split("\n"))
    if "noise" in tr.uses:
        o.extend("        " + l if l else "" for l in NOISE_HELPERS.strip("\n").split("\n"))
    used = "".join(tr.bodies["ps"] + vs_body)
    if "FPU_NZ(" in used:
        o.append("        #define FPU_NZ(b) ((b) + ((b) == 0.0) * 0.00001)")
    if "FPU_UV_SPAN" in used:
        o.append("        #define FPU_UV_SPAN %s" % fl(LOCAL_UV_SPAN))
    if "PorterDP4(" in used or "PorterDP1(" in used:
        o.extend("        " + l if l else "" for l in PORTER_DP_HELPERS.strip("\n").split("\n"))
    for fn_name in ("RgbToHsvUE", "HsvToRgbUE", "RotateAboutAxisUE", "BlackBodyUE"):
        if fn_name in used:
            block = re.search(r"float3 %s\(.*?\n\}\n" % fn_name, HELPERS, re.S).group(0)
            o.extend("        " + l if l else "" for l in block.strip("\n").split("\n"))
    custom_text = "".join(tr.customs)
    for fn_name, block in chk_function_blocks(VIEW_HELPERS).items():
        if fn_name in custom_text:
            o.extend("        " + l if l else "" for l in block.strip("\n").split("\n"))
    for fn_name, block in chk_function_blocks(NORMAL_HELPERS).items():
        if fn_name in custom_text:
            o.extend("        " + l if l else "" for l in block.strip("\n").split("\n"))
    for k, v in tr.textures.items():
        o.append("        sampler2D %s;" % v["prop"])
    for k, v in tr.props.items():
        if k in hard:
            if v["kind"] == "float":
                o.append("        static const float %s = %s;" % (k, pf(v["default"])))
            else:
                o.append("        static const float4 %s = float4(%s);" % (k, ", ".join(pf(c) for c in v["default"])))
        else:
            o.append("        %s %s;" % ("float" if v["kind"] == "float" else "float4", k))
    if "pstreams" in tr.uses:
        o.append("        float _PorterParticleStreams;")
    if has_wpo:
        o.append("        float _PorterWpoScale;")
    for i in sorted(tr.dpidx):
        o.append("        float4 _PorterDPSlot%d;" % i)
    for prop in sorted(tr.slotprops):
        o.append("        float %s;" % prop)
    if not unlit:
        o.append("        float _FlipNormalY;")
    if cloud_tone:
        o.append("        float _CloudExposure;")
    if sky_exposure:
        o.append("        float _SkyExposure;")
    if sun_cap:
        o.append("        float _SkyExposure;")
    for c in tr.customs:
        o.extend("        " + l for l in c.split("\n"))
    o.append("")
    o.append("        struct Input")
    o.append("        {")
    o.append("            float4 tc01;")
    o.append("            float3 worldPos;")
    o.append("            float3 viewDir;")
    o.append("            float4 color : COLOR;")
    if "pcolor" in tr.uses:
        o.append("            float4 pcol;")
    if "dpstreams" in tr.uses:
        o.append("            float4 pdp;")
        o.append("            float4 pdp2;")
    o.append("            float3 objPos;")
    o.append("            float4 screenPos;")
    o.append("            float3 worldNormal;")
    if two or "facing" in tr.uses:
        o.append("            float facing : VFACE;")
    o.append("            INTERNAL_DATA")
    o.append("        };")
    o.append("")
    o.append("        void vert(inout appdata_full v, out Input o)")
    o.append("        {")
    o.append("            UNITY_INITIALIZE_OUTPUT(Input, o);")
    if "pstreams" in tr.uses:
        o.append("            o.tc01 = float4(v.texcoord.xy, lerp(v.texcoord1.xy, v.texcoord.zw, _PorterParticleStreams));")
        if "pcolor" in tr.uses:
            o.append("            o.pcol = v.texcoord1;")
    else:
        o.append("            o.tc01 = float4(v.texcoord.xy, v.texcoord1.xy);")
    if "dpstreams" in tr.uses:
        o.append("            o.pdp = v.texcoord2;")
        o.append("            o.pdp2 = v.texcoord3;")
    o.append("            o.objPos = v.vertex.xyz;")
    if has_wpo:
        o.append("            float3 vpos = v.vertex.xyz;")
        o.append("            float3 vnrm = v.normal;")
        o.append("            float4 vtan = v.tangent;")
        o.append("            float4 vcol = v.color;")
        o.append("            float2 uv0 = v.texcoord.xy;")
        o.append("            float2 uv1 = v.texcoord1.xy;")
        o.append("            float3 wpoLen = float3(length(unity_ObjectToWorld._m00_m10_m20), length(unity_ObjectToWorld._m01_m11_m21), length(unity_ObjectToWorld._m02_m12_m22));")
        o.append("            float3x3 wpoRot = mul((float3x3)unity_ObjectToWorld, float3x3(1.0 / max(wpoLen.x, 1e-6), 0.0, 0.0, 0.0, 1.0 / max(wpoLen.y, 1e-6), 0.0, 0.0, 0.0, 1.0 / max(wpoLen.z, 1e-6)));")
        o.append("            float3x3 wpoMat = lerp((float3x3)unity_ObjectToWorld, wpoRot, _PorterWpoScale);")
        for l in vs_body:
            if sun_cap:
                l = l.replace("float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m23, unity_ObjectToWorld._m13) * 100.0", "float3(0.0, 0.0, 0.0)")
            o.append("            " + l.replace("(float3x3)unity_ObjectToWorld", "wpoMat"))
        o.append("            float3 wpoUE = %s;" % tr.fit(wpo, 3).e)
        o.append("            v.vertex.xyz += mul((float3x3)unity_WorldToObject, wpoUE.xzy * 0.01) * lerp(1.0, (wpoLen.x + wpoLen.y + wpoLen.z) / 3.0, _PorterWpoScale);")
    o.append("        }")
    o.append("")
    if unlit:
        o.append("        half4 LightingUnlit(SurfaceOutput s, half3 lightDir, half atten)")
        o.append("        {")
        o.append("            return half4(0.0, 0.0, 0.0, s.Alpha);")
        o.append("        }")
        o.append("")
        o.append("        void surf(Input IN, inout SurfaceOutput o)")
    else:
        o.append("        void surf(Input IN, inout SurfaceOutputStandard o)")
    o.append("        {")
    for l in tr.bodies["ps"]:
        o.append("            " + l)
    if unlit:
        o.append("            o.Albedo = 0.0;")
        if sun_cap:
            o.append("            float3 skyEm = max(%s, 0.0) * _SkyExposure;" % f3("EmissiveColor"))
            o.append("            o.Emission = PorterFilm(skyEm);")
        elif cloud_tone:
            o.append("            float3 cEm = max(%s, 0.0) * _CloudExposure;" % f3("EmissiveColor"))
            o.append("            o.Emission = PorterCloudFilm(cEm);")
        elif sky_exposure:
            o.append("            o.Emission = PorterFilm(max(%s, 0.0) * _SkyExposure);" % f3("EmissiveColor"))
        else:
            o.append("            o.Emission = max(%s, 0.0);" % f3("EmissiveColor"))
    else:
        o.append("            o.Albedo = saturate(%s);" % f3("BaseColor"))
        o.append("            o.Metallic = saturate(%s);" % f1("Metallic"))
        o.append("            o.Smoothness = 1.0 - saturate(%s);" % f1("Roughness"))
        o.append("            o.Occlusion = saturate(%s);" % f1("AmbientOcclusion"))
        o.append("            o.Emission = max(%s, 0.0);" % f3("EmissiveColor"))
        nrm = vals["Normal"]
        if not (nrm.const and nrm.e.replace(" ", "") == "float3(0.0,0.0,1.0)"):
            if tangent_space_normal:
                o.append("            float3 nUE = %s;" % tr.fit(nrm, 3).e)
            else:
                o.append("            float3 nWorldUE = %s;" % tr.fit(nrm, 3).e)
                o.append("            float3 nWorld = nWorldUE.xzy;")
                o.append("            float3 nTx = WorldNormalVector(IN, float3(1.0, 0.0, 0.0));")
                o.append("            float3 nTy = WorldNormalVector(IN, float3(0.0, 1.0, 0.0));")
                o.append("            float3 nTz = WorldNormalVector(IN, float3(0.0, 0.0, 1.0));")
                o.append("            float3 nUE = float3(dot(nWorld, nTx), -dot(nWorld, nTy), dot(nWorld, nTz));")
            o.append("            o.Normal = normalize(float3(nUE.x, lerp(nUE.y, -nUE.y, _FlipNormalY), max(nUE.z, 0.0001)));")
    if "tangent" in tr.uses and not any("o.Normal" in l for l in o):
        o.append("            o.Normal = float3(0.0, 0.0, 1.0);")
    if two and not unlit:
        o.append("            o.Normal.z *= IN.facing > 0.0 ? 1.0 : -1.0;")
    if bm == "BLEND_Additive":
        o.append("            o.Emission *= saturate(%s);" % f1("Opacity"))
    if bm == "BLEND_Masked":
        o.append("            o.Alpha = %s;" % f1("OpacityMask"))
    elif bm == "BLEND_Opaque":
        o.append("            o.Alpha = 1.0;")
    else:
        o.append("            o.Alpha = saturate(%s);" % f1("Opacity"))
    o.append("        }")
    o.append("        ENDCG")
    o.append("    }")
    o.append('    FallBack "Diffuse"')
    o.append("}")
    return "\n".join(o) + "\n", tr


ANNOTATE = False
APP_NAME = "FortniteUnityPorter"
DEFAULT_BRIDGE = "http://localhost:24320"
GENERIC_NAMES = {"uasset", "uexp", "ubulk", "umap", "json", "none", "null", ""}


def pick_name(candidates):
    for label, value in candidates:
        value = str(value or "").strip()
        if value.lower() not in GENERIC_NAMES:
            return ident(value), label
    return "material", "fallback"


DX10_RAW = {87: ("RGBA", "BGRA", 4), 91: ("RGBA", "BGRA", 4), 88: ("RGB", "BGRX", 4), 93: ("RGB", "BGRX", 4),
            28: ("RGBA", "RGBA", 4), 29: ("RGBA", "RGBA", 4), 61: ("L", "L", 1)}


def dds_raw_to_png(source, dest):
    from PIL import Image
    with open(source, "rb") as f:
        head = f.read(148)
        if head[:4] != b"DDS " or head[84:88] != b"DX10":
            raise ValueError("not a DX10 DDS")
        height, width = struct.unpack_from("<II", head, 12)
        fmt = struct.unpack_from("<I", head, 128)[0]
        if fmt not in DX10_RAW:
            raise NotImplementedError("DXGI format %d" % fmt)
        mode, raw, bpp = DX10_RAW[fmt]
        data = f.read(width * height * bpp)
    if len(data) < width * height * bpp:
        raise ValueError("DDS pixel data is truncated")
    Image.frombytes(mode, (width, height), data, "raw", raw).save(dest)


def dds_to_png(source, dest):
    try:
        from PIL import Image
        with Image.open(source) as im:
            im.load()
            if im.mode not in ("RGB", "RGBA", "L", "LA", "I;16"):
                im = im.convert("RGBA")
            im.save(dest)
        return None
    except Exception as e:
        first = "%s: %s" % (type(e).__name__, e)
    try:
        dds_raw_to_png(source, dest)
        return None
    except Exception:
        return first


def low_res_texture_warnings(tr, tex_info):
    out = []
    for k, v in tr.textures.items():
        info = tex_info.get(v["prop"]) or {}
        if str(info.get("Kind", "2d")).lower() != "2d":
            continue
        try:
            w = int(info.get("Width") or 0)
            h = int(info.get("Height") or 0)
            full = int(info.get("FullWidth") or 0)
        except (TypeError, ValueError):
            continue
        if w > 0 and full > w:
            out.append("texture %s was exported at %dx%d but the game's texture is %d px wide: its full-size data was not available (Fortnite streams it), so this is an approximation and it will look blurry. Turn on Pre-Download Streamed Assets for Fortnite in the Epic Games Launcher, let it finish, then convert again" % (os.path.basename(k).split(".")[0], w, h, full))
    return out


def export_textures(src, tr, folder):
    tex_dir = os.path.join(folder, "Textures")
    src.prefetch_textures([v["path"] for v in tr.textures.values()])
    os.makedirs(tex_dir, exist_ok=True)

    def one(k, v):
        name = os.path.basename(k).split(".")[0]
        info = src.texture_info(v["path"])
        file = (info or {}).get("File")
        if not file:
            why = src.why("texture", v["path"])
            return ("missing", "%s (%s)" % (name, why) if why else name)
        try:
            local = src.local(file)
            ext = os.path.splitext(local)[1] or os.path.splitext(file)[1]
            kept = None
            if ext.lower() == ".dds":
                if info.get("Hdr"):
                    why = "HDR texture"
                else:
                    why = dds_to_png(local, os.path.join(tex_dir, name + ".png"))
                    if why is None:
                        return ("copied", name + ".png", None)
                    if str(info.get("Kind", "2d")).lower() != "2d":
                        why = "not a plain 2D texture (%s): %s" % (info.get("Kind"), why)
                kept = "%s (%s)" % (name, why)
            shutil.copyfile(local, os.path.join(tex_dir, name + ext))
            return ("copied", name + ext, kept)
        except Exception as e:
            return ("missing", "%s (%s: %s)" % (name, type(e).__name__, e))

    items = list(tr.textures.items())
    if len(items) > 1:
        with ThreadPoolExecutor(max_workers=min(6, len(items))) as pool:
            results = list(pool.map(lambda kv: one(*kv), items))
    else:
        results = [one(k, v) for k, v in items]
    copied, missing, kept = [], [], []
    for r in results:
        if r[0] == "missing":
            missing.append(r[1])
        else:
            if r[2]:
                kept.append(r[2])
            copied.append(r[1])
    return copied, missing, kept


def convert(src, material=None, graph=None, entry_file=None, name=None, out=".", blend=None):
    entry = {}
    if entry_file:
        entry = json.load(open(entry_file, encoding="utf-8"))
    elif material:
        entry = src.material(material)
    gp = graph or (src.local(entry["graph"]) if entry.get("graph") else None)
    if not gp:
        raise SystemExit("no graph: give a graph file or a material path (with the app running)")
    graph0 = src.graph(gp, os.path.basename(gp))
    material_obj = graph0.material_object() if graph0 else None
    material_arg = (material or "").replace("\\", "/").split("/")[-1].split(".")[0]
    candidates = [
        ("--name", name),
        ("app entry name", entry.get("name")),
        ("material path", material_arg),
        ("graph material", (material_obj or {}).get("Name")),
        ("graph file", os.path.splitext(os.path.basename(gp))[0]),
    ]
    shader_name, source = pick_name(candidates)
    print("shader name: %s (from %s)" % (shader_name, source))
    print("  candidates: " + ", ".join("%s=%r" % (l, v) for l, v in candidates))
    text, tr = build(src, entry, shader_name, gp, blend=blend)
    try:
        src.prefetch_textures([v["path"] for v in tr.textures.values()])
    except Exception:
        pass
    if "particle" not in tr.uses:
        text, ac_used = add_ao_cavity_editor(text, len(tr.textures))
        if ac_used:
            tr.notes.append("Ao and Cavity sliders added (Ao scales ambient occlusion by the red channel, Cavity darkens or lightens the base color with the green channel of %s; both default to 0, so the look is unchanged)" % ac_used)
        text, ms_added = add_metal_smooth_editor(text)
        if ms_added:
            tr.notes.append("%s multiplier/offset sliders added; the original Metallic/Smoothness/Roughness parameters are hidden so these are the controls (neutral defaults, the look is unchanged)" % ms_added)
    text, n_guard = guard_divisions(text)
    if n_guard:
        tr.notes.append("%d division(s) guarded with FPU_NZ() so a zero divisor cannot cause a division-by-zero warning or NaN" % n_guard)
    if len(tr.textures) > SAMPLER_BUDGET:
        nearest = set()
        for v in tr.textures.values():
            try:
                if (src.texture_info(v["path"]) or {}).get("Nearest"):
                    nearest.add(v["prop"])
            except Exception:
                pass
        text, n_shared = share_samplers(text, nearest)
        if n_shared:
            tr.notes.append("%d textures: %d of them now share the sampler of one host texture (UNITY_DECLARE_TEX2D_NOSAMPLER) because Unity allows only 16 sampler registers per shader and its lighting needs some; wrap and filtering come from the host texture, which the texture setup script imports as Repeat and Trilinear (Point for pixel-art textures)" % (len(tr.textures), n_shared))
            print("  %d textures: shared samplers used (Unity's 16-sampler limit)" % len(tr.textures))
    for w in lint_shader(text):
        tr.warn(w)
    if not (entry.get("scalars") or entry.get("vectors") or entry.get("textures")) and not re.match(r"^MI_|_Inst(_?\d+)?$", shader_name):
        tr.notes.append("%s is a base material, so every parameter is the graph's own default, which is what the game uses" % shader_name)
    elif not (entry.get("scalars") or entry.get("vectors") or entry.get("textures")):
        tr.warn("no material-instance data was loaded (entry has no scalars, vectors or textures): every parameter is the parent graph's default, so an MI_ / _Inst material will look different. Convert with --material while the app is running, or pass --entry")
    elif tr.override_hits == 0:
        tr.warn("the material-instance data matched none of the graph's parameters (names differ?)")
    else:
        print("%d parameter values taken from the material instance" % tr.override_hits)
        unused = sorted(k for k in tr.entry.get("scalars", {}) if ("scalar", str(k).lower()) not in tr.used_values) + \
            sorted(k for k in tr.entry.get("vectors", {}) if ("vector", str(k).lower()) not in tr.used_values)
        if unused:
            tr.notes.append("%d instance value(s) matched no parameter in the converted graph and were not used: %s" % (len(unused), ", ".join(unused[:25]) + (" ..." if len(unused) > 25 else "")))
        seen_tex = {str(r["param"]).lower() for r in tr.tex_trace if r.get("param")}
        lost_tex = sorted(k for k in tr.entry.get("textures", {}) if str(k).lower() not in seen_tex)
        if lost_tex:
            tr.warn("%d instance texture(s) matched no texture node that the converted graph reaches and were not used: %s (the node sits in a branch a static switch turned off, or the parameter name differs)" % (len(lost_tex), ", ".join(lost_tex[:25]) + (" ..." if len(lost_tex) > 25 else "")))
            print("instance values not used by any parameter: " + ", ".join(unused[:25]))
    folder_shown = os.path.abspath(os.path.join(out, shader_name))
    folder = long_path(folder_shown)
    deepest = len(folder_shown) + len(os.sep) + len(shader_name) + len(".textures.json")
    if deepest > WINDOWS_PATH_LIMIT:
        tr.warn("the shader's file path is %d characters, over Windows' %d-character limit: it was written anyway, but Unity may refuse to import it. Convert into a shorter output folder to avoid this" % (deepest, WINDOWS_PATH_LIMIT))
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, shader_name + ".shader"), "w", encoding="utf-8") as fh:
        fh.write(text)
    if "objradius" in tr.uses:
        bounds_path = os.path.join(out, "PorterObjectBounds.cs")
        bounds_cs = BOUNDS_CS.replace("__FLIP__", str(bool(LOCAL_X_FLIP))).replace("__XMIN__", "-mb.max.x" if LOCAL_X_FLIP else "mb.min.x").replace("__XMAX__", "-mb.min.x" if LOCAL_X_FLIP else "mb.max.x")
        if not os.path.exists(bounds_path) or "// x-flip: %s" % bool(LOCAL_X_FLIP) not in open(bounds_path, encoding="utf-8").read():
            open(bounds_path, "w", encoding="utf-8").write(bounds_cs)
    # Colors are real Color properties now, so no picker script is written. Remove copies left by earlier versions.
    for old_color in (os.path.join(folder, "Editor", "PorterColorDrawer.cs"), os.path.join(out, "Editor", "PorterColorDrawer.cs")):
        for f in (old_color, old_color + ".meta"):
            if os.path.exists(f):
                try:
                    os.remove(f)
                except OSError:
                    pass
        try:
            os.rmdir(os.path.dirname(old_color))
        except OSError:
            pass
    importer_path = os.path.join(out, "Editor", "PorterTextureSetup.cs")
    if not os.path.exists(importer_path):
        os.makedirs(os.path.dirname(importer_path), exist_ok=True)
        open(importer_path, "w", encoding="utf-8").write(IMPORTER_CS)
    tex_info = {}
    for v in tr.textures.values():
        info = src.texture_info(v["path"])
        if info:
            tex_info[v["prop"]] = {k: info.get(k) for k in ("Srgb", "Kind", "Width", "Height", "Depth", "Hdr", "Nearest", "FullWidth") if k in info}
    tex_list = []
    for k, v in tr.textures.items():
        info = tex_info.get(v["prop"], {})
        sr = info.get("Srgb")
        if isinstance(sr, str):
            sr = sr.strip().lower() in ("true", "1", "yes")
        tex_list.append({"name": os.path.basename(k).split(".")[0], "prop": v["prop"],
                         "srgb": -1 if sr is None else int(bool(sr)), "normal": bool(v.get("normal")),
                         "width": int(info.get("Width") or 0), "height": int(info.get("Height") or 0),
                         "nearest": bool(info.get("Nearest")),
                         "stack": bool(v.get("sheet")) or str(info.get("Kind", "2d")).lower() != "2d"})
    copied, missing, kept = export_textures(src, tr, folder)
    for w in low_res_texture_warnings(tr, tex_info):
        tr.warn(w)
        print("  low-res texture: %s" % w)
    if kept:
        tr.warn("textures left as DDS: %s" % "; ".join(kept))
        for k in kept:
            print("  left as DDS: %s" % k)
    if missing:
        tr.warn("texture files not exported (the app gave no file for them): %s" % ", ".join(missing))
    tex_lines, tex_warns = tr.texture_report()
    for w in tex_warns:
        tr.warn(w)
    print("textures (%d slots):" % len(tex_lines))
    for l in tex_lines:
        print(l)
    manifest = {"shader": shader_name, "textureSources": tr.tex_trace, "textures": {v["prop"]: v["path"] for v in tr.textures.values()},
                "textureInfo": tex_info, "textureList": tex_list, "params": tr.props, "warnings": tr.warnings, "notes": tr.notes}
    with open(os.path.join(folder, shader_name + ".textures.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
    print("wrote %s.shader (%d textures, %d warnings)" % (shader_name, len(tr.textures), len(tr.warnings)))
    print("folder: %s" % folder_shown)
    print("textures copied to Textures: %d of %d" % (len(copied), len(tr.textures)))
    for w in tr.warnings:
        print("  warning:", w)
    for n in tr.notes:
        print("  note:", n)
    src.file_away(shader_name)
    return shader_name, tr


BOUNDS_CS = r'''// x-flip: __FLIP__
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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
        block.SetVector(BoundsMinId, new Vector4(__XMIN__ * 100.0f, mb.min.z * 100.0f, mb.min.y * 100.0f, 0.0f));
        block.SetVector(BoundsMaxId, new Vector4(__XMAX__ * 100.0f, mb.max.z * 100.0f, mb.max.y * 100.0f, 0.0f));
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

#if UNITY_EDITOR
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
#endif
}
'''


IMPORTER_CS = r'''using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class PorterTextureSetup : EditorWindow
{
    [System.Serializable]
    class Entry
    {
        public string name;
        public string prop;
        public int srgb;
        public bool normal;
        public int width;
        public int height;
        public bool nearest;
        public bool stack;
    }

    [System.Serializable]
    class Manifest
    {
        public string shader;
        public List<Entry> textureList;
    }

    TextAsset manifestAsset;
    Material target;

    [MenuItem("Tools/Porter Texture Setup")]
    static void Open()
    {
        GetWindow<PorterTextureSetup>("Porter Texture Setup");
    }

    void OnGUI()
    {
        manifestAsset = (TextAsset)EditorGUILayout.ObjectField("Textures JSON", manifestAsset, typeof(TextAsset), false);
        target = (Material)EditorGUILayout.ObjectField("Material", target, typeof(Material), false);
        if (GUILayout.Button("Apply") && manifestAsset != null)
        {
            Apply();
        }
    }

    void Apply()
    {
        Manifest m = JsonUtility.FromJson<Manifest>(manifestAsset.text);
        foreach (Entry e in m.textureList)
        {
            string path = null;
            foreach (string guid in AssetDatabase.FindAssets(e.name + " t:Texture2D"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) == e.name)
                {
                    path = p;
                    break;
                }
            }
            if (path == null)
            {
                Debug.LogWarning("Porter: texture not found in project: " + e.name);
                continue;
            }
            TextureImporter imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = e.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (!e.normal && e.srgb >= 0)
            {
                imp.sRGBTexture = e.srgb == 1;
            }
            imp.mipmapEnabled = !e.stack;
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.filterMode = e.nearest ? FilterMode.Point : FilterMode.Trilinear;
            imp.anisoLevel = 8;
            imp.crunchedCompression = false;
            imp.textureCompression = (long)e.width * e.height <= 512L * 512L
                ? TextureImporterCompression.Uncompressed
                : TextureImporterCompression.CompressedHQ;
            int side = e.stack ? 16384 : Mathf.Max(e.width, e.height);
            if (side > 0)
            {
                imp.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(side), 32, 16384);
            }
            imp.SaveAndReimport();
            if (target != null)
            {
                target.SetTexture(e.prop, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
            }
        }
        if (target != null)
        {
            EditorUtility.SetDirty(target);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("Porter: texture setup finished for " + m.shader);
    }
}

public class PorterTextureAuto : AssetPostprocessor
{
    [System.Serializable]
    class E
    {
        public string name;
        public int srgb;
        public bool normal;
        public int width;
        public int height;
        public bool nearest;
        public bool stack;
    }

    [System.Serializable]
    class M
    {
        public List<E> textureList;
    }

    static Dictionary<string, E> map;
    static double loadedAt = -100.0;
    static bool warnedColorSpace;

    static void Load()
    {
        map = new Dictionary<string, E>();
        loadedAt = EditorApplication.timeSinceStartup;
        foreach (string guid in AssetDatabase.FindAssets("textures t:TextAsset"))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (!p.EndsWith("textures.json"))
            {
                continue;
            }
            M m = null;
            try
            {
                m = JsonUtility.FromJson<M>(File.ReadAllText(p));
            }
            catch (System.Exception)
            {
            }
            if (m == null || m.textureList == null)
            {
                continue;
            }
            foreach (E e in m.textureList)
            {
                if (!string.IsNullOrEmpty(e.name))
                {
                    map[e.name] = e;
                }
            }
        }
    }

    static E Find(string name)
    {
        if (map == null || (!map.ContainsKey(name) && EditorApplication.timeSinceStartup - loadedAt > 3.0))
        {
            Load();
        }
        E e;
        return map.TryGetValue(name, out e) ? e : null;
    }

    static bool Apply(TextureImporter imp, E e)
    {
        bool changed = false;
        TextureImporterType type = e.normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (imp.textureType != type)
        {
            imp.textureType = type;
            changed = true;
        }
        if (!e.normal && e.srgb >= 0 && imp.sRGBTexture != (e.srgb == 1))
        {
            imp.sRGBTexture = e.srgb == 1;
            changed = true;
        }
        if (imp.mipmapEnabled == e.stack)
        {
            imp.mipmapEnabled = !e.stack;
            changed = true;
        }
        if (imp.wrapMode != TextureWrapMode.Repeat)
        {
            imp.wrapMode = TextureWrapMode.Repeat;
            changed = true;
        }
        FilterMode wantFilter = e.nearest ? FilterMode.Point : FilterMode.Trilinear;
        if (imp.filterMode != wantFilter)
        {
            imp.filterMode = wantFilter;
            changed = true;
        }
        if (imp.anisoLevel != 8)
        {
            imp.anisoLevel = 8;
            changed = true;
        }
        if (imp.crunchedCompression)
        {
            imp.crunchedCompression = false;
            changed = true;
        }
        TextureImporterCompression comp = (long)e.width * e.height <= 512L * 512L
            ? TextureImporterCompression.Uncompressed
            : TextureImporterCompression.CompressedHQ;
        if (imp.textureCompression != comp)
        {
            imp.textureCompression = comp;
            changed = true;
        }
        int side = e.stack ? 16384 : Mathf.Max(e.width, e.height);
        if (side > 0)
        {
            int max = Mathf.Clamp(Mathf.NextPowerOfTwo(side), 32, 16384);
            if (imp.maxTextureSize != max)
            {
                imp.maxTextureSize = max;
                changed = true;
            }
        }
        return changed;
    }

    static void WarnColorSpace()
    {
        if (warnedColorSpace || PlayerSettings.colorSpace == ColorSpace.Linear)
        {
            return;
        }
        warnedColorSpace = true;
        Debug.LogWarning("Porter: this project uses Gamma color space. Ported Unreal shaders are written for Linear (Edit > Project Settings > Player > Other Settings > Color Space); their colors and textures will look wrong in Gamma.");
    }

    void OnPreprocessTexture()
    {
        E e = Find(Path.GetFileNameWithoutExtension(assetPath));
        if (e == null)
        {
            return;
        }
        Apply((TextureImporter)assetImporter, e);
        WarnColorSpace();
    }

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string p in imported)
        {
            if (p.EndsWith("textures.json"))
            {
                map = null;
                EditorApplication.delayCall += ApplyToProject;
                return;
            }
        }
    }

    [MenuItem("Tools/Porter Apply Texture Settings")]
    static void ApplyToProject()
    {
        Load();
        WarnColorSpace();
        int count = 0;
        foreach (KeyValuePair<string, E> kv in map)
        {
            foreach (string guid in AssetDatabase.FindAssets(kv.Key + " t:Texture2D"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(p) != kv.Key)
                {
                    continue;
                }
                TextureImporter imp = AssetImporter.GetAtPath(p) as TextureImporter;
                if (imp != null && Apply(imp, kv.Value))
                {
                    imp.SaveAndReimport();
                    count++;
                }
            }
        }
        if (count > 0)
        {
            Debug.Log("Porter: fixed import settings on " + count + " texture(s)");
        }
    }
}
'''


def lint_shader(text):
    t = text.replace("\r\n", "\n")
    if "CGPROGRAM" not in t:
        return []
    code = re.sub(r"//[^\n]*", "", t[t.index("CGPROGRAM"):])
    out, widths = [], {}
    for m in re.finditer(r"\bfloat([234]?)\s+(v\d+)\s*=", code):
        if m.group(2) in widths:
            out.append("lint: %s is declared twice" % m.group(2))
        widths[m.group(2)] = int(m.group(1) or 1)
    for n in sorted(set(re.findall(r"\b(v\d+)\b", code)) - set(widths)):
        out.append("lint: %s is used but never declared" % n)
    for m in re.finditer(r"\b(v\d+)\.([xyzwrgba]{1,4})\b", code):
        w = widths.get(m.group(1))
        if w and not set(m.group(2)) <= set("xyzw"[:w]) | set("rgba"[:w]):
            out.append("lint: invalid swizzle %s.%s on a float%s value" % (m.group(1), m.group(2), "" if w == 1 else w))
    for m in re.finditer(r"\bfloat([234])\s+(v\d+)\s*=\s*(v\d+)\s*;", code):
        src = widths.get(m.group(3))
        if src and 1 < src < int(m.group(1)):
            out.append("lint: %s is float%s but is assigned from float%s %s" % (m.group(2), m.group(1), src, m.group(3)))
    props = t[:t.index("SubShader")] if "SubShader" in t else ""
    tail = re.search(r"((?:^[ \t]*\[[^\]\n]*\][ \t]*\n)+)[ \t]*\}[ \t]*\n[ \t]*SubShader", t, re.M)
    if tail:
        out.append("lint: the Properties block ends with an attribute that has no property after it (%s); Unity fails with \"Parse error: syntax error, unexpected '}'\"" % ", ".join(x.strip() for x in tail.group(1).strip().split("\n")))
    left = sorted(set(re.findall(r"\b(?:BRANCH|LOOP|UNROLL|FLATTEN|Texture2DSample\w*|GetMaterialSharedSampler|ProcessMaterial\w*|UnpackNormalMap|View_\w+|ResolvedView)\b", code)))
    if left:
        out.append("lint: Unreal-only code is still in the shader (%s); Unity cannot compile it" % ", ".join(left))
    for n in sorted(set(re.findall(r"^\s*(?:sampler2D(?:_float)?|Texture2D)\s+(_T_\w+)\s*;", code, re.M)) | set(re.findall(r"^\s*UNITY_DECLARE_TEX2D(?:_NOSAMPLER)?\(\s*(_T_\w+)\s*\)\s*;", code, re.M))):
        if len(re.findall(r"\b%s\b" % re.escape(n), code)) <= 1:
            out.append("lint: texture %s is exported but the shader never samples it" % n)
    declared = set(re.findall(r"^\s*(?:float\d?|sampler2D|Texture2D)\s+(_\w+)\s*;", code, re.M)) | set(re.findall(r"^\s*UNITY_DECLARE_TEX2D(?:_NOSAMPLER)?\(\s*(_\w+)\s*\)\s*;", code, re.M))
    for n in sorted(set(re.findall(r"^\s*(?:\[[^\]]*\]\s*)*(_\w+)\s*\(", props, re.M)) - declared):
        if n != "_Cutoff":
            out.append("lint: property %s is not declared in the CG block" % n)
    return list(dict.fromkeys(out))[:25]

SAMPLER_BUDGET = 8

SHARED_PLATFORMS = "defined(UNITY_SEPARATE_TEXTURE_SAMPLER)"
LEGACY_SHARED_PLATFORMS = "defined(SHADER_API_D3D11) || defined(SHADER_API_XBOXONE) || defined(SHADER_API_METAL) || defined(SHADER_API_VULKAN)"


def shared_sampler_macros(host_t, host_p):
    """Sampling macros for textures that share the sampler of one host texture (the form Unity's own lighting code uses:
    UNITY_DECLARE_TEX2D for the host, UNITY_DECLARE_TEX2D_NOSAMPLER for the rest). Has to come after the Unity includes,
    because the platform defines are only known from there on."""
    st, sp = "sampler" + host_t, "sampler" + host_p
    return "\n".join([
        "        #define FPU_TEX2D(t, uv) UNITY_SAMPLE_TEX2D_SAMPLER(t, %s, uv)" % host_t,
        "        #define FPU_TEX2DP(t, uv) UNITY_SAMPLE_TEX2D_SAMPLER(t, %s, uv)" % host_p,
        "        #if " + SHARED_PLATFORMS,
        "            #define FPU_TEX2DLOD(t, c) t.SampleLevel(%s, (c).xy, (c).w)" % st,
        "            #define FPU_TEX2DGRAD(t, uv, gx, gy) t.SampleGrad(%s, uv, gx, gy)" % st,
        "            #define FPU_TEX2DPLOD(t, c) t.SampleLevel(%s, (c).xy, (c).w)" % sp,
        "            #define FPU_TEX2DPGRAD(t, uv, gx, gy) t.SampleGrad(%s, uv, gx, gy)" % sp,
        "        #else",
        "            #define FPU_TEX2DLOD(t, c) tex2Dlod(t, c)",
        "            #define FPU_TEX2DGRAD(t, uv, gx, gy) tex2Dgrad(t, uv, gx, gy)",
        "            #define FPU_TEX2DPLOD(t, c) tex2Dlod(t, c)",
        "            #define FPU_TEX2DPGRAD(t, uv, gx, gy) tex2Dgrad(t, uv, gx, gy)",
        "        #endif",
    ]) + "\n"


OLD_SHARED_SAMPLER_BLOCK = re.compile(r"^[ \t]*//[^\n]*Shared samplers[^\n]*\n(?:[ \t]*//[^\n]*\n)*[ \t]*SamplerState sampler_TrilinearRepeat;[^\n]*\n[ \t]*SamplerState sampler_PointRepeat;[^\n]*\n(?:[ \t]*#define FPU_TEX2D\w*\([^\n]*\n)+", re.M)
OLD_SHARED_SAMPLER_GUARDED = re.compile(r"^[ \t]*#if defined\(SHADER_TARGET_SURFACE_ANALYSIS\)\n(?:(?![ \t]*#endif)[^\n]*\n)*?[ \t]*SamplerState sampler_TrilinearRepeat;[^\n]*\n(?:(?![ \t]*#endif)[^\n]*\n)*[ \t]*#endif[^\n]*\n", re.M)
INCLUDE_LIGHTING = re.compile(r"^[ \t]*#include[ \t]+\"Lighting\.cginc\"[^\n]*\n", re.M)
INCLUDE_UNITYCG = re.compile(r"^[ \t]*#include[ \t]+\"UnityCG\.cginc\"[^\n]*\n", re.M)


def insert_shared_macros(t, host_t, host_p):
    """Puts the sampling macros right after the last Unity include in the CG block (t uses plain \\n line ends)."""
    macros = shared_sampler_macros(host_t, host_p)
    pos = None
    for rx in (INCLUDE_LIGHTING, INCLUDE_UNITYCG):
        m = rx.search(t)
        if m:
            pos = m.end()
            break
    if pos is None:
        m = re.search(r"^[ \t]*#pragma[ \t]+target[^\n]*\n", t, re.M)
        if not m:
            return t, False
        t = t[:m.end()] + '        #include "UnityCG.cginc"\n' + t[m.end():]
        pos = m.end() + len('        #include "UnityCG.cginc"\n')
    return t[:pos] + macros + t[pos:], True


def pick_sampler_hosts(trilinear, point):
    trilinear, point = list(trilinear), list(point)
    host_t = trilinear[0] if trilinear else (point[0] if point else None)
    host_p = point[0] if point else host_t
    return host_t, host_p


def declare_shared_textures(t, names, host_t, host_p):
    """sampler2D / Texture2D name;  ->  UNITY_DECLARE_TEX2D(name);  (the two hosts)  or  UNITY_DECLARE_TEX2D_NOSAMPLER(name);"""
    hosts = {host_t, host_p}
    for n in names:
        macro = "UNITY_DECLARE_TEX2D" if n in hosts else "UNITY_DECLARE_TEX2D_NOSAMPLER"
        t = re.sub(r"^([ \t]*)(?:sampler2D|Texture2D)([ \t]+)%s[ \t]*;" % re.escape(n),
                   lambda m: "%s%s(%s);" % (m.group(1), macro, n), t, count=1, flags=re.M)
    return t


HOST_DEFINE = re.compile(r"^[ \t]*#define[ \t]+FPU_TEX2DP?\([ \t]*t[ \t]*,[ \t]*uv[ \t]*\)[ \t]+UNITY_SAMPLE_TEX2D_SAMPLER\([ \t]*t[ \t]*,[ \t]*(_\w+)[ \t]*,[ \t]*uv[ \t]*\)", re.M)

HOST_TOUCH_FUNC = (
    "        // Unity pairs a sampler with the texture of the same name in every compiled program (vertex, pixel, shadow...) and fails with\n"
    "        // \"Unrecognized sampler ... does not match any texture\" when the host texture is not used in that program. The vertex stage only\n"
    "        // samples with .SampleLevel, so the host would be missing there. This keeps it in use; _FPU_Zero is 0, the image does not change.\n"
    "        float _FPU_Zero;\n"
    "        float FPU_HostTouch()\n"
    "        {\n"
    "        #if defined(UNITY_SEPARATE_TEXTURE_SAMPLER)\n"
    "%s"
    "            return (%s) * _FPU_Zero;\n"
    "        #else\n"
    "            return 0.0;\n"
    "        #endif\n"
    "        }\n")


def _inject_before_close(t, signature, stmt):
    span = chk_body(chk_strip(t), signature)
    if not span:
        return t, False
    at = t.rfind("\n", 0, span[1]) + 1
    return t[:at] + stmt + t[at:], True


def add_host_touch(text):
    """Gives the shared-sampler host texture(s) a real (zero-valued) use in vert() and surf(), so Unity does not drop the host in one stage
    while the sampler is still used there. Returns (new_text, changed). Does nothing for shaders without a host or that already have it."""
    if "FPU_HostTouch" in text:
        return text, False
    hosts = list(dict.fromkeys(m.group(1) for m in HOST_DEFINE.finditer(text)))
    if not hosts:
        return text, False
    crlf = "\r\n" in text
    t = text.replace("\r\n", "\n")
    pos = -1
    for h in hosts:
        m = re.search(r"^[ \t]*UNITY_DECLARE_TEX2D\([ \t]*%s[ \t]*\)[ \t]*;[^\n]*\n" % re.escape(h), t, re.M)
        if not m:
            return text, False
        pos = max(pos, m.end())
    dims = "".join("            float w%d, h%d;\n            %s.GetDimensions(w%d, h%d);\n" % (i, i, h, i, i) for i, h in enumerate(hosts))
    total = " + ".join("w%d + h%d" % (i, i) for i in range(len(hosts)))
    t = t[:pos] + HOST_TOUCH_FUNC % (dims, total) + t[pos:]
    t, ok_v = _inject_before_close(t, "void vert(", "            v.vertex.xyz += FPU_HostTouch();\n")
    t, ok_s = _inject_before_close(t, "void surf(", "            float fpuTouch = FPU_HostTouch();\n            o.Albedo += fpuTouch;\n            o.Emission += fpuTouch;\n            o.Alpha += fpuTouch;\n")
    if not (ok_v or ok_s):
        return text, False
    return (t.replace("\n", "\r\n") if crlf else t), True


def _split_top_args(s):
    args, depth, cur = [], 0, ""
    for ch in s:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            args.append(cur)
            cur = ""
        else:
            cur += ch
    args.append(cur)
    return args


def fix_equal_smoothstep(text):
    """smoothstep(a, a, x) divides by zero (UE gives the same 0/1 jump a step() does). Returns (new_text, number_rewritten)."""
    pat = re.compile(r"(?<![\w.])smoothstep\(")
    out, i, n = [], 0, 0
    while True:
        m = pat.search(text, i)
        if not m:
            out.append(text[i:])
            break
        depth, j = 1, m.end()
        while j < len(text) and depth:
            depth += (text[j] == "(") - (text[j] == ")")
            j += 1
        if depth:
            out.append(text[i:])
            break
        args = _split_top_args(text[m.end():j - 1])
        if len(args) == 3 and args[0].strip() and args[0].strip() == args[1].strip():
            x, k = fix_equal_smoothstep(args[2].strip())
            out.append(text[i:m.start()] + "step(%s, %s)" % (args[0].strip(), x))
            n += 1 + k
            i = j
        else:
            out.append(text[i:m.end()])
            i = m.end()
    return "".join(out), n


def upgrade_shared_sampler_block(text):
    """Converts a Beta21-Beta23 shared-sampler shader (free-standing SamplerState names) to the form that gets Unity below its
    16-sampler limit: one host texture keeps its sampler, every other texture is declared without one and samples through it."""
    if LEGACY_SHARED_PLATFORMS in text and "FPU_TEX2DLOD" in text:
        return text.replace(LEGACY_SHARED_PLATFORMS, SHARED_PLATFORMS), True
    if "SamplerState sampler_TrilinearRepeat" not in text:
        return text, False
    crlf = "\r\n" in text
    t = text.replace("\r\n", "\n")
    t, n1 = OLD_SHARED_SAMPLER_GUARDED.subn("", t, count=1)
    if not n1:
        t, n1 = OLD_SHARED_SAMPLER_BLOCK.subn("", t, count=1)
    if not n1:
        return text, False
    names = re.findall(r"^[ \t]*Texture2D[ \t]+(_\w+)[ \t]*;", t, re.M)
    point = []
    for m in re.finditer(r"FPU_TEX2DP(?:LOD|GRAD)?[ \t]*\([ \t]*(_\w+)", t):
        if m.group(1) in names and m.group(1) not in point:
            point.append(m.group(1))
    trilinear = [n for n in names if n not in point]
    host_t, host_p = pick_sampler_hosts(trilinear, point)
    if host_t is None:
        return text, False
    t = declare_shared_textures(t, names, host_t, host_p)
    t, ok = insert_shared_macros(t, host_t, host_p)
    if not ok:
        return text, False
    t, _ = add_host_touch(t)
    return (t.replace("\n", "\r\n") if crlf else t), True


MS_EDITOR_PROPS = (
    '        [Header(Metallic and Smoothness)]\n'
    '        _MetallicMultiplier ("Metallic Multiplier", Range(0, 2)) = 1.0\n'
    '        _MetallicOffset ("Metallic Offset", Range(-1, 1)) = 0.0\n'
    '        _SmoothnessMultiplier ("Smoothness Multiplier", Range(0, 2)) = 1.0\n'
    '        _SmoothnessOffset ("Smoothness Offset", Range(-1, 1)) = 0.0\n'
    '        [Space(6)]\n')
MS_EDITOR_PROPS_M = (
    '        [Header(Metallic)]\n'
    '        _MetallicMultiplier ("Metallic Multiplier", Range(0, 2)) = 1.0\n'
    '        _MetallicOffset ("Metallic Offset", Range(-1, 1)) = 0.0\n'
    '        [Space(6)]\n')
MS_EDITOR_PROPS_S = (
    '        [Header(Smoothness)]\n'
    '        _SmoothnessMultiplier ("Smoothness Multiplier", Range(0, 2)) = 1.0\n'
    '        _SmoothnessOffset ("Smoothness Offset", Range(-1, 1)) = 0.0\n'
    '        [Space(6)]\n')
MS_METALLIC = re.compile(r"^([ \t]*)o\.Metallic = saturate\((.*)\);[ \t]*$", re.M)
MS_SMOOTH = re.compile(r"^([ \t]*)o\.Smoothness = 1\.0 - saturate\((.*)\);[ \t]*$", re.M)


def ms_driving_props(t, expr, words):
    """Scalar parameters (Float/Range) in the expression's dependency chain whose name looks like words (metal, rough...)."""
    props = set(re.findall(r'^[ \t]*(?:\[[^\]]*\][ \t]*)*(_\w+)[ \t]*\("[^"]*",[ \t]*(?:Float|Range)', t, re.M))
    defs = {}
    for m in re.finditer(r"^[ \t]*\w+[ \t]+(v\d+)[ \t]*=[ \t]*(.*);[ \t]*$", t, re.M):
        defs.setdefault(m.group(1), m.group(2))
    seen = set()
    found = []
    todo = [expr]
    while todo:
        for name in re.findall(r"[A-Za-z_]\w*", todo.pop()):
            if name in props and re.search(words, name, re.I) and name not in found:
                found.append(name)
            if name in defs and name not in seen:
                seen.add(name)
                todo.append(defs[name])
    return found


def hide_property(t, name):
    """Hides one Properties entry from the inspector (its value is kept, so the look is unchanged). A [Header] line in front of it
    is moved behind it so the group title still shows on the next property."""
    pm = re.search(r"^[ \t]*Properties[ \t]*\n[ \t]*\{\n", t, re.M)
    if not pm:
        return t
    m = re.compile(r"^([ \t]*)((?:\[[^\]\n]*\][ \t]*)*)(%s[ \t]*\(.*)$" % re.escape(name), re.M).search(t, pm.end())
    if not m or "HideInInspector" in m.group(2):
        return t
    line = "%s[HideInInspector] %s%s" % (m.group(1), m.group(2), m.group(3))
    pre = re.search(r"^[ \t]*\[Header\([^\n]*\)\][ \t]*\n\Z", t[:m.start()], re.M)
    if pre:
        hdr = pre.group(0)
        return t[:pre.start()] + line + "\n" + hdr + t[m.end() + 1:]
    return t[:m.start()] + line + t[m.end():]


def add_metal_smooth_editor(text):
    """Adds Metallic / Smoothness multiplier and offset sliders (neutral defaults, so the look is unchanged) to every lit surface shader.
    The original parameters that drove Metallic or Smoothness (Metallic, Roughness, ...) are hidden from the inspector, so these sliders
    are the one place to control them. Returns (new_text, what_was_added or False). Not used for particle shaders."""
    if "_SmoothnessMultiplier" in text or "_MetallicMultiplier" in text or "SurfaceOutputStandard" not in text:
        return text, False
    crlf = "\r\n" in text
    t = text.replace("\r\n", "\n")
    mm, ms = MS_METALLIC.search(t), MS_SMOOTH.search(t)
    if not mm or not ms:
        return text, False
    want_m = want_s = True
    old = ms_driving_props(t, mm.group(2), r"metal") + [n for n in ms_driving_props(t, ms.group(2), r"smooth|rough|gloss")]
    if want_m:
        t = MS_METALLIC.sub(lambda m: "%so.Metallic = saturate((%s) * _MetallicMultiplier + _MetallicOffset);" % (m.group(1), m.group(2)), t, count=1)
    if want_s:
        t = MS_SMOOTH.sub(lambda m: "%so.Smoothness = saturate((1.0 - saturate(%s)) * _SmoothnessMultiplier + _SmoothnessOffset);" % (m.group(1), m.group(2)), t, count=1)
    m = re.search(r"^[ \t]*Properties[ \t]*\n[ \t]*\{\n", t, re.M)
    if not m:
        return text, False
    block = MS_EDITOR_PROPS
    t = t[:m.end()] + block + t[m.end():]
    for n in old:
        t = hide_property(t, n)
    decl = ""
    if want_m:
        decl += "        float _MetallicMultiplier;\n        float _MetallicOffset;\n"
    if want_s:
        decl += "        float _SmoothnessMultiplier;\n        float _SmoothnessOffset;\n"
    m = re.search(r"^[ \t]*float[ \t]+_FlipNormalY;[^\n]*\n", t, re.M) or re.search(r"^[ \t]*struct[ \t]+Input", t, re.M)
    if not m:
        return text, False
    pos = m.end() if m.group(0).rstrip().endswith(";") else m.start()
    t = t[:pos] + decl + t[pos:]
    added = "Metallic and Smoothness" if want_m and want_s else ("Metallic" if want_m else "Smoothness")
    return (t.replace("\n", "\r\n") if crlf else t), added


AC_ALBEDO = re.compile(r"^([ \t]*)o\.Albedo = saturate\((.*)\);[ \t]*$", re.M)
AC_OCCLUSION = re.compile(r"^([ \t]*)o\.Occlusion = saturate\((.*)\);[ \t]*$", re.M)
AC_MASK_RE = re.compile(r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(_\w*_M)[ \t]*\(\"[^\"\n]*\"[ \t]*,[ \t]*2D\b", re.M)


def add_ao_cavity_editor(text, texture_count=0):
    """Adds Ao and Cavity sliders (Fortnite's own detail shading) to a lit surface shader. Ao scales ambient occlusion by the red channel
    of a mask texture, Cavity overlay-blends its green channel onto the base color. The mask is the shader's own _M texture when it has
    one (Fortnite's character mask), otherwise a new white slot. Both sliders default to 0, so the look is unchanged until they are raised.
    Returns (new_text, what_was_used or False). Not used for particle or unlit shaders."""
    if "_Cavity" in text or "SurfaceOutputStandard" not in text or "float4 tc01;" not in text:
        return text, False
    crlf = "\r\n" in text
    t = text.replace("\r\n", "\n")
    am, om = AC_ALBEDO.search(t), AC_OCCLUSION.search(t)
    if not am or not om:
        return text, False
    pm = re.search(r"^[ \t]*Properties[ \t]*\n[ \t]*\{\n", t, re.M)
    if not pm:
        return text, False
    mask, fn = None, "tex2D"
    pend = chk_body(chk_strip(t), "Properties")
    if pend:
        for mm in AC_MASK_RE.finditer(t[pend[0]:pend[1]]):
            use = re.search(r"\b((?:FPU_)?[tT][eE][xX]2[dD])\(%s\b" % re.escape(mm.group(1)), t)
            if use:
                mask, fn = mm.group(1), use.group(1)
                break
    own = mask is None
    if own:
        return text, False   # no _M mask texture in this shader: Ao and Cavity would have nothing to read, so they are not added
    sample = "%s(%s, IN.tc01.xy)" % (fn, mask)
    t = AC_OCCLUSION.sub(lambda m: "%so.Occlusion = saturate(%s) * lerp(1.0, %s.r, _Ao);" % (m.group(1), m.group(2), sample), t, count=1)
    def albedo(m):
        i = m.group(1)
        return ("%so.Albedo = saturate(%s);\n"
                "%sfloat3 fpuCavD = o.Albedo;\n"
                "%sfloat fpuCavS = %s.g;\n"
                "%so.Albedo = saturate(lerp(fpuCavD, lerp(2.0 * fpuCavD * fpuCavS, 1.0 - 2.0 * (1.0 - fpuCavD) * (1.0 - fpuCavS), step(0.5, fpuCavD)), _Cavity));"
                % (i, m.group(2), i, i, sample, i))
    t = AC_ALBEDO.sub(albedo, t, count=1)
    block = ('        [Header(Ao and Cavity)]\n'
             '        _Ao ("Ao", Range(0, 1)) = 0.0\n'
             '        _Cavity ("Cavity", Range(0, 1)) = 0.0\n')
    if own:
        block += '        _AoCavityMask ("Ao and Cavity Mask (R = Ao, G = Cavity)", 2D) = "white" {}\n'
    block += '        [Space(6)]\n'
    t = t[:pm.end()] + block + t[pm.end():]
    decl = "        float _Ao;\n        float _Cavity;\n" + ("        sampler2D _AoCavityMask;\n" if own else "")
    m = re.search(r"^[ \t]*float[ \t]+_FlipNormalY;[^\n]*\n", t, re.M) or re.search(r"^[ \t]*struct[ \t]+Input", t, re.M)
    if not m:
        return text, False
    pos = m.end() if m.group(0).rstrip().endswith(";") else m.start()
    t = t[:pos] + decl + t[pos:]
    return (t.replace("\n", "\r\n") if crlf else t), ("the shader's own " + mask if not own else "a new mask slot")


def count_separate_samplers(text):
    """Number of 2D texture properties that are still declared as their own sampler2D."""
    code = chk_strip(text)
    pend = chk_body(code, "Properties")
    if not pend:
        return 0
    names = set(re.findall(r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(_\w+)[ \t]*\(\"[^\"\n]*\"[ \t]*,[ \t]*2D\b", code[pend[0]:pend[1]], re.M))
    return sum(1 for n in names if re.search(r"^[ \t]*sampler2D[ \t]+%s[ \t]*;" % re.escape(n), code, re.M))


def share_samplers(text, nearest=()):
    """Turns per-texture sampler2D slots into slots that share the sampler of one host texture (UNITY_DECLARE_TEX2D for the host,
    UNITY_DECLARE_TEX2D_NOSAMPLER for the rest). Returns (new_text, number_of_textures_converted). A texture that is used in any way
    other than tex2D/tex2Dlod/tex2Dgrad (for example passed to a function) is left alone."""
    code = chk_strip(text)
    pend = chk_body(code, "Properties")
    if not pend or "FPU_TEX2D(" in text:
        return text, 0
    names = re.findall(r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(_\w+)[ \t]*\(\"[^\"\n]*\"[ \t]*,[ \t]*2D\b", code[pend[0]:pend[1]], re.M)
    prog = code[:pend[0]] + " " * (pend[1] - pend[0]) + code[pend[1]:]
    nearest = set(nearest or ())
    done = []
    for n in dict.fromkeys(names):
        decl = re.search(r"^([ \t]*)sampler2D([ \t]+%s[ \t]*;)" % re.escape(n), prog, re.M)
        if not decl:
            continue
        calls = list(re.finditer(r"(?<![\w.])tex2D(lod|grad)?[ \t]*\([ \t]*%s[ \t]*," % re.escape(n), prog))
        total = len(re.findall(r"(?<![\w])%s(?![\w])" % re.escape(n), prog))
        if total != len(calls) + 1:
            continue        # used some other way (passed on, _TexelSize...): keep it a classic sampler2D
        done.append((n, n in nearest))
    if not done:
        return text, 0
    crlf = "\r\n" in text
    t = text.replace("\r\n", "\n")
    for n, point in done:
        suffix = "P" if point else ""
        t = re.sub(r"(?<![\w.])tex2D(lod|grad)?([ \t]*)\(([ \t]*)%s([ \t]*),"
                   % re.escape(n),
                   lambda m: "FPU_TEX2D%s%s%s(%s%s%s," % (suffix, (m.group(1) or "").upper(), m.group(2), m.group(3), n, m.group(4)),
                   t)
    trilinear = [n for n, point in done if not point]
    pointy = [n for n, point in done if point]
    host_t, host_p = pick_sampler_hosts(trilinear, pointy)
    t = declare_shared_textures(t, [n for n, _ in done], host_t, host_p)
    m = re.search(r"^[ \t]*#pragma[ \t]+target[ \t]+([\d.]+)[^\n]*$", t, re.M)
    if m and float(m.group(1)) < 3.5:
        t = t[:m.start(1)] + "3.5" + t[m.end(1):]
    t, ok = insert_shared_macros(t, host_t, host_p)
    if not ok:
        return text, 0
    t, _ = add_host_touch(t)
    return (t.replace("\n", "\r\n") if crlf else t), len(done)

def guard_divisions(text):
    """Wraps every division in the generated code whose divisor could be 0 in FPU_NZ(), which nudges an exact 0 to 0.00001, and defines the macro.
    UE turns x/0 into a harmless huge number; Unity warns about it and can let NaN/Inf reach bloom (black squares, flares)."""
    crlf = "\r\n" in text
    t = text.replace("\r\n", "\n")
    lines = t.split("\n")
    safe = set()
    for ln in lines:
        m = re.match(r"\s*\w+\s+(v\d+)\s*=\s*FPU_NZ\(", ln)
        if m:
            safe.add(m.group(1))
    stmt = re.compile(r"^\s*(?:float|half|int)\d?(?:x\d)?\s+v\d+\s*=")
    n = 0

    def divisor_end(ln, k):
        while k < len(ln) and ln[k] == " ":
            k += 1
        start = k
        if k < len(ln) and ln[k] == "(":
            depth = 0
            while k < len(ln):
                if ln[k] == "(":
                    depth += 1
                elif ln[k] == ")":
                    depth -= 1
                    if depth == 0:
                        return start, k + 1
                k += 1
            return start, -1
        m = re.compile(r"[A-Za-z_][\w]*(?:\.[A-Za-z_]\w*)*(?:\(|)").match(ln, k)
        if not m:
            return start, start
        k = m.end()
        if ln[k - 1] == "(":
            depth = 1
            while k < len(ln) and depth:
                if ln[k] == "(":
                    depth += 1
                elif ln[k] == ")":
                    depth -= 1
                k += 1
            if depth:
                return start, -1
            tail = re.compile(r"(?:\.[A-Za-z_]\w*)*").match(ln, k)
            k = tail.end()
        return start, k

    fmod_re = re.compile(r"fmod\(((?:[^(),]|\([^()]*\))+),\s*([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)\)")

    def wrap_fmod(m):
        nonlocal n
        if m.group(2) in safe:
            return m.group(0)
        n += 1
        return "fmod(%s, FPU_NZ(%s))" % (m.group(1), m.group(2))

    out = []
    for ln in lines:
        if stmt.match(ln) and "fmod(" in ln:
            ln = fmod_re.sub(wrap_fmod, ln)
        if stmt.match(ln) and " / " in ln:
            pos = 0
            res = ""
            while True:
                k = ln.find(" / ", pos)
                if k < 0:
                    res += ln[pos:]
                    break
                a, b = divisor_end(ln, k + 3)
                res += ln[pos:k + 3]
                if b <= a:
                    pos = k + 3
                    continue
                div = ln[a:b]
                plain = div.strip()
                if re.fullmatch(r"-?\d+(?:\.\d*)?(?:[eE][-+]?\d+)?f?", plain) and float(plain.rstrip("f")) != 0.0:
                    res += div
                elif plain.startswith(("FPU_NZ(", "max(", "abs(")) or plain in safe:
                    res += div
                else:
                    res += "FPU_NZ(%s)" % div
                    n += 1
                pos = b
            ln = res
        out.append(ln)
    t = "\n".join(out)
    if n and "#define FPU_NZ" not in t:
        mt = re.search(r"^[ \t]*#pragma[ \t]+target[^\n]*\n", t, re.M)
        if not mt:
            return text, 0
        t = t[:mt.end()] + "        #define FPU_NZ(b) ((b) + ((b) == 0.0) * 0.00001)\n" + t[mt.end():]
    if not n:
        return text, 0
    return (t.replace("\n", "\r\n") if crlf else t), n


def clean_path(text):
    return text.strip().strip('"').strip("'")


def run_one(src, material, out, name=None):
    try:
        shader_name, tr = convert(src, material=material, name=name, out=out)
        return {"material": material, "shader": shader_name, "warnings": len(tr.warnings), "ok": True}
    except SystemExit as e:
        print("failed:", e.code if isinstance(e.code, str) else "no graph found")
    except Exception as e:
        print("failed: %s: %s" % (type(e).__name__, e))
    return {"material": material, "shader": None, "warnings": 0, "ok": False}


# ---- shader check mode: drag shaders in, see what is wrong, fix the safe things ---------------
# Works on any generated .shader file. Static checks always run; compiling in Unity runs when an editor is found.
CHK_TYPES = r"(?:float[234]?|half[234]?|fixed[234]?|int|bool)"
CHK_WIDTH = {"float": 1, "half": 1, "fixed": 1, "int": 1, "bool": 1, "float2": 2, "half2": 2, "fixed2": 2,
             "float3": 3, "half3": 3, "fixed3": 3, "float4": 4, "half4": 4, "fixed4": 4}
CHK_INPUT_MEMBERS = {"tc01": "float4 tc01;", "worldPos": "float3 worldPos;", "viewDir": "float3 viewDir;",
                     "color": "float4 color : COLOR;", "objPos": "float3 objPos;", "screenPos": "float4 screenPos;",
                     "worldNormal": "float3 worldNormal;", "facing": "float facing : VFACE;", "pcol": "float4 pcol;", "pdp": "float4 pdp;", "pdp2": "float4 pdp2;"}
CHK_OURS = re.compile(r"\b_(?:[SVCOT])_\w+\b")
UNITY_EDITOR_PATH = os.environ.get("UNITY_EDITOR", "")
UNITY_FIX_ROUNDS = 10
VERT_LOCALS = {"uv0": "float2 uv0 = v.texcoord.xy;", "uv1": "float2 uv1 = v.texcoord1.xy;",
               "vpos": "float3 vpos = v.vertex.xyz;", "vnrm": "float3 vnrm = v.normal;",
               "vtan": "float4 vtan = v.tangent;", "vcol": "float4 vcol = v.color;"}


class Issue(object):
    def __init__(self, level, msg, line=None, fix=None):
        self.level, self.msg, self.line, self.fix = level, msg, line, fix   # level: error / warn / info


def chk_strip(text):
    """Code with comments and string contents blanked, same length so line numbers stay valid."""
    out = re.sub(r"//[^\n]*", lambda m: " " * len(m.group(0)), text)
    out = re.sub(r"/\*.*?\*/", lambda m: re.sub(r"[^\n]", " ", m.group(0)), out, flags=re.S)
    return re.sub(r'"[^"\n]*"', lambda m: '"' + " " * (len(m.group(0)) - 2) + '"', out)


def chk_function_blocks(block_text):
    return {m.group(1): m.group(0) for m in re.finditer(r"^(?:float\d?(?:x\d)?|void)\s+(\w+)\(.*?\n\}\n", block_text, re.M | re.S)}


def chk_helper_library():
    lib = {}
    for blk in (HELPERS, NOISE_HELPERS, DEPTH_HELPERS, VIEW_HELPERS, NORMAL_HELPERS):
        lib.update(chk_function_blocks(blk))
    return lib


def chk_line_of(text, pos):
    return text.count("\n", 0, pos) + 1


def chk_body(text, signature):
    """(start, end) character offsets of the { } body of the function whose header contains signature."""
    m = re.search(re.escape(signature) + r"[^{]*\{", text)
    if not m:
        return None
    depth, i = 0, m.end() - 1
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return m.end(), i
        i += 1
    return None


def check_shader_text(text):
    """text uses \\n line ends. Returns a list of Issue."""
    issues = []
    code = chk_strip(text)
    # --- structure
    for what, pat in (("Shader header", r'^\s*Shader\s+"'), ("Properties block", r"^\s*Properties\b"), ("SubShader", r"\bSubShader\b"),
                      ("CGPROGRAM", r"\bCGPROGRAM\b"), ("ENDCG", r"\bENDCG\b"), ("#pragma surface", r"#pragma\s+surface\s+surf"),
                      ("surf function", r"void\s+surf\s*\("), ("vert function", r"void\s+vert\s*\(")):
        if not re.search(pat, code, re.M):
            issues.append(Issue("error", "missing: " + what))
    for a, b, n in (("{", "}", "braces"), ("(", ")", "parentheses"), ("[", "]", "brackets")):
        if code.count(a) != code.count(b):
            issues.append(Issue("error", "unbalanced %s: %d open, %d close (the file looks cut off or damaged, convert it again)" % (n, code.count(a), code.count(b))))
    if issues and any(i.msg.startswith("unbalanced") for i in issues):
        return issues   # nothing below is reliable on a broken file
    # --- properties and declarations
    pm = re.search(r"Properties\s*\{", code)
    pend = chk_body(code, "Properties")
    props, dup_props = {}, []
    if pend:
        for mm in re.finditer(r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(_\w+)[ \t]*\(\"[^\"\n]*\"[ \t]*,[ \t]*(\w+)", code[pend[0]:pend[1]], re.M):
            ln = chk_line_of(text, pend[0] + mm.start())
            if mm.group(1) in props:
                dup_props.append((mm.group(1), ln))
            props[mm.group(1)] = mm.group(2)
    pstart = pend[0] if pend else 0
    pstop = pend[1] if pend else 0
    if pend:
        body_lines = code[pend[0]:pend[1]].split("\n")
        while body_lines and not body_lines[-1].strip():
            body_lines.pop()
        if body_lines and re.fullmatch(r"\s*(?:\[[^\]\n]*\]\s*)+", body_lines[-1]):
            issues.append(Issue("error", "the Properties block ends with an attribute that has no property after it (%s). Unity fails with \"Parse error: syntax error, unexpected '}', expecting TVAL_ID or TVAL_VARREF\". The attribute is removed" % body_lines[-1].strip(), chk_line_of(text, pend[0]) + len(body_lines) - 1, ("dangling_attr",)))
    prog = code[:pstart] + " " * (pstop - pstart) + code[pstop:]    # everything except the Properties block
    decls = {}
    dup_decls = []
    for mm in re.finditer(r"^[ \t]*(?:static[ \t]+const[ \t]+)?(float[234]?|half[234]?|fixed[234]?|int|sampler2D(?:_float)?|Texture2D|samplerCUBE)[ \t]+(_\w+)[ \t]*(?:;|=)", prog, re.M):
        ln = chk_line_of(text, mm.start())
        if mm.group(2) in decls:
            dup_decls.append((mm.group(2), ln))
        decls[mm.group(2)] = mm.group(1)
    for mm in re.finditer(r"^[ \t]*UNITY_DECLARE_TEX2D(?:_NOSAMPLER)?\([ \t]*(_\w+)[ \t]*\)[ \t]*;", prog, re.M):
        ln = chk_line_of(text, mm.start())
        if mm.group(1) in decls:
            dup_decls.append((mm.group(1), ln))
        decls[mm.group(1)] = "Texture2D"
    for name, ln in dup_props:
        issues.append(Issue("error", "property %s is declared twice" % name, ln, ("dup_prop", name)))
    for name, ln in dup_decls:
        issues.append(Issue("error", "%s is declared twice in the shader code" % name, ln, ("dup_decl", name)))
    used = {}
    for mm in CHK_OURS.finditer(prog):
        used.setdefault(mm.group(0), chk_line_of(text, mm.start()))
    auto_cutoff = re.search(r"#pragma[ \t]+surface\b[^\n]*\balphatest:_Cutoff\b", code) is not None
    for extra in (("_FlipNormalY",) if auto_cutoff else ("_FlipNormalY", "_Cutoff")):
        if re.search(r"\b%s\b" % extra, prog):
            used.setdefault(extra, 0)
    if auto_cutoff and "_Cutoff" in decls:
        cut_ln = 0
        for mm in re.finditer(r"^[ \t]*(?:float|half|fixed)[ \t]+_Cutoff[ \t]*;", prog, re.M):
            cut_ln = chk_line_of(text, mm.start())
            break
        issues.append(Issue("error", "_Cutoff is declared in the shader code, but \"alphatest:_Cutoff\" on the #pragma surface line already makes Unity declare it in every pass, so it fails with \"redefinition of '_Cutoff'\". The extra declaration is removed", cut_ln, ("auto_cutoff",)))
    for name, ln in sorted(used.items(), key=lambda kv: kv[1]):
        if name in decls:
            continue
        if name in props:
            issues.append(Issue("error", "%s is used but has no declaration in the shader code" % name, ln, ("add_decl", name, props[name])))
        else:
            issues.append(Issue("error", "%s is used but is not declared anywhere (convert the material again)" % name, ln))
    if not SHOW_ALL_PARAMS and pend:
        bake = []
        for mm in re.finditer(r'^[ \t]*((?:\[[^\]\n]*\][ \t]*)*)(_\w+)[ \t]*\("([^"\n]*)"[ \t]*,[ \t]*(\w+)', text[pend[0]:pend[1]], re.M):
            name, label, typ = mm.group(2), mm.group(3), mm.group(4)
            if typ not in ("Float", "Range", "Int", "Color", "Vector") or "HideInInspector" in mm.group(1):
                continue
            if name.startswith(("_O_", "_C_")) or name not in decls or decls[name].startswith("sampler"):
                continue
            if is_hardcoded(name, label):
                bake.append((name, label))
        if bake:
            shown = ", ".join(l for _, l in bake[:4]) + (" ..." if len(bake) > 4 else "")
            issues.append(Issue("warn", "%d parameters only matter inside Fortnite (%s). They are baked in at their default values and removed from the inspector, so the look does not change" % (len(bake), shown), None, ("bake_hidden", [n for n, _ in bake])))
    for name, typ in props.items():
        if typ == "2D" and name not in decls:
            issues.append(Issue("error", "texture %s has no sampler2D declaration" % name, None, ("add_decl", name, "2D")))
    if "SamplerState sampler_TrilinearRepeat" in text and re.search(r"#pragma\s+surface", text):
        issues.append(Issue("error", "the shader uses the Beta21-Beta23 shared-sampler form (free-standing SamplerState names). Unity's surface shaders still give every texture its own sampler register with it, so it fails with \"maximum ps_5_0 sampler register index (16) exceeded\" once there are about 15 textures. The textures can share one host texture's sampler instead", None, ("upgrade_shared",)))
    if LEGACY_SHARED_PLATFORMS in text and "FPU_TEX2DLOD" in text:
        issues.append(Issue("error", "the LOD / gradient sampling macros choose their branch with SHADER_API_D3D11, which Unity's surface-shader analysis pass does not define (it fails with \"'tex2D': no matching 4 parameter intrinsic function\"). They have to test UNITY_SEPARATE_TEXTURE_SAMPLER, the macro Unity's own texture macros use", None, ("upgrade_shared",)))
    if HOST_DEFINE.search(text) and "FPU_HostTouch" not in text and re.search(r"#pragma\s+surface", text):
        issues.append(Issue("error", "the shared-sampler host texture is not used in every shader stage. Unity pairs a sampler with the texture of the same name in each compiled program and fails with \"Unrecognized sampler 'sampler_..._s' - does not match any texture\" when the host is missing there (the vertex stage samples with .SampleLevel and never uses the host). The host gets a zero-valued use in vert() and surf()", None, ("host_touch",)))
    if fix_equal_smoothstep(code)[1]:
        issues.append(Issue("warn", "smoothstep() with two identical edges divides by zero (\"floating point division by zero\"). UE gives the same hard 0/1 jump as step(), so it is rewritten as step()", None, ("equal_smoothstep",)))
    n_sep = count_separate_samplers(text)
    if n_sep > SAMPLER_BUDGET:
        issues.append(Issue("error", "%d textures each use their own sampler2D. Unity on DX11 allows only 16 sampler registers per shader and its lighting needs several, so this fails with \"maximum ps_4_0 sampler register index (16) exceeded\" (the textures can share one host texture's sampler instead)" % n_sep, None, ("share_samplers",)))
    for name, typ in decls.items():
        if CHK_OURS.fullmatch(name) and name not in props and not re.search(r"static[ \t]+const[ \t]+\w+[ \t]+%s\b" % name, prog):
            if name.startswith("_O_"):
                issues.append(Issue("info", "%s is not a property: it is filled in per object by the bounds script" % name))
            else:
                issues.append(Issue("warn", "%s has no property: it stays 0 and cannot be edited" % name, None, ("add_prop", name, typ)))
    # --- helper and custom functions
    defined = set(re.findall(r"^[ \t]*(?:float\d?(?:x\d)?|void)[ \t]+(\w+)[ \t]*\(", prog, re.M))
    called = {}
    for mm in re.finditer(r"\b([A-Za-z_]\w*UE|Custom\d+)[ \t]*\(", prog):
        called.setdefault(mm.group(1), chk_line_of(text, mm.start()))
    for mm in re.finditer(r"^[ \t]*float(\d?)[ \t]+(Custom\d+)[ \t]*\(", prog, re.M):
        span = chk_body(code, mm.group(2) + "(")
        if span:
            body = code[span[0]:span[1]]
            if not re.search(r"\breturn\b", body) or CUSTOM_UE_SYMBOLS.search(body):
                issues.append(Issue("error", "%s still has Unreal code (no return, or ResolvedView / View / MaterialFloat, or BRANCH / Texture2DSample / ProcessMaterial... / UnpackNormalMap); Unity cannot compile it" % mm.group(2), chk_line_of(text, mm.start()), ("fix_custom",)))
    lib = chk_helper_library()
    for name, ln in called.items():
        if name not in defined:
            if name in lib:
                issues.append(Issue("error", "helper function %s is used but missing" % name, ln, ("add_helper", name)))
            else:
                issues.append(Issue("error", "function %s is used but never defined (convert the material again)" % name, ln))
    # --- Input struct
    sm = re.search(r"struct\s+Input\s*\{", code)
    if sm:
        sb = chk_body(code, "struct Input")
        members = set(re.findall(r"\b(\w+)\s*(?::\s*\w+)?\s*;", code[sb[0]:sb[1]])) if sb else set()
        body_all = code
        for mem in sorted(set(re.findall(r"\bIN\.(\w+)", body_all))):
            if mem not in members:
                if mem in CHK_INPUT_MEMBERS:
                    issues.append(Issue("error", "IN.%s is used but missing from struct Input" % mem, None, ("add_input", mem)))
                else:
                    issues.append(Issue("error", "IN.%s is used but is not a known Input member" % mem))
        needs_internal = ("WorldNormalVector(" in code or "WorldReflectionVector(" in code or
                          (re.search(r"\bIN\.(?:worldNormal|worldRefl)\b", code) and re.search(r"\bo\.Normal\s*=", code)))
        if sb and needs_internal and "INTERNAL_DATA" not in code[sb[0]:sb[1]]:
            issues.append(Issue("error", "WorldNormalVector is used but struct Input has no INTERNAL_DATA", None, ("add_input", "INTERNAL_DATA")))
    elif re.search(r"\bIN\.", code):
        issues.append(Issue("error", "struct Input is missing"))
    for inc in ("UnityCG.cginc", "Lighting.cginc"):
        if "CGPROGRAM" in code and inc not in text:
            issues.append(Issue("warn", "#include \"%s\" is missing" % inc, None, ("add_include", inc)))
    # --- variables inside surf and vert: defined before use, not defined twice, vector widths
    for fname, sig in (("surf", "void surf("), ("vert", "void vert(")):
        span = chk_body(code, sig)
        if not span:
            continue
        seg = code[span[0]:span[1]]
        base = chk_line_of(text, span[0])
        if fname == "vert":
            for mm in re.finditer(r"\b(?:tex2D|tex2Dbias|tex2Dproj)\s*\(", seg):
                issues.append(Issue("error", "vert() samples a texture with %s; the vertex stage cannot, it needs tex2Dlod" % mm.group(0).strip("( "), chk_line_of(text, span[0] + mm.start()), ("tex2d_vert",)))
                break
            for name in VERT_LOCALS:
                decl = re.search(r"\b" + CHK_TYPES + r"\s+" + name + r"\b", seg)
                uses = [u for u in re.finditer(r"\b" + name + r"\b", seg) if not decl or u.start() != decl.end() - len(name)]
                if uses and (not decl or uses[0].start() < decl.start()):
                    why = "is never defined" if not decl else "is used before the line that defines it"
                    issues.append(Issue("error", "vert() uses %s but it %s" % (name, why), chk_line_of(text, span[0] + uses[0].start()), ("hoist_local", name)))
            for mm in re.finditer(r"\b(fwidth|ddx|ddy)\s*\(", seg):
                issues.append(Issue("error", "vert() uses %s; screen-space derivatives only exist in the pixel stage (convert the material again)" % mm.group(1), chk_line_of(text, span[0] + mm.start())))
                break
        widths, seen_dup = {}, set()
        arg_names = set(re.findall(r"\b(v\d+)\b", code[max(0, span[0] - 160):span[0]]))
        for off, full_line in enumerate(seg.split("\n")):
            ln = base + off
            for ln_text in full_line.split(";"):
                if not ln_text.strip():
                    continue
                md = re.match(r"\s*(" + CHK_TYPES + r")\s+(\w+)\s*=\s*(.*)$", ln_text)
                rhs = md.group(3) if md else ln_text
                for u in re.findall(r"\bv\d+\b", rhs):
                    if u not in widths and u not in arg_names:
                        issues.append(Issue("error", "%s() uses %s on this line but it was never defined above (convert the material again)" % (fname, u), ln))
                        widths[u] = 0   # report once
                if md:
                    typ, var = md.group(1), md.group(2)
                    if re.match(r"v\d+$", var) and widths.get(var):
                        issues.append(Issue("error", "%s() defines %s twice" % (fname, var), ln))
                    w = CHK_WIDTH.get(typ, 1)
                    widths[var] = w
                    src = re.fullmatch(r"\s*(v\d+)\s*", rhs)
                    if src and widths.get(src.group(1), 0) > w:
                        issues.append(Issue("warn", "%s(): %s (%d wide) is squeezed into a %s on this line" % (fname, src.group(1), widths[src.group(1)], typ), ln, ("trunc", ln, src.group(1), w)))
    return issues


def chk_split_args(argtext):
    args, depth, cur = [], 0, ""
    for ch in argtext:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            args.append(cur)
            cur = ""
        else:
            cur += ch
    args.append(cur)
    return args


def chk_tex2d_to_lod(seg):
    """tex2D(s, uv) -> tex2Dlod(s, float4((uv).xy, 0.0, 0.0)) inside one function body. Returns (text, count)."""
    out, i, n = "", 0, 0
    while True:
        m = re.compile(r"(?<![\w.])tex2D\s*\(").search(seg, i)
        if not m:
            out += seg[i:]
            return out, n
        depth, j = 1, m.end()
        while j < len(seg) and depth:
            depth += {"(": 1, ")": -1}.get(seg[j], 0)
            j += 1
        args = chk_split_args(seg[m.end():j - 1])
        out += seg[i:m.start()]
        if len(args) == 2:
            out += "tex2Dlod(%s, float4((%s).xy, 0.0, 0.0))" % (args[0].strip(), args[1].strip())
            n += 1
        else:
            out += seg[m.start():j]
        i = j


def hoist_vert_locals(text, names):
    span = chk_body(chk_strip(text), "void vert(")
    if not span:
        return text, []
    seg = text[span[0]:span[1]]
    moved = []
    for n in names:
        if n not in VERT_LOCALS:
            continue
        pat = re.compile(r"^[ \t]*" + CHK_TYPES + r"[ \t]+" + n + r"[ \t]*=([^;\n]*);[ \t]*\n", re.M)
        canon = VERT_LOCALS[n].split("=", 1)[1].strip().rstrip(";").strip()
        if any(m.group(1).strip() != canon for m in pat.finditer(seg)):
            continue
        seg = pat.sub("", seg)
        moved.append(n)
    if not moved:
        return text, []
    ins = "".join("            %s\n" % VERT_LOCALS[n] for n in moved)
    anchor = re.search(r"UNITY_INITIALIZE_OUTPUT\([^\n]*\n", seg)
    seg = seg[:anchor.end()] + ins + seg[anchor.end():] if anchor else "\n" + ins + seg.lstrip("\n")
    return text[:span[0]] + seg + text[span[1]:], moved


def fix_custom_functions(text):
    """Rewrites every CustomNN function that still holds Unreal code. Returns (new_text, count)."""
    count = 0
    for name in re.findall(r"^[ \t]*float\d?[ \t]+(Custom\d+)[ \t]*\(", chk_strip(text), re.M):
        code = chk_strip(text)
        span = chk_body(code, name + "(")
        head = re.search(r"float(\d?)[ \t]+%s[ \t]*\(" % name, code)
        if not span or not head:
            continue
        body = text[span[0]:span[1]]
        if re.search(r"\breturn\b", code[span[0]:span[1]]) and not CUSTOM_UE_SYMBOLS.search(code[span[0]:span[1]]):
            continue
        decls, new, ok = ue_custom_code(body, int(head.group(1) or 1))
        lines = ["        " + d for d in decls] + (["        " + new.strip()] if not re.search(r"\breturn\b", body) or not ok else [new.strip("\n")])
        text = text[:span[0]] + "\n" + "\n".join(lines) + "\n        " + text[span[1]:]
        count += 1
    first = re.search(r"^[ \t]*float\d?[ \t]+Custom\d+[ \t]*\(", text, re.M)
    for bn in ("_O_BoundsMin", "_O_BoundsMax"):
        if first and re.search(r"\b%s\b" % bn, chk_strip(text)) and not re.search(r"^[ \t]*(?:static[ \t]+const[ \t]+)?float4[ \t]+%s\b" % bn, text, re.M):
            text = text[:first.start()] + "        float4 %s;\n" % bn + text[first.start():]
    blocks = dict(chk_function_blocks(VIEW_HELPERS))
    blocks.update(chk_function_blocks(NORMAL_HELPERS))
    prog = chk_strip(text)
    add = [b for n, b in blocks.items() if re.search(r"\b%s\s*\(" % n, prog) and not re.search(r"^[ \t]*float4x4[ \t]+%s[ \t]*\(" % n, prog, re.M)]
    if add:
        block = "\n".join(("        " + l) if l else "" for l in "\n".join(add).split("\n"))
        m = re.search(r"^[ \t]*#include[ \t]+\"Lighting\.cginc\"[^\n]*\n", text, re.M) or re.search(r"^[ \t]*#pragma[ \t]+target[^\n]*\n", text, re.M)
        pos = m.end() if m else text.find("CGPROGRAM") + len("CGPROGRAM\n")
        text = text[:pos] + block + "\n" + text[pos:]
    return text, count


def bake_hidden_props(text, names):
    """Turns exposed properties into static const values at their default and drops them from the Properties block."""
    n = 0
    for name in names:
        pm = re.search(r'^[ \t]*(?:\[[^\]\n]*\][ \t]*)*%s[ \t]*\("[^"\n]*"[ \t]*,[ \t]*(\w+)[ \t]*(?:\([^)\n]*\))?[ \t]*\)[ \t]*=[ \t]*([^\n]*?)[ \t]*\n' % re.escape(name), text, re.M)
        dm = re.search(r'^([ \t]*)(float[234]?|half[234]?|fixed[234]?|int)[ \t]+%s[ \t]*;' % re.escape(name), text, re.M)
        if not pm or not dm:
            continue
        typ, raw, ctype = pm.group(1), pm.group(2).strip(), dm.group(2)
        nums = [float(x) for x in re.findall(r"-?\d*\.?\d+(?:[eE][-+]?\d+)?", raw)]
        if typ in ("Color", "Vector"):
            dims = int(ctype[-1]) if ctype[-1].isdigit() else 0
            if dims < 2 or len(nums) < dims:
                continue
            value = "%s(%s)" % (ctype, ", ".join(repr(v) for v in nums[:dims]))
        else:
            if not nums or ctype[-1].isdigit():
                continue
            value = str(int(nums[0])) if ctype == "int" else repr(nums[0])
        text = text[:pm.start()] + text[pm.end():]
        text = re.sub(r'^([ \t]*)%s[ \t]+%s[ \t]*;' % (re.escape(ctype), re.escape(name)), lambda m: "%sstatic const %s %s = %s;" % (m.group(1), ctype, name, value), text, flags=re.M)
        n += 1
    text = re.sub(r"^[ \t]*\[Header\([^\n]*\)\][ \t]*\n[ \t]*\[Space\([^\n]*\)\][ \t]*\n", "", text, flags=re.M)
    text = re.sub(r"^[ \t]*\[Header\([^\n]*\)\][ \t]*\n(?=[ \t]*(?:\[Header\(|\}))", "", text, flags=re.M)
    return text, n


def apply_fixes(text, issues):
    """Applies the safe fixes. Returns (new_text, [what was done])."""
    done = []
    lib = chk_helper_library()
    fixes = [i.fix for i in issues if i.fix]
    # duplicates
    for f in fixes:
        if f[0] == "dup_prop":
            lines = text.split("\n")
            pend = chk_body(chk_strip(text), "Properties")
            seen = False
            for idx, l in enumerate(lines):
                if re.match(r"^[ \t]*(?:\[[^\]]*\][ \t]*)*%s[ \t]*\(" % re.escape(f[1]), l):
                    if seen:
                        lines[idx] = None
                    seen = True
            text = "\n".join(l for l in lines if l is not None)
            done.append("removed the duplicate property %s" % f[1])
        elif f[0] == "dup_decl":
            lines = text.split("\n")
            seen = False
            for idx, l in enumerate(lines):
                if re.match(r"^[ \t]*(?:static[ \t]+const[ \t]+)?\w+[ \t]+%s[ \t]*(?:;|=)" % re.escape(f[1]), l):
                    if seen:
                        lines[idx] = None
                    seen = True
            text = "\n".join(l for l in lines if l is not None)
            done.append("removed the duplicate declaration of %s" % f[1])
    if any(f[0] == "dangling_attr" for f in fixes):
        span = chk_body(chk_strip(text), "Properties")
        if span:
            seg = text[span[0]:span[1]]
            new = re.sub(r"(?:^[ \t]*\[[^\]\n]*\][ \t]*\n)+(?=[ \t]*\Z)", "", seg, flags=re.M)
            if new != seg:
                text = text[:span[0]] + new + text[span[1]:]
                done.append("removed the attribute at the end of the Properties block that had no property after it")
    if any(f[0] == "auto_cutoff" for f in fixes):
        new_text = re.sub(r"^[ \t]*(?:float|half|fixed)[ \t]+_Cutoff[ \t]*;[ \t]*\n", "", text, flags=re.M)
        if new_text != text:
            text = new_text
            done.append("removed the extra _Cutoff declaration (alphatest:_Cutoff already declares it, which caused \"redefinition of '_Cutoff'\")")
    for f in fixes:
        if f[0] == "bake_hidden":
            text, n = bake_hidden_props(text, f[1])
            if n:
                done.append("%d game-only parameters baked in at their default values and removed from the inspector" % n)
    if any(f[0] == "upgrade_shared" for f in fixes):
        text, ok = upgrade_shared_sampler_block(text)
        if ok:
            done.append("shared-sampler form rewritten: one host texture keeps its sampler, every other texture is declared without one and samples through it")
    # shared samplers (Unity's 16-sampler limit)
    if any(f[0] == "share_samplers" for f in fixes):
        near = set()
        text, n = share_samplers(text, near)
        if n:
            done.append("%d textures now share the sampler of one host texture; #pragma target raised to 3.5 if it was lower" % n)
    if any(f[0] == "fix_custom" for f in fixes):
        text, n = fix_custom_functions(text)
        if n:
            done.append("%d custom function(s) rewritten from Unreal code to Unity code (return added, View matrices mapped, shader macros removed). Functions that sampled a texture could not be mapped to it from the shader text alone, so they return 0 until the material is converted again" % n)
    if any(f[0] == "host_touch" for f in fixes):
        text, ok = add_host_touch(text)
        if ok:
            done.append("shared-sampler host texture(s) now used in vert() and surf() (value 0, the look is unchanged), so Unity no longer reports an unrecognized sampler")
    if any(f[0] == "equal_smoothstep" for f in fixes):
        text, n = fix_equal_smoothstep(text)
        if n:
            done.append("%d smoothstep() call(s) with identical edges rewritten as step() (they divided by zero)" % n)
    # includes
    for f in fixes:
        if f[0] == "add_include":
            text = re.sub(r"(CGPROGRAM[^\n]*\n)", lambda m: m.group(1) + "        #include \"%s\"\n" % f[1], text, count=1)
            done.append("added #include %s" % f[1])
    # declarations
    add_lines = []
    for f in fixes:
        if f[0] == "add_decl":
            typ = {"Float": "float", "Range": "float", "Int": "float", "Vector": "float4", "Color": "float4", "2D": "sampler2D"}.get(f[2], "float4")
            add_lines.append("        %s %s;" % (typ, f[1]))
            done.append("declared %s" % f[1])
    if add_lines:
        m = re.search(r"^[ \t]*float4?[ \t]+_FlipNormalY;|^[ \t]*struct[ \t]+Input", text, re.M)
        pos = m.start() if m else text.find("ENDCG")
        text = text[:pos] + "\n".join(add_lines) + "\n" + text[pos:]
    # properties for uniforms that had none (hidden, so the inspector stays tidy)
    prop_lines = []
    for f in fixes:
        if f[0] == "add_prop":
            if f[2].startswith("float") and f[2] not in ("float2", "float3", "float4"):
                prop_lines.append('        [HideInInspector] %s ("%s", Float) = 0' % (f[1], f[1].lstrip("_")))
            elif f[2].startswith("sampler"):
                prop_lines.append('        %s ("%s", 2D) = "white" {}' % (f[1], f[1].lstrip("_")))
            else:
                prop_lines.append('        [HideInInspector] %s ("%s", Vector) = (0, 0, 0, 0)' % (f[1], f[1].lstrip("_")))
            done.append("added a hidden property for %s" % f[1])
    if prop_lines:
        pend = chk_body(chk_strip(text), "Properties")
        if pend:
            text = text[:pend[1]] + "\n".join(prop_lines) + "\n    " + text[pend[1]:].lstrip(" ")
    # helper functions (with the helpers they call)
    need = []
    def want(n):
        if n in lib and n not in need:
            need.append(n)
            for dep in re.findall(r"\b(\w+UE)\s*\(", lib[n]):
                if dep != n:
                    want(dep)
    for f in fixes:
        if f[0] == "add_helper":
            want(f[1])
    if need:
        block = ""
        if any(n in ("SceneDepthUE", "DepthFadeUE") for n in need) and "_CameraDepthTexture" not in text:
            block += "UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);\nfloat _PorterDepthFade;\n"
        for n in reversed(need):    # dependencies first
            block += lib[n] + "\n"
        block = "\n".join(("        " + l) if l else "" for l in block.split("\n"))
        m = re.search(r"^[ \t]*#include[ \t]+\"Lighting\.cginc\"[^\n]*\n", text, re.M) or re.search(r"^[ \t]*#pragma[ \t]+target[^\n]*\n", text, re.M)
        pos = m.end() if m else text.find("CGPROGRAM") + len("CGPROGRAM\n")
        text = text[:pos] + block + "\n" + text[pos:]
        done.append("added the missing helper functions: " + ", ".join(sorted(need)))
    # Input members
    members = []
    for f in fixes:
        if f[0] == "add_input":
            members.append("INTERNAL_DATA" if f[1] == "INTERNAL_DATA" else CHK_INPUT_MEMBERS[f[1]])
            done.append("added %s to struct Input" % f[1])
    if members:
        m = re.search(r"(struct\s+Input\s*\{[^\n]*\n)", text)
        if m:
            ind = "            "
            plain = [x for x in members if x != "INTERNAL_DATA"]
            ins = "".join(ind + x + "\n" for x in plain)
            text = text[:m.end()] + ins + text[m.end():]
            if "INTERNAL_DATA" in members and "INTERNAL_DATA" not in text[text.find("struct Input"):]:
                text = re.sub(r"(struct\s+Input\s*\{.*?)(\n\s*\};)", lambda mm: mm.group(1) + "\n" + ind + "INTERNAL_DATA" + mm.group(2), text, count=1, flags=re.S)
    names = [f[1] for f in fixes if f[0] == "hoist_local"]
    if names:
        text, moved = hoist_vert_locals(text, names)
        if moved:
            done.append("vertex stage: defined %s at the top of vert() so they exist before they are used" % ", ".join(moved))
    if any(f[0] == "tex2d_vert" for f in fixes):
        span = chk_body(chk_strip(text), "void vert(")
        if span:
            new_seg, n = chk_tex2d_to_lod(text[span[0]:span[1]])
            if n:
                text = text[:span[0]] + new_seg + text[span[1]:]
                done.append("vertex stage: %d tex2D call(s) changed to tex2Dlod" % n)
    # squeezed vectors: add the swizzle (line numbers refer to the text before the edits above, so find by content)
    for f in fixes:
        if f[0] == "trunc":
            _, ln, var, w = f
            sw = {1: ".x", 2: ".xy", 3: ".xyz"}[w]
            text, n = re.subn(r"(=\s*)%s(\s*;)" % re.escape(var), r"\g<1>%s%s\2" % (var, sw), text)
            if n:
                done.append("trimmed %s to %s (it was wider than its target)" % (var, sw[1:]))
    return text, done


def find_sidecar(path):
    stem = os.path.splitext(os.path.basename(path))[0]
    folder = os.path.dirname(os.path.abspath(path))
    for cand in (stem + ".textures.json", stem + "_textures.json"):
        p = os.path.join(folder, cand)
        if os.path.exists(p):
            return p
    return None


def conversion_report(path):
    """Reads the warnings the Porter saved next to the shader and sorts them into gaps / approximations / harmless."""
    side = find_sidecar(path)
    if not side:
        return None
    try:
        j = json.load(open(side, encoding="utf-8"))
    except Exception:
        return None
    gaps, approx, fyi = [], [], []
    for w in (j.get("warnings") or []):
        low = w.lower()
        if "skylightenvmapsample" in low or "skyatmospherelightilluminanceonground" in low:
            fyi.append(w)
        elif "approximat" in low or "passed through" in low or "held static" in low or "generic character-sized" in low:
            approx.append(w)
        elif "not converted" in low or low.startswith("unsupported") or "stand-in" in low or "replaced by 0" in low or "-> 0" in low:
            gaps.append(re.sub(r"^NOT CONVERTED:\s*", "", w))
        else:
            fyi.append(w)
    missing_tex = []
    folder = os.path.dirname(os.path.abspath(path))
    tex_dir = os.path.join(folder, "Textures")
    for t in (j.get("textureList") or []):
        nm = t.get("name") if isinstance(t, dict) else None
        if nm and os.path.isdir(tex_dir) and not any(f.lower().startswith(nm.lower() + ".") for f in os.listdir(tex_dir)):
            missing_tex.append(nm)
    return {"file": side, "gaps": gaps, "approx": approx, "fyi": fyi, "missing_tex": missing_tex, "notes": j.get("notes") or []}


def find_unity_editor():
    global UNITY_EDITOR_PATH
    if UNITY_EDITOR_PATH and os.path.exists(UNITY_EDITOR_PATH):
        return UNITY_EDITOR_PATH
    import glob
    cands = []
    for base in (os.environ.get("ProgramFiles", r"C:\Program Files"), os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), r"D:\Program Files", r"C:\Program Files\Unity"):
        cands += glob.glob(os.path.join(base, "Unity", "Hub", "Editor", "*", "Editor", "Unity.exe"))
        cands += glob.glob(os.path.join(base, "Unity", "Editor", "Unity.exe"))
    cands += glob.glob("/Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity")
    cands.sort(key=lambda p: (("2022.3" not in p), p))
    return cands[0] if cands else None


CHECK_EDITOR_CS = r'''using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PorterShaderCheck
{
    public static void Run()
    {
        var sb = new StringBuilder();
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/PorterCheck" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(path);
            bool bad = sh == null || ShaderUtil.ShaderHasError(sh);
            sb.AppendLine((bad ? "ERROR " : "OK ") + path);
        }
        File.WriteAllText("PorterShaderCheck.txt", sb.ToString());
    }
}
'''


def parse_unity_log(text, names):
    """Pulls 'Shader error in ...' lines out of a Unity log. names maps shader name -> file path.
    Understands both 'at Assets/X.shader(123)' and 'at line 123'."""
    out = {}
    pat = re.compile(r"Shader (error|warning) in '([^']+)':\s*(.*?)(?:\s+at\s+(?:line\s+(\d+)|[^\s(]+\((\d+)\)))?(?:\s*\(on [^)]*\))?\s*$", re.M)
    for m in pat.finditer(text):
        shader = m.group(2)
        target = names.get(shader) or next((names[k] for k in names if k.split("/")[-1] == shader.split("/")[-1]), None)
        if not target:
            continue
        line = int(m.group(4) or m.group(5) or 0)
        msg = m.group(3).strip() or "(no message)"
        item = ("error" if m.group(1) == "error" else "warn", msg, line)
        if item not in out.setdefault(target, []):
            out[target].append(item)
    return out


def unity_compile(paths, unity=None, timeout=900):
    """Imports the shaders into a throw-away project with the real Unity editor and returns
    {shader_path: [(level, message, line)]}. Returns None if Unity cannot be run."""
    import subprocess, tempfile
    unity = unity or find_unity_editor()
    if not unity:
        return None
    proj = os.path.join(tempfile.gettempdir(), "porter_shader_check_project")
    os.makedirs(proj, exist_ok=True)
    log = os.path.join(proj, "check.log")

    def run(args):
        return subprocess.run([unity, "-batchmode", "-quit", "-logFile", log] + args, timeout=timeout, capture_output=True)

    try:
        if not os.path.exists(os.path.join(proj, "ProjectSettings", "ProjectVersion.txt")):
            print("  Unity: creating a throw-away check project (first run only, can take a minute)...")
            run(["-createProject", proj])
        drop = os.path.join(proj, "Assets", "PorterCheck")
        shutil.rmtree(drop, ignore_errors=True)
        os.makedirs(drop, exist_ok=True)
        os.makedirs(os.path.join(proj, "Assets", "Editor"), exist_ok=True)
        open(os.path.join(proj, "Assets", "Editor", "PorterShaderCheck.cs"), "w", encoding="utf-8").write(CHECK_EDITOR_CS)
        names = {}
        for p in paths:
            dst = os.path.join(drop, os.path.basename(p))
            shutil.copy(p, dst)
            m = re.search(r'Shader\s+"([^"]+)"', open(p, encoding="utf-8", errors="replace").read())
            names[m.group(1) if m else os.path.basename(p)] = p
        print("  Unity: compiling %d shader(s) in the real editor..." % len(paths))
        run(["-projectPath", proj, "-executeMethod", "PorterShaderCheck.Run"])
    except Exception as e:
        print("  Unity: could not run (%s: %s)" % (type(e).__name__, e))
        return None
    if not os.path.exists(log):
        return None
    out = {p: [] for p in paths}
    text = open(log, encoding="utf-8", errors="replace").read()
    for p, found in parse_unity_log(text, names).items():
        out[p] += found
    verdict = os.path.join(proj, "PorterShaderCheck.txt")
    if os.path.exists(verdict):
        for l in open(verdict, encoding="utf-8").read().splitlines():
            if l.startswith("ERROR "):
                base = os.path.basename(l[6:])
                for p in out:
                    if os.path.basename(p) == base and not any(lv == "error" for lv, _, _ in out[p]):
                        raw = [x.strip() for x in text.splitlines() if base in x or "Shader error" in x or "error:" in x.lower()]
                        for x in raw[:8]:
                            out[p].append(("error", "log: " + x[:200], 0))
                        if not raw:
                            out[p].append(("error", "Unity reports a compile error (see its log: %s)" % log, 0))
    return out


UNITY_BUILTIN_NEEDS = {"_CameraDepthTexture": "decl:UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);", "_LightColor0": "inc:Lighting.cginc",
                       "_Time": "inc:UnityCG.cginc", "_SinTime": "inc:UnityCG.cginc", "_CosTime": "inc:UnityCG.cginc",
                       "_WorldSpaceCameraPos": "inc:UnityCG.cginc", "_ScreenParams": "inc:UnityCG.cginc",
                       "_ProjectionParams": "inc:UnityCG.cginc", "_ZBufferParams": "inc:UnityCG.cginc",
                       "unity_ObjectToWorld": "inc:UnityCG.cginc", "unity_WorldToObject": "inc:UnityCG.cginc",
                       "_WorldSpaceLightPos0": "inc:UnityCG.cginc", "unity_OrthoParams": "inc:UnityCG.cginc",
                       "unity_CameraProjection": "inc:UnityCG.cginc", "unity_SpecCube0": "inc:UnityCG.cginc",
                       "UNITY_MATRIX_VP": "inc:UnityCG.cginc", "UNITY_MATRIX_V": "inc:UnityCG.cginc", "UNITY_MATRIX_P": "inc:UnityCG.cginc",
                       "UNITY_MATRIX_I_V": "inc:UnityCG.cginc", "unity_MatrixVP": "inc:UnityCG.cginc", "unity_FogColor": "inc:UnityCG.cginc",
                       "unity_AmbientSky": "inc:UnityCG.cginc"}
UNITY_HINTS = [
    (r"Unrecognized sampler .*does not match any texture", "a shared-sampler shader samples through the sampler of a host texture, but that host texture is not used in the same compiled program (typically the vertex stage). Using the host texture in vert() and surf() fixes it"),
    (r"division by zero", "the compiler sees a divisor that is exactly 0 (a constant folded through the graph); UE would give a huge number, Unity gives Inf/NaN. FPU_NZ() nudges the divisor off 0"),
    (r"sampler register index|too many samplers|maximum ps_\d_\d sampler", "the shader uses more than 16 sampler registers (one per texture, plus Unity's shadow, reflection-probe and lightmap samplers); the textures can share one host texture's sampler instead (UNITY_DECLARE_TEX2D_NOSAMPLER + UNITY_SAMPLE_TEX2D_SAMPLER)"),
    (r"'tex2D(?:lod)?': no matching \d parameter intrinsic function",
     "the shader samples a Texture2D (shared sampler) with tex2D / tex2Dlod / tex2Dgrad; in Unity's surface-shader analysis pass tex2Dgrad is defined as tex2D, so LOD and gradient samples have to use .SampleLevel / .SampleGrad whenever UNITY_SEPARATE_TEXTURE_SAMPLER is defined"),
    (r"Unexpected identifier .?(?:Texture2D|SamplerState)", "Unity's surface-shader analysis pass does not know Texture2D / SamplerState; shared samplers have to be declared with UNITY_DECLARE_TEX2D / UNITY_DECLARE_TEX2D_NOSAMPLER"),
    (r"too many (?:texture )?interpolators", "the surface shader passes more data between stages than the compile target allows (#pragma target 3.0 = 10 interpolators, 4.0 = 32); an unlit shader first drops the lightmap, ambient and vertex-light data it never uses, and raising the target fixes the rest"),
    (r"unable to unroll loop", "Unity could not unroll a loop at compile time; marking it [loop] keeps it as a real loop"),
    (r"needs INTERNAL_DATA", "struct Input needs INTERNAL_DATA when world normal/reflection is read while o.Normal is written"),
    (r"cannot map expression to vs_", "a pixel-stage-only instruction (usually tex2D or a derivative) is used in vert(); the vertex stage needs tex2Dlod"),
    (r"gradient instruction used in a loop|can't use gradient instructions", "tex2D / derivatives inside a loop or branch with varying flow are undefined; use tex2Dlod or move the sample out of the loop"),
    (r"implicit truncation of vector type", "a wider vector is squeezed into a smaller one; add a swizzle (.xyz, .xy, .x) on that line"),
    (r"undeclared identifier", "a name is used that was never declared: a property, helper function, or Unity built-in whose include is missing"),
    (r"redefinition of", "the same name is declared twice in one scope"),
    (r"cannot convert|cannot implicitly convert", "two types do not match (for example float3 assigned to float4); add or remove a component"),
    (r"no matching \d+ parameter (?:intrinsic )?function", "a function was called with the wrong number or type of arguments"),
    (r"maximum temp(?:orary)? registers? exceeded|too many instructions|instruction slot count exceeded", "the shader is too long for the compile target; raise #pragma target to 4.0 or simplify the graph"),
]


UNLIT_KEYWORDS = ("noforwardadd", "nolightmap", "nodynlightmap", "nodirlightmap", "novertexlights", "noambient")
PROP_DECL_TYPES = {"float": "float", "range": "float", "int": "float", "vector": "float4", "color": "float4"}


def trim_unlit_lighting(text):
    m = re.search(r"#pragma[ \t]+surface[ \t]+surf[ \t]+Unlit[^\n]*", text)
    if not m:
        return text, False
    have = set(m.group(0).split())
    missing = [k for k in UNLIT_KEYWORDS if k not in have]
    if not missing:
        return text, False
    return text[:m.end()].rstrip() + " " + " ".join(missing) + text[m.end():], True


def declare_missing_property(text, ident):
    code = chk_strip(text)
    span = chk_body(code, "Properties")
    if not span:
        return text, False
    m = re.search(r"^[ \t]*(?:\[[^\]\n]*\][ \t]*)*" + re.escape(ident) + r"[ \t]*\(\"[^\"\n]*\"[ \t]*,[ \t]*(\w+)", code[span[0]:span[1]], re.M)
    if not m:
        return text, False
    kind = PROP_DECL_TYPES.get(m.group(1).lower())
    if not kind:
        return text, False
    anchor = re.search(r"CGPROGRAM[^\n]*\n(?:[ \t]*#[^\n]*\n)*", text)
    if not anchor:
        return text, False
    return text[:anchor.end()] + "        %s %s;\n" % (kind, ident) + text[anchor.end():], True


def remove_duplicate_declaration(text, ident, line_no):
    span = chk_body(chk_strip(text), "Properties")
    pat = re.compile(r"^([ \t]*)(?:static[ \t]+const[ \t]+)?(?:float[234]?|half[234]?|fixed[234]?|int|sampler2D|sampler3D|samplerCUBE)[ \t]+" + re.escape(ident) + r"[ \t]*(?:;|=)[^\n]*\n", re.M)
    hits = [m for m in pat.finditer(text) if not (span and span[0] <= m.start() < span[1])]
    if len(hits) < 2:
        return text, False
    pick = None
    for m in hits[1:]:
        if chk_line_of(text, m.start()) == line_no:
            pick = m
    if pick is None:
        top = [m for m in hits if m.group(1) == hits[0].group(1)]
        if len(top) != 2:
            return text, False
        pick = top[1]
    return text[:pick.start()] + text[pick.end():], True


def mark_loop(text, line_no):
    lines = text.split("\n")
    if not 0 < line_no <= len(lines):
        return text, False
    line = lines[line_no - 1]
    m = re.match(r"^(\s*)((?:for|while)\s*\()", line)
    if not m or re.search(r"\[\s*(?:loop|unroll|fastopt)", lines[line_no - 2] if line_no > 1 else ""):
        return text, False
    lines[line_no - 1] = m.group(1) + "[loop] " + line[len(m.group(1)):]
    return "\n".join(lines), True


def unity_hint(msg):
    for pat, hint in UNITY_HINTS:
        if re.search(pat, msg, re.I):
            return hint
    return None


def apply_unity_fixes(text, msgs):
    done = []
    orig_lines = text.split("\n")
    orig_span = chk_body(chk_strip(text), "void vert(")
    orig_vert_lines = None
    if orig_span:
        orig_vert_lines = (chk_line_of(text, orig_span[0]), chk_line_of(text, orig_span[1]))
    vert_done = False
    wanted_locals = []
    for lv, msg, ln in msgs:
        if lv != "error" and re.search(r"division by zero", msg, re.I):
            if not any("smoothstep" in d for d in done):
                text, ns = fix_equal_smoothstep(text)
                if ns:
                    done.append("%d smoothstep() call(s) with identical edges rewritten as step() (Unity warned: %s)" % (ns, msg[:60]))
            if not any("guarded divisions" in d for d in done):
                text, n = guard_divisions(text)
                if n:
                    done.append("guarded divisions: %d division(s) by a variable now go through FPU_NZ() (Unity warned: %s)" % (n, msg[:60]))
            continue
        if lv != "error":
            continue
        if re.search(r"too many (?:texture )?interpolators|maximum temp|too many instructions|instruction slot count", msg, re.I):
            if re.search(r"interpolators", msg, re.I):
                text, trimmed = trim_unlit_lighting(text)
                if trimmed:
                    done.append("unlit shader: lightmap, ambient and vertex-light data switched off so it fits the interpolator limit (Unity said: %s)" % msg[:80])
                    continue
            m = re.search(r"#pragma\s+target\s+([\d.]+)", text)
            if m and float(m.group(1)) < 4.0:
                text = text[:m.start(1)] + "4.0" + text[m.end(1):]
                done.append("raised #pragma target from %s to 4.0 (Unity said: %s)" % (m.group(1), msg[:80]))
        elif re.search(r"Unrecognized sampler .*does not match any texture", msg, re.I):
            if not any("host texture" in d for d in done):
                text, ok = add_host_touch(text)
                if ok:
                    done.append("shared-sampler host texture(s) now used in vert() and surf() (value 0, the look is unchanged) (Unity said: %s)" % msg[:80])
        elif re.search(r"sampler register index|too many samplers|maximum ps_\d_\d sampler", msg, re.I):
            text, ok = upgrade_shared_sampler_block(text)
            if ok:
                done.append("shared-sampler form rewritten so the textures share one host texture's sampler (Unity said: %s)" % msg[:80])
            else:
                text, n = share_samplers(text, set())
                if n:
                    done.append("%d textures now share the sampler of one host texture (Unity said: %s)" % (n, msg[:80]))
        elif re.search(r"Unexpected identifier .?(?:Texture2D|SamplerState).?|'tex2D(?:lod)?': no matching \d parameter intrinsic", msg, re.I):
            text, ok = upgrade_shared_sampler_block(text)
            if ok:
                done.append("shared-sampler form rewritten so Unity's surface-shader analysis pass accepts it (Unity said: %s)" % msg[:80])
        elif re.search(r"needs INTERNAL_DATA", msg, re.I):
            sb = chk_body(chk_strip(text), "struct Input")
            if sb and "INTERNAL_DATA" not in text[sb[0]:sb[1]]:
                text = re.sub(r"(struct\s+Input\s*\{.*?)(\n\s*\};)", lambda mm: mm.group(1) + "\n            INTERNAL_DATA" + mm.group(2), text, count=1, flags=re.S)
                done.append("added INTERNAL_DATA to struct Input (Unity asked for it)")
        elif re.search(r"cannot map expression to vs_|'tex2D(?:bias|proj)?': no matching", msg, re.I):
            in_vert = bool(orig_vert_lines) and orig_vert_lines[0] <= ln <= orig_vert_lines[1]
            if (in_vert or re.search(r"vs_", msg)) and not vert_done:
                span = chk_body(chk_strip(text), "void vert(")
                if span:
                    new_seg, n = chk_tex2d_to_lod(text[span[0]:span[1]])
                    if n:
                        text = text[:span[0]] + new_seg + text[span[1]:]
                        vert_done = True
                        done.append("vertex stage: %d tex2D call(s) changed to tex2Dlod (Unity rejected tex2D there)" % n)
        elif re.search(r"unable to unroll loop", msg, re.I) and 0 < ln <= len(orig_lines):
            now = text.split("\n")
            ok = False
            if ln <= len(now) and now[ln - 1] == orig_lines[ln - 1]:
                text, ok = mark_loop(text, ln)
            if ok:
                done.append("line %d: loop marked [loop] so Unity keeps it as a real loop (Unity said: %s)" % (ln, msg[:60]))
        elif re.search(r"redefinition of '(\w+)'", msg, re.I):
            ident = re.search(r"redefinition of '(\w+)'", msg, re.I).group(1)
            text, ok = remove_duplicate_declaration(text, ident, ln)
            if ok:
                done.append("duplicate declaration of %s removed (Unity said: %s)" % (ident, msg[:60]))
        elif re.search(r"invalid subscript", msg, re.I) and 0 < ln <= len(orig_lines):
            sub = (re.search(r"invalid subscript '(\w+)'", msg) or [None, None])[1]
            line = orig_lines[ln - 1]
            if sub and line in text:
                scalars = set(re.findall(r"(?m)^\s*(?:const\s+)?(?:float|half|fixed|int)\s+(\w+)\s*[=;]", text))
                rep = "x" * len(sub)
                fixed = re.sub(r"\b(\w+)\.%s\b" % re.escape(sub), lambda mm: mm.group(1) + "." + rep if mm.group(1) in scalars else mm.group(0), line)
                if fixed != line:
                    text = text.replace(line, fixed, 1)
                    done.append("line %d: scalar swizzled with .%s now splats with .%s (Unity said: %s)" % (ln, sub, rep, msg[:60]))
        elif re.search(r"cannot (?:implicitly )?convert from", msg, re.I) and 0 < ln <= len(orig_lines):
            mt = re.search(r"from '(?:const\s+)?(?:float|half|fixed)(\d?)' to '(?:const\s+)?(?:float|half|fixed)(\d?)'", msg)
            line = orig_lines[ln - 1]
            md = re.match(r"^(\s*)((?:float|half|fixed)\d?)(\s+\w+\s*=\s*)(.*?)(\s*;.*)$", line)
            if mt and md and line in text:
                a, b = int(mt.group(1) or 1), int(mt.group(2) or 1)
                expr = md.group(4)
                if a > b:
                    new_expr = "(%s).%s" % (expr, "xyzw"[:b])
                elif a == 1:
                    new_expr = "(%s).%s" % (expr, "x" * b)
                else:
                    new_expr = "float%d(%s, %s)" % (b, expr, ", ".join(["0.0"] * (b - a)))
                text = text.replace(line, md.group(1) + md.group(2) + md.group(3) + new_expr + md.group(5), 1)
                done.append("line %d: float%s value fitted to float%s (Unity said: %s)" % (ln, a if a > 1 else "", b if b > 1 else "", msg[:60]))
        elif re.search(r"implicit truncation of vector type", msg, re.I) and 0 < ln <= len(orig_lines):
            line = orig_lines[ln - 1]
            mt = re.match(r"^(\s*)(" + CHK_TYPES + r")(\s+\w+\s*=\s*)(v\d+)(\s*;.*)$", line)
            if mt and line in text:
                w = CHK_WIDTH.get(mt.group(2), 1)
                if w < 4:
                    sw = {1: ".x", 2: ".xy", 3: ".xyz"}[w]
                    text = text.replace(line, mt.group(1) + mt.group(2) + mt.group(3) + mt.group(4) + sw + mt.group(5), 1)
                    done.append("line %d: trimmed %s to %s (Unity said the vector was truncated)" % (ln, mt.group(4), sw[1:]))
        else:
            m = re.search(r"undeclared identifier '(\w+)'", msg)
            if not m:
                continue
            ident = m.group(1)
            if ident in VERT_LOCALS:
                if ident not in wanted_locals:
                    wanted_locals.append(ident)
            elif ident in UNITY_BUILTIN_NEEDS:
                kind, val = UNITY_BUILTIN_NEEDS[ident].split(":", 1)
                if kind == "inc" and val not in text:
                    text = re.sub(r"(CGPROGRAM[^\n]*\n)", lambda mm: mm.group(1) + "        #include \"%s\"\n" % val, text, count=1)
                    done.append("added #include %s (Unity did not know %s)" % (val, ident))
                elif kind == "decl" and ident not in re.sub(r"//[^\n]*", "", text.split("CGPROGRAM", 1)[-1].split("void surf", 1)[0].replace("_CameraDepthTexture)", "")).replace("(", " "):
                    text = re.sub(r"(CGPROGRAM[^\n]*\n(?:[ \t]*#[^\n]*\n)*)", lambda mm: mm.group(1) + "        " + val + "\n", text, count=1)
                    done.append("declared %s (Unity did not know it)" % ident)
            else:
                text, ok = declare_missing_property(text, ident)
                if ok:
                    done.append("declared property %s in the shader code (Unity did not know it)" % ident)
    if wanted_locals:
        text, moved = hoist_vert_locals(text, wanted_locals)
        if moved:
            done.append("vertex stage: defined %s at the top of vert() (Unity said they were undeclared)" % ", ".join(moved))
    return text, done


def gather_shader_paths(text):
    """Turns a dragged-in line (quoted paths, folders, several at once) into a list of .shader files."""
    found = re.findall(r'"([^"]+)"|\'([^\']+)\'|(\S+)', text)
    paths = []
    for a, b, c in found:
        p = clean_path(a or b or c)
        if os.path.isdir(p):
            for root, _, files in os.walk(p):
                paths += [os.path.join(root, f) for f in sorted(files) if f.lower().endswith(".shader")]
        elif os.path.isfile(p) and p.lower().endswith(".shader"):
            paths.append(p)
        elif p:
            print("  skipped (not a .shader file or folder): %s" % p)
    seen, out = set(), []
    for p in paths:
        if p not in seen:
            seen.add(p)
            out.append(p)
    return out


def read_shader(path):
    raw = open(path, encoding="utf-8", errors="replace", newline="").read()
    return raw, "\r\n" in raw, raw.replace("\r\n", "\n").replace("\r", "\n")


def write_shader(path, text, crlf):
    open(path, "w", encoding="utf-8", newline="").write(text.replace("\n", "\r\n") if crlf else text)


def default_fixed_dir():
    return os.path.join(os.path.expanduser("~"), "Documents", "Fortnite Unity Converter", "Fixed Conversions")


def sync_folder(src, dst):
    for root, _, files in os.walk(src):
        rel = os.path.relpath(root, src)
        target = dst if rel == "." else os.path.join(dst, rel)
        os.makedirs(target, exist_ok=True)
        for f in files:
            a, b = os.path.join(root, f), os.path.join(target, f)
            if os.path.exists(b) and os.path.getsize(a) == os.path.getsize(b) and int(os.path.getmtime(a)) <= int(os.path.getmtime(b)):
                continue
            shutil.copy2(a, b)


def make_fixed_copies(paths, fixed_out):
    fixed_root = os.path.abspath(fixed_out)
    os.makedirs(fixed_root, exist_ok=True)
    copies, taken = {}, {}
    for p in paths:
        ap = os.path.abspath(p)
        try:
            already = os.path.commonpath([ap, fixed_root]) == fixed_root
        except ValueError:
            already = False
        if already:
            copies[p] = p
            continue
        stem = os.path.splitext(os.path.basename(p))[0]
        n = taken.get(stem, 0)
        taken[stem] = n + 1
        folder = os.path.join(fixed_root, stem if n == 0 else "%s_%d" % (stem, n + 1))
        os.makedirs(folder, exist_ok=True)
        dst = os.path.join(folder, os.path.basename(p))
        shutil.copyfile(p, dst)
        side = find_sidecar(p)
        if side:
            shutil.copyfile(side, os.path.join(folder, os.path.basename(side)))
        tex = os.path.join(os.path.dirname(ap), "Textures")
        if os.path.isdir(tex):
            sync_folder(tex, os.path.join(folder, "Textures"))
        copies[p] = dst
    return copies


def print_issue(i):
    print("   %s%s: %s%s" % (i.level.upper(), (" line %d" % i.line) if i.line else "", i.msg, "   [can fix]" if i.fix else ""))


def check_one_shader(path, fix=True):
    raw, crlf, text = read_shader(path)
    print("")
    print("=" * 60)
    print(os.path.basename(path))
    print("=" * 60)
    issues = check_shader_text(text)
    rep = conversion_report(path)
    if rep:
        if rep["gaps"]:
            print("CONVERSION: %d part(s) NOT converted (replaced by 0 or a stand-in)" % len(rep["gaps"]))
        elif rep["approx"]:
            print("CONVERSION: everything converted, %d part(s) approximated" % len(rep["approx"]))
        else:
            print("CONVERSION: everything converted")
        for w in rep["gaps"]:
            print("   NOT CONVERTED:", w)
        for w in rep["approx"]:
            print("   approximated:", w)
        if rep["fyi"]:
            print("   (%d harmless notes: texture overrides, sky lighting, time wrap...)" % len(rep["fyi"]))
        if rep["missing_tex"]:
            print("   texture files missing next to the shader: " + ", ".join(rep["missing_tex"]))
    else:
        print("CONVERSION: no saved warnings found next to this shader (the .textures.json from the Porter), so only the code was checked")
    n_zero = len(re.findall(r"\b0\.0\s*\*\s*v\d+|\bv\d+\s*\*\s*0\.0\b", text))
    if n_zero:
        print("   %d line(s) multiply by a constant 0 (normal for switched-off effects, but also what a missing node looks like)" % n_zero)
    errs = [i for i in issues if i.level == "error"]
    warns = [i for i in issues if i.level == "warn"]
    if not issues:
        print("CODE: no problems found")
    else:
        print("CODE: %d error(s), %d warning(s)" % (len(errs), len(warns)))
        for i in issues:
            if i.level != "info":
                print_issue(i)
    remaining = len(errs)
    fixes_done = []
    src_lines = text.split("\n")
    detail = {"conversion": rep, "issues": [{"level": i.level, "line": i.line, "msg": i.msg, "fixable": bool(i.fix),
              "src": src_lines[i.line - 1].strip()[:200] if i.line and 0 < i.line <= len(src_lines) else ""} for i in issues if i.level != "info"],
              "fixes": [], "left": []}
    if fix and any(i.fix for i in issues):
        fixed_text, done = apply_fixes(text, issues)
        again = check_shader_text(fixed_text)
        left = [i for i in again if i.level in ("error", "warn")]
        if done and fixed_text != text:
            write_shader(path, fixed_text, crlf)
            fixes_done = done
            detail["fixes"] = list(done)
            detail["left"] = [{"level": i.level, "line": i.line, "msg": i.msg} for i in left]
            remaining = len([i for i in left if i.level == "error"])
            print("FIXED %d thing(s) in the copy:" % len(done))
            for d in done:
                print("   -", d)
            print("   still open: %d" % len(left))
            for i in left:
                print_issue(i)
    return {"errors": remaining, "gaps": len(rep["gaps"]) if rep else None, "fixed": len(fixes_done), "unity_errors": None, "unity_fixed": 0, "detail": detail}


def shader_check_mode(fix=True, unity=None):
    print("")
    print("Shader check: drag .shader files or a folder into this window, then press Enter.")
    print("Empty line to go back.")
    while True:
        try:
            line = input("check> ").strip()
        except (EOFError, KeyboardInterrupt):
            print("")
            return
        if not line:
            return
        paths = gather_shader_paths(line)
        if not paths:
            print("no .shader files found in that")
            continue
        run_shader_check(paths, fix=fix, unity=unity)


def print_unity_verdict(path, msgs, applied):
    errs = [m for m in msgs if m[0] == "error"]
    raw, crlf, text = read_shader(path)
    lines = text.split("\n")
    print("")
    print("%s: %s" % (os.path.basename(path), "Unity compiles it with no errors" if not errs else "Unity still reports %d error(s) that could not be fixed automatically" % len(errs)))
    for d in applied:
        print("   fixed: " + d)
    shown = [m for m in msgs if m[0] == "error"] + [m for m in msgs if m[0] != "error"]
    for lv, msg, ln in shown[:25]:
        ctx = lines[ln - 1].strip()[:110] if 0 < ln <= len(lines) else ""
        print("   %s line %d: %s%s" % (lv.upper(), ln, msg, ("\n        > " + ctx) if ctx else ""))
        h = unity_hint(msg)
        if h:
            print("        hint: " + h)
    if len(shown) > 25:
        print("   ... and %d more" % (len(shown) - 25))


def write_fix_log(paths, originals, summary, unity_msgs, unity_applied, fixed_dir):
    """Writes one text file describing everything the check found and fixed, with a before/after diff per shader,
    meant to be sent back so the converter itself can be fixed."""
    import difflib, datetime
    now = datetime.datetime.now()
    out = ["FORTNITE UNITY CONVERTER - SHADER FIX LOG",
           "Made: " + now.strftime("%Y-%m-%d %H:%M:%S"),
           "Shaders checked: %d" % len(paths),
           "Send this file so the converter can be fixed to stop producing these problems.", ""]
    total_fixed = 0
    for p in paths:
        r = summary.get(p) or {}
        d = r.get("detail") or {}
        orig = originals.get(p)
        try:
            cur = read_shader(p)[2]
        except Exception:
            cur = None
        out += ["=" * 70, "SHADER: " + os.path.basename(p), "=" * 70]
        try:
            m = re.search(r'^\s*Shader\s+"([^"]+)"', orig or "", re.M)
            if m:
                out.append("Unity shader name: " + m.group(1))
        except Exception:
            pass
        rep = d.get("conversion")
        if rep:
            out.append("Converter report: %d not converted, %d approximated" % (len(rep["gaps"]), len(rep["approx"])))
            for w in rep["gaps"]:
                out.append("   NOT CONVERTED: " + w)
            for w in rep["approx"]:
                out.append("   approximated: " + w)
            if rep["missing_tex"]:
                out.append("   missing textures: " + ", ".join(rep["missing_tex"]))
        else:
            out.append("Converter report: none found next to the shader")
        out += ["", "PROBLEMS FOUND IN THE CODE (%d):" % len(d.get("issues") or [])]
        for i in d.get("issues") or []:
            out.append("   [%s]%s %s%s" % (i["level"].upper(), (" line %d:" % i["line"]) if i["line"] else "", i["msg"], "  (auto-fixable)" if i["fixable"] else ""))
            if i["src"]:
                out.append("        > " + i["src"])
        out += ["", "CODE FIXES APPLIED (%d):" % len(d.get("fixes") or [])]
        for f in d.get("fixes") or []:
            out.append("   - " + f)
        if d.get("left"):
            out.append("STILL OPEN AFTER THOSE FIXES:")
            for i in d["left"]:
                out.append("   [%s]%s %s" % (i["level"].upper(), (" line %d:" % i["line"]) if i["line"] else "", i["msg"]))
        if p in unity_msgs:
            msgs = unity_msgs[p]
            errs = [m for m in msgs if m[0] == "error"]
            out += ["", "UNITY COMPILE: %s" % ("no errors" if not errs else "%d error(s) left" % len(errs))]
            for lv, msg, ln in msgs[:40]:
                out.append("   %s line %d: %s" % (lv.upper(), ln, msg))
            ap = unity_applied.get(p) or []
            out.append("UNITY FIXES APPLIED (%d):" % len(ap))
            for f in ap:
                out.append("   - " + f)
            total_fixed += len(ap)
        total_fixed += len(d.get("fixes") or [])
        if orig is not None and cur is not None and orig != cur:
            diff = list(difflib.unified_diff(orig.split("\n"), cur.split("\n"), "as converted", "after fixes", lineterm="", n=2))
            out += ["", "CHANGES (as converted -> after fixes), %d diff lines:" % len(diff)]
            out += diff[:600]
            if len(diff) > 600:
                out.append("... %d more diff lines cut" % (len(diff) - 600))
        else:
            out += ["", "No text changes were made to this shader."]
        out.append("")
    out.insert(4, "Total fixes applied: %d" % total_fixed)
    os.makedirs(fixed_dir, exist_ok=True)
    path = os.path.join(fixed_dir, "FixLog_%s.txt" % now.strftime("%Y%m%d_%H%M%S"))
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))
    return path


def run_shader_check(paths, fix=True, unity=None, fixed_out=None):
    if fix and paths:
        target = fixed_out or default_fixed_dir()
        try:
            copies = make_fixed_copies(paths, target)
        except Exception as e:
            print("could not make the fixed copies (%s: %s), nothing was changed" % (type(e).__name__, e))
            return
        paths = [copies[p] for p in paths]
        print("Fixing copies in: %s (your originals are not touched)" % os.path.abspath(target))
    summary = {}
    originals = {}
    for p in paths:
        try:
            originals[p] = read_shader(p)[2]
        except Exception:
            pass
        summary[p] = check_one_shader(p, fix=fix)
    last, applied = {}, {p: [] for p in paths}
    ran_unity = False
    if unity is not False:
        pending = list(paths)
        for rnd in range(1, UNITY_FIX_ROUNDS + 2):
            print("")
            print("Unity compile%s..." % ("" if rnd == 1 else " (round %d, after the fixes above)" % rnd))
            res = unity_compile(pending, unity if isinstance(unity, str) else None)
            if res is None:
                if rnd == 1:
                    print("  Unity: no editor found, so Unity's own compile errors are not included (set the UNITY_EDITOR environment variable to Unity.exe to turn this on)")
                break
            ran_unity = True
            for p in pending:
                last[p] = res.get(p, [])
            if not fix or rnd > UNITY_FIX_ROUNDS:
                break
            changed = []
            for p in pending:
                msgs = last[p]
                if not any(lv == "error" or re.search(r"division by zero", m, re.I) for lv, m, _ in msgs):
                    continue
                raw, crlf, text = read_shader(p)
                new_text, done = apply_unity_fixes(text, msgs)
                if done and new_text != text:
                    write_shader(p, new_text, crlf)
                    applied[p] += done
                    changed.append(p)
            if not changed:
                break
            pending = changed
    if ran_unity:
        for p in paths:
            msgs = last.get(p, [])
            print_unity_verdict(p, msgs, applied[p])
            summary[p]["unity_errors"] = len([m for m in msgs if m[0] == "error"])
            summary[p]["unity_fixed"] = len(applied[p])
    print("")
    print("summary:")
    for p in paths:
        r = summary[p]
        bits = ["%d code error(s) left" % r["errors"]]
        if r["fixed"] or r["unity_fixed"]:
            bits.append("%d fixed" % (r["fixed"] + r["unity_fixed"]))
        if r["unity_errors"] is not None:
            bits.append("Unity: %s" % ("compiles" if not r["unity_errors"] else "%d error(s)" % r["unity_errors"]))
        if r["gaps"] is not None:
            bits.append("%d not converted" % r["gaps"])
        print("   %s: %s" % (os.path.basename(p), ", ".join(bits)))
    if fix:
        print("")
        for p in paths:
            print("fixed: %s" % os.path.abspath(p))
        print("fixed-folder: %s" % os.path.abspath(fixed_out or default_fixed_dir()))
        try:
            lp = write_fix_log(paths, originals, summary, {p: last[p] for p in paths if p in last}, applied, os.path.abspath(fixed_out or default_fixed_dir()))
            print("fixlog: %s" % lp)
        except Exception as e:
            print("could not write the fix log (%s: %s)" % (type(e).__name__, e))


def interactive():
    bridge = DEFAULT_BRIDGE
    out = "out"
    src = Source(bridge=bridge)
    history = []
    print("=" * 50)
    print(" %s" % APP_NAME)
    print(" The API must be running (bridge: %s)" % bridge)
    print("=" * 50)
    while True:
        print("")
        print(" 1  Convert a material")
        print(" 2  Convert several materials")
        print(" 3  Output folder: %s" % os.path.abspath(out))
        print(" 4  Bridge address: %s" % bridge)
        print(" 5  This session's conversions (%d)" % len(history))
        print(" 6  Check shaders (drag them in; finds problems and fixes them)")
        print(" 0  Quit")
        try:
            choice = input("> ").strip()
        except (EOFError, KeyboardInterrupt):
            print("")
            return
        if choice == "1":
            material = clean_path(input("Material path (e.g. /Game/.../MI_X.MI_X), Enter to cancel: "))
            if not material:
                continue
            name = clean_path(input("Shader name [Enter = automatic]: ")) or None
            history.append(run_one(src, material, out, name))
        elif choice == "2":
            print("Paste one material path per line. Empty line to start.")
            batch = []
            while True:
                line = clean_path(input("  path %d: " % (len(batch) + 1)))
                if not line:
                    break
                batch.append(line)
            for i, material in enumerate(batch, 1):
                print("")
                print("[%d/%d] %s" % (i, len(batch), material))
                history.append(run_one(src, material, out))
            if batch:
                done = sum(1 for h in history[-len(batch):] if h["ok"])
                print("")
                print("finished: %d of %d converted" % (done, len(batch)))
        elif choice == "3":
            value = clean_path(input("Output folder [Enter = keep current]: "))
            if value:
                out = value
        elif choice == "4":
            value = clean_path(input("Bridge address [Enter = %s]: " % DEFAULT_BRIDGE)) or DEFAULT_BRIDGE
            bridge = value
            src = Source(bridge=bridge)
        elif choice == "5":
            if not history:
                print("nothing converted yet")
            for h in history:
                mark = "ok  " if h["ok"] else "FAIL"
                print(" %s %s -> %s (%d warnings)" % (mark, h["material"], h["shader"] or "-", h["warnings"]))
        elif choice == "6":
            shader_check_mode()
        elif choice in ("0", "q", "quit", "exit"):
            return
        else:
            print("choose 1, 2, 3, 4, 5, 6 or 0")


def is_sky_mesh(c):
    """A sky dome: its mesh or component is named for a sky or dome (clouds are not part of the dome)."""
    mesh = (c.get("mesh") or "").split("/")[-1].split(".")[0]
    for n in (mesh, c.get("name") or ""):
        if re.search(r"sky|dome|skybox|skysphere", n, re.I) and not re.search(r"cloud", n, re.I):
            return bool(c.get("mesh"))
    return False


def run_blueprint_export(bridge, path, out, name, scripts=None):
    import shutil
    src = Source(bridge=bridge, folder=None, cache=None)
    bp = src._json("blueprint", path)
    comps = (bp or {}).get("components") or []
    words = ("sky", "dome", "sun", "moon", "cloud", "atmosphere", "timeofday")
    names = [(bp or {}).get("blueprint") or "", path]
    for c in comps:
        names.append(c.get("name") or "")
        names.append(c.get("mesh") or "")
        names += [m.get("material") or "" for m in (c.get("materials") or [])]
    is_sky = bool(bp) and bool(comps) and (any(is_sky_mesh(c) for c in comps) or any(w in n.lower() for n in names for w in words))
    if is_sky and not any(is_sky_mesh(c) for c in comps):
        import PropBlueprint
        if PropBlueprint.looks_like_prop(src, path):
            is_sky = False
    if not is_sky:
        import PropBlueprint
        PropBlueprint.run(src, path, out, name, scripts)
        return
    base = name or bp.get("blueprint") or re.split(r"[\\/.]", path.rstrip("/\\"))[-1]
    folder = os.path.join(out, base)
    os.makedirs(folder, exist_ok=True)
    os.makedirs(os.path.join(out, "Editor"), exist_ok=True)
    os.makedirs(os.path.join(out, "Scripts"), exist_ok=True)
    sky_materials = []
    for c in comps:
        if is_sky_mesh(c):
            c["sky"] = True
            for m in (c.get("materials") or []):
                if m.get("material") and m["material"] not in sky_materials:
                    sky_materials.append(m["material"])
    day = bp.pop("day", None)
    bp["blueprint"] = base
    if bp.get("dayError"):
        print("note: the day sequence could not be read (%s), so the prefab has no Time of Day" % bp["dayError"])
    if day:
        with open(os.path.join(folder, base + ".day.json"), "w", encoding="utf-8") as fh:
            json.dump(day, fh, separators=(",", ":"))
    with open(os.path.join(folder, base + ".blueprint.json"), "w", encoding="utf-8") as fh:
        json.dump(bp, fh, indent=2)
    here = scripts or os.path.join(os.path.dirname(os.path.abspath(__file__)), "unity")
    for fname, sub in (("PorterBlueprintSetup.cs", "Editor"), ("TimeOfDayOverlay.cs", "Editor"), ("TimeOfDayController.cs", "Scripts"), ("PorterSkyRig.cs", "Scripts"), ("PorterSkyLighting.cs", "Scripts"), ("PorterHeightFog.cs", "Scripts"), ("PorterHeightFog.shader", "Scripts")):
        srcf = os.path.join(here, fname)
        if not os.path.exists(srcf):
            sys.exit("the Unity scripts are missing from the app (%s)" % here)
        shutil.copyfile(srcf, os.path.join(out, sub, fname))
    materials, meshes = [], []
    for c in comps:
        if c.get("mesh") and c["mesh"] not in meshes:
            meshes.append(c["mesh"])
        for m in (c.get("materials") or []):
            if m.get("material") and m["material"] not in materials:
                materials.append(m["material"])
    for m in (bp.get("materials") or []):
        if m and m not in materials:
            materials.append(m)
    linked = [b for b in (bp.get("linked") or []) if b]
    print("%d components, %d meshes, %d materials, %s" % (len(comps), len(meshes), len(materials),
          "day cycle included" if day else "no day cycle"))
    for m in materials:
        print("material: %s" % m)
    for m in sky_materials:
        print("skymaterial: %s" % m)
    for m in meshes:
        print("mesh: %s" % m)
    for b in linked:
        print("linked: %s" % b)
    if not meshes and not materials:
        print("note: %s holds no meshes or materials of its own, so nothing was exported besides its day cycle" % base)
        if linked:
            print("it points at %d other Blueprint(s), listed above as linked; the sky dome is usually one of those" % len(linked))
    print("wrote %s.blueprint.json" % base)
    print("folder: %s" % os.path.abspath(folder))


def main():
    if len(sys.argv) == 1:
        interactive()
        return
    ap = argparse.ArgumentParser(prog=APP_NAME)
    ap.add_argument("--bridge", default=DEFAULT_BRIDGE)
    ap.add_argument("--folder")
    ap.add_argument("--material")
    ap.add_argument("--graph")
    ap.add_argument("--entry")
    ap.add_argument("--name")
    ap.add_argument("--out", default=".")
    ap.add_argument("--blend")
    ap.add_argument("--cache", help="folder for cached graphs and files; each shader gets its own subfolder named after it")
    ap.add_argument("--animate-unwired-time", action="store_true", help="(default now) Panner/Rotator nodes with no Time input animate with live time")
    ap.add_argument("--time-offset", type=float, default=100.0, metavar="SECONDS", help="added to Unity time so Time nodes start like Unreal's game clock (default 100, 0 turns it off)")
    ap.add_argument("--hold-unwired-time", action="store_true", help="Panner/Rotator nodes with no Time input stay static instead of animating")
    ap.add_argument("--hold-node", action="append", default=[], metavar="NAME", help="keep this unwired Panner/Rotator static (repeatable); NAME or GRAPH:NAME, e.g. MF_SheerWill_FresnelMask:MaterialExpressionPanner_2")
    ap.add_argument("--animate-node", action="append", default=[], metavar="NAME", help="animate this unwired Panner/Rotator node (repeatable), e.g. MaterialExpressionPanner_1")
    ap.add_argument("--show-all-params", action="store_true", help="show every parameter in the inspector (nothing is hard-coded and hidden)")
    ap.add_argument("--hide", action="append", default=[], metavar="REGEX", help="also hard-code and hide parameters whose UE name matches (repeatable)")
    ap.add_argument("--show", action="append", default=[], metavar="REGEX", help="keep parameters whose UE name matches visible even if hidden by default (repeatable)")
    ap.add_argument("--check-shader", nargs="+", metavar="PATH", help="check .shader files or folders for problems (and fix the safe ones unless --no-fix)")
    ap.add_argument("--no-fix", action="store_true", help="with --check-shader: only report, change nothing")
    ap.add_argument("--no-unity", action="store_true", help="with --check-shader: do not compile in the Unity editor")
    ap.add_argument("--fixed-out", metavar="FOLDER", help="with --check-shader: folder the fixed copies are written to (default: Documents\\Fortnite Unity Converter\\Fixed Conversions)")
    ap.add_argument("--unity", metavar="UNITY_EXE", help="with --check-shader: path to Unity.exe for the compile check")
    ap.add_argument("--curves", default=None, help="folder holding curve JSON exports (CQ_Something.json); also looks in Curves next to the cache")
    ap.add_argument("--keep-local-position-panning", action="store_true", help="keep object-space position for time-animated texture lookups instead of using the mesh UV (object-space position changes with scale on skinned meshes in Unity)")
    ap.add_argument("--keep-local-x", action="store_true", help="do not mirror object-space X for LocalPosition in the pixel stage (the exported Unity mesh has X mirrored relative to Unreal, so mirroring is the default)")
    ap.add_argument("--seconds", type=float, default=6.0, help="with --particle-system: how many seconds of the system to play")
    ap.add_argument("--scripts", default=None, help="with --blueprint: folder holding the Unity scripts to copy")
    ap.add_argument("--sky", action="store_true", help="the material is on a sky dome: draw it first, behind everything, without writing depth")
    ap.add_argument("--expose-globals", action="store_true", help="show the parameter-collection values (the _C_ ones) as material properties instead of fixing them in the shader, so a script can animate them")
    ap.add_argument("--blueprint", metavar="PATH", help="convert a Blueprint (object path) to a Unity prefab: a sky dome, or a prop with lights, sounds and particle systems")
    ap.add_argument("--particle-system", metavar="PATH", help="convert a Niagara system (object path) to a Unity prefab spec, built the way a prop Blueprint is")
    ap.add_argument("--annotate", action="store_true", help="add UE node names as comments in the shader (off by default)")
    ap.add_argument("--no-annotate", action="store_true", help=argparse.SUPPRESS)
    a = ap.parse_args()
    global LOCAL_X_FLIP
    LOCAL_X_FLIP = not a.keep_local_x
    global UV_FOR_LOCAL_PANNING
    UV_FOR_LOCAL_PANNING = not a.keep_local_position_panning
    if a.check_shader:
        paths = gather_shader_paths(" ".join('"%s"' % p for p in a.check_shader))
        if not paths:
            sys.exit("no .shader files found")
        run_shader_check(paths, fix=not a.no_fix, unity=False if a.no_unity else (a.unity or None), fixed_out=a.fixed_out)
        return
    if a.blueprint:
        run_blueprint_export(a.bridge, a.blueprint, a.out, a.name, a.scripts)
        return
    if a.particle_system:
        import ParticleConverter
        ParticleConverter.run_system(Source(bridge=a.bridge, folder=None, cache=None), a.particle_system, a.out, a.name, a.scripts, a.seconds)
        return
    global ANIMATE_UNWIRED_TIME
    ANIMATE_UNWIRED_TIME = not a.hold_unwired_time
    global TIME_OFFSET
    TIME_OFFSET = a.time_offset
    global ANIMATE_NODES
    ANIMATE_NODES = set(a.animate_node)
    global HOLD_NODES
    HOLD_NODES = set(a.hold_node)
    global SHOW_ALL_PARAMS, EXTRA_HIDE, EXTRA_SHOW, SKY_MATERIAL, HARDCODE_GLOBALS
    SKY_MATERIAL = a.sky
    if a.expose_globals:
        HARDCODE_GLOBALS = False
    SHOW_ALL_PARAMS = a.show_all_params
    EXTRA_HIDE = list(a.hide)
    EXTRA_SHOW = list(a.show)
    global ANNOTATE
    ANNOTATE = a.annotate and not a.no_annotate
    src = Source(bridge=a.bridge if not a.graph else None, folder=a.folder, cache=a.cache)
    src.curve_dirs = [a.curves] if a.curves else []
    convert(src, material=a.material, graph=a.graph, entry_file=a.entry, name=a.name, out=a.out, blend=a.blend)


if __name__ == "__main__":
    try:
        main()
    except SystemExit as e:
        if e.code not in (None, 0):
            print(e.code if isinstance(e.code, str) else "failed")
    except Exception:
        import traceback
        traceback.print_exc()
    if len(sys.argv) > 1 and sys.stdin and sys.stdin.isatty():
        input("Press Enter to close")
