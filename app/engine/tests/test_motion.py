import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import NiagaraPorter
import PropBlueprint


def ids(n):
    return np.array([list(range(n)), [7] * n], dtype=np.int64)


def check_still_particles():
    m = NiagaraPorter.Motion()
    pos = np.array([[1.0, 2.0], [0.0, 0.0], [0.0, 0.0]])
    quat = np.array([[0.0, 0.0], [0.0, 0.0], [0.0, 0.0], [1.0, 1.0]])
    for _ in range(10):
        m.feed(ids(2), pos, quat, None)
    s = m.summary()
    assert s["driftCm"] == 0.0 and s["spinDeg"] == 0.0 and s["movedParticles"] == 0 and s["tracked"] == 2, s


def check_moving_and_spinning():
    m = NiagaraPorter.Motion()
    quat0 = np.array([[0.0], [0.0], [0.0], [1.0]])
    half = np.radians(90.0) / 2.0
    quat1 = np.array([[0.0], [0.0], [np.sin(half)], [np.cos(half)]])
    m.feed(ids(1), np.array([[0.0], [0.0], [0.0]]), quat0, None)
    m.feed(ids(1), np.array([[30.0], [40.0], [0.0]]), quat1, None)
    s = m.summary()
    assert abs(s["driftCm"] - 50.0) < 1e-6, s
    assert abs(s["spinDeg"] - 90.0) < 1e-3, s
    assert s["movedParticles"] == 1, s


def check_sprite_rotation_and_missing_ids():
    m = NiagaraPorter.Motion()
    m.feed(None, None, None, None)
    m.feed(ids(1), None, None, np.array([[10.0]]))
    m.feed(ids(1), None, None, np.array([[75.0]]))
    assert abs(m.summary()["spinDeg"] - 65.0) < 1e-9


def check_spin_rate():
    m = NiagaraPorter.Motion()
    for k in range(6):
        half = np.radians(90.0 * k * NiagaraPorter.DT) / 2.0
        quat = np.array([[0.0], [0.0], [np.sin(half)], [np.cos(half)]])
        m.feed(ids(1), None, quat, None)
    rate = m.summary()["spinRateDegPerSec"]
    assert abs(rate - 90.0) < 0.5, rate


def check_slots_without_ids():
    m = NiagaraPorter.Motion()
    two = np.array([[0.0, 5.0], [0.0, 0.0], [0.0, 0.0]])
    m.feed(None, two, None, None, 2)
    m.feed(None, two, None, None, 2)
    assert m.summary()["driftCm"] == 0.0
    one = np.array([[5.0], [0.0], [0.0]])
    m.feed(None, one, None, None, 1)
    assert m.summary()["driftCm"] == 0.0, m.summary()
    m.feed(None, np.array([[25.0], [0.0], [0.0]]), None, None, 1)
    assert abs(m.summary()["driftCm"] - 20.0) < 1e-9, m.summary()


def check_notes():
    PropBlueprint.report[:] = []
    PropBlueprint.motion_notes({"name": "A", "motion": {"driftCm": 0.0, "spinDeg": 0.0, "movedParticles": 0, "tracked": 5, "explainedCm": 0.0}})
    PropBlueprint.motion_notes({"name": "B", "motion": {"driftCm": 40.0, "spinDeg": 0.0, "movedParticles": 4, "tracked": 5, "explainedCm": 38.0}})
    assert PropBlueprint.report == [], PropBlueprint.report
    PropBlueprint.motion_notes({"name": "C", "motion": {"driftCm": 120.0, "spinDeg": 180.0, "movedParticles": 5, "tracked": 5, "explainedCm": 0.0}})
    assert len(PropBlueprint.report) == 2 and "C:" in PropBlueprint.report[0], PropBlueprint.report
    PropBlueprint.report[:] = []
    PropBlueprint.motion_notes({"name": "D"})
    assert PropBlueprint.report == []


def check_dump(path):
    dump = json.load(open(path, encoding="utf-8-sig"))
    name = os.path.basename(path).split(".")[0]
    system, emitters, skipped = NiagaraPorter.replay(NiagaraPorter.to_exports(dump), name, path, 6.0)
    for em in emitters:
        print(em["name"], em["motion"])
    return {em["name"]: em["motion"] for em in emitters}


def check_missing_vector_field():
    from niagara_core import niagara
    class Sys:
        fields = {}
        field_gaps = {}
    sysm = Sys()
    vf = niagara.VectorField(sysm, "NiagaraDataInterfaceVectorField", {"Field": {"ObjectPath": "/Niagara/VectorFields/Assets/TilingCurl32.0"}})
    vf.caller = "NE_Sprites"
    out = vf.function("SampleField", [], 3, 3)(4, [np.zeros(4)] * 3)
    assert len(out) == 3 and all(float(np.abs(o).sum()) == 0.0 for o in out)
    assert sysm.field_gaps == {"NE_Sprites": {"TilingCurl32"}}, sysm.field_gaps


if __name__ == "__main__":
    check_missing_vector_field()
    check_still_particles()
    check_moving_and_spinning()
    check_sprite_rotation_and_missing_ids()
    check_spin_rate()
    check_slots_without_ids()
    check_notes()
    print("synthetic checks passed")
    if len(sys.argv) > 1:
        check_dump(sys.argv[1])
