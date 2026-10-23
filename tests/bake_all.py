import sys, json, glob, os
sys.path.insert(0, sys.argv[1])
import NiagaraPorter as NP, ParticleBake as PB
NP.BAKE_FRAMES = True
out = {}
for f in sorted(glob.glob("/mnt/user-data/uploads/NS_SDVB_*.json")):
    d = json.load(open(f, encoding="utf-8-sig"))
    system, summ, sk = NP.replay(NP.to_exports(d), "s", f, 3.0)
    print("#####", os.path.basename(f))
    for em in summ:
        rec, e = NP.LAST_TRACKS[em["name"]]
        for i, (rt, props) in enumerate(NP.renderer_exports(system, e)):
            notes = []
            r = PB.bake_renderer(rec, rt, props, em["name"] + "_%d" % i, notes)
            kind = rt.replace("Niagara","").replace("RendererProperties","")
            if r:
                dt, counts, rows = PB.read_blob(r["blob"])
                assert rows.shape == (r["rows"], r["stride"]) and not np.isnan(rows).any() if (np:=__import__("numpy")) else 0
                print("  %-22s %-6s frames %d rows %6d stride %2d maxN %3d  dp %s  %s" % (em["name"], kind, r["frames"], r["rows"], r["stride"], r["maxPerFrame"], r["dpComps"], r["why"][:50]))
            else:
                print("  %-22s %-6s not baked %s" % (em["name"], kind, notes))
