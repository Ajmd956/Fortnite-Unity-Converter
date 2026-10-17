import re
import sys


def split_polys(idx):
    polys, cur = [], []
    for i in idx:
        if i < 0:
            cur.append(~i)
            polys.append(cur)
            cur = []
        else:
            cur.append(i)
    return polys


def patch(text):
    if "LayerElementMaterial" in text:
        return text, 0
    m = re.search(r"PolygonVertexIndex: \*\d+ \{\s*a: ([^}]*)\}", text)
    if not m:
        raise SystemExit("no PolygonVertexIndex in this file")
    idx = [int(x) for x in m.group(1).replace("\n", "").replace("\t", "").split(",") if x.strip()]
    polys = split_polys(idx)
    parent = {}

    def find(a):
        parent.setdefault(a, a)
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for p in polys:
        for v in p[1:]:
            parent[find(v)] = find(p[0])
    order = []
    slot_of_root = {}
    for p in polys:
        r = find(p[0])
        if r not in slot_of_root:
            slot_of_root[r] = len(order)
            order.append(r)
    count = len(order)
    if count < 2:
        return text, count
    slots = [slot_of_root[find(p[0])] for p in polys]
    layer = (
        "\t\tLayerElementMaterial: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"\"\n"
        "\t\t\tMappingInformationType: \"ByPolygon\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n"
        "\t\t\tMaterials: *%d {\n\t\t\t\ta: %s\n\t\t\t}\n\t\t}\n" % (len(slots), ",".join(map(str, slots)))
    )
    text = text.replace("\t\tLayer: 0 {", layer + "\t\tLayer: 0 {", 1)
    text = text.replace(
        "\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementUV\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n",
        "\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementUV\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n"
        "\t\t\tLayerElement:  {\n\t\t\t\tType: \"LayerElementMaterial\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n",
        1,
    )
    mats = ""
    for k in range(count):
        mats += (
            "\tMaterial: %d, \"Material::Slot%d\", \"\" {\n\t\tVersion: 102\n\t\tShadingModel: \"lambert\"\n"
            "\t\tMultiLayer: 0\n\t\tProperties70:  {\n\t\t\tP: \"DiffuseColor\", \"Color\", \"\", \"A\",0.8,0.8,0.8\n\t\t}\n\t}\n"
            % (2000001 + k, k)
        )
    text = text.replace("}\nConnections:  {", mats + "}\nConnections:  {", 1)
    model = re.search(r"Model: (\d+), \"Model::", text).group(1)
    conns = "".join("\tC: \"OO\",%d,%s\n" % (2000001 + k, model) for k in range(count))
    i = text.rfind("}")
    text = text[:i] + conns + text[i:]
    text = text.replace("\tCount: 3\n", "\tCount: 4\n", 1)
    text = text.replace(
        "\tObjectType: \"Geometry\" {\n\t\tCount: 1\n\t}\n",
        "\tObjectType: \"Geometry\" {\n\t\tCount: 1\n\t}\n\tObjectType: \"Material\" {\n\t\tCount: %d\n\t}\n" % count,
        1,
    )
    return text, count


if __name__ == "__main__":
    for path in sys.argv[1:]:
        src = open(path, encoding="utf-8").read()
        out, n = patch(src)
        if n >= 2 and out != src:
            open(path + ".bak", "w", encoding="utf-8").write(src)
            open(path, "w", encoding="utf-8").write(out)
            print("%s: %d material slots written" % (path, n))
        else:
            print("%s: unchanged (%d separate pieces)" % (path, n))
