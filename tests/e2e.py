import sys, json, os
E = sys.argv[1]; sys.path.insert(0, E)
import NiagaraPorter as NP, PropBlueprint as PB
NP.fetch_dump = lambda bridge, path: json.load(open("/mnt/user-data/uploads/" + path.rsplit("/",1)[1].split(".")[0] + ".json", encoding="utf-8-sig"))
class L:
    def __init__(self, **k): pass
PB.Loader = L
def boom(*a, **k): raise RuntimeError("no stateless in test")
PB.system_spec = boom
class Src: bridge = "x"
name = sys.argv[2]
PB.run_system(Src(), "/Game/Effects/" + name + "." + name, "/home/claude/w/out", name, scripts=E + "/unity", seconds=3.0)
