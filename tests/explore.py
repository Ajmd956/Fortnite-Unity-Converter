import sys, json, time
sys.path.insert(0, sys.argv[1])
import NiagaraPorter as NP
NP.BAKE_FRAMES = True
for f in sys.argv[2:]:
    t=time.time()
    dump = json.load(open(f, encoding="utf-8-sig"))
    system, summ, skipped = NP.replay(NP.to_exports(dump), "sys", f, 2.0)
    print("#####", f.split("/")[-1], "skipped", skipped, "%.1fs"%(time.time()-t))
    for em in summ:
        rec, e = NP.LAST_TRACKS[em["name"]]
        print("==", em["name"], "frames", len(rec.frames), "maxalive", rec.max_alive, "trunc", rec.truncated)
        for rtype, props in NP.renderer_exports(system, e):
            print("   R", rtype.replace("Niagara","").replace("RendererProperties",""), {k: (v.get("RootName") if isinstance(v, dict) else v) for k, v in props.items() if (k.endswith("Binding") and isinstance(v, dict) and v.get("RootName")) or k in ("RendererVisibility","MaterialParamValidMask","bIsEnabled","FacingMode","Alignment")})
