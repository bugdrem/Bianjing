import zipfile, json, struct

src = r"E:\迅雷下载\kenney_nature-kit.zip"
z = zipfile.ZipFile(src)


def glb_json(data):
    """解析 GLB 的 JSON chunk（GLB = 12 字节头 + [长度,类型,数据] 块序列）。"""
    magic, _ver, length = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67, "not a GLB"
    off = 12
    while off + 8 <= length:
        clen, ctype = struct.unpack_from("<II", data, off)
        cdata = data[off + 8: off + 8 + clen]
        if ctype == 0x4E4F534A:  # 'JSON'
            return json.loads(cdata.decode("utf-8"))
        off += 8 + clen
    return None


targets = [
    "Models/GLTF format/tree_default.glb",
    "Models/GLTF format/tree_oak.glb",
    "Models/GLTF format/tree_pineDefaultA.glb",
    "Models/GLTF format/tree_plateau.glb",
    "Models/GLTF format/tree_fat.glb",
]

for name in targets:
    data = z.read(name)
    j = glb_json(data)
    print("=" * 66)
    print(f"{name}  ({len(data)/1024:.1f} KB)")
    print(f"  nodes={len(j.get('nodes', []))}  images={len(j.get('images', []))}  textures={len(j.get('textures', []))}")

    for i, m in enumerate(j.get("meshes", [])):
        for p in m.get("primitives", []):
            attrs = list(p.get("attributes", {}).keys())
            print(f"  mesh[{i}] attrs={attrs} material={p.get('material')}")

    for i, m in enumerate(j.get("materials", [])):
        pbr = m.get("pbrMetallicRoughness", {})
        has_tex = "baseColorTexture" in pbr
        print(f"  mat[{i}] baseColorTexture={'YES' if has_tex else 'no'}  baseColorFactor={pbr.get('baseColorFactor')}")

    # 节点层级（确认是否有多层级变换需要合并）
    for i, n in enumerate(j.get("nodes", [])):
        print(f"  node[{i}] name={n.get('name')!r} mesh={n.get('mesh')} children={n.get('children')} "
              f"T={n.get('translation')} S={n.get('scale')}")
