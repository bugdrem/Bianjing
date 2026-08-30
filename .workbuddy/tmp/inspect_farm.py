import zipfile, json, struct, os, sys

src = r"E:\Users\bugdream\Downloads\Farm Animal Pack-glb.zip"
if not os.path.exists(src):
    print("NOT FOUND:", src)
    sys.exit(1)
print("文件存在，大小:", f"{os.path.getsize(src)/1048576:.1f} MB")

z = zipfile.ZipFile(src)
models = [n for n in z.namelist() if n.lower().endswith((".glb", ".gltf"))]
print(f"总条目 {len(z.namelist())}，模型 {len(models)} 个：")


def glb_json(data):
    magic, _v, length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67:
        return None
    off = 12
    while off + 8 <= length:
        clen, ctype = struct.unpack_from("<II", data, off)
        if ctype == 0x4E4F534A:  # 'JSON'
            return json.loads(data[off + 8: off + 8 + clen].decode("utf-8", "replace"))
        off += 8 + clen
    return None


for n in sorted(models):
    print(f"  {z.getinfo(n).file_size/1024:8.1f} KB  {n}")

print("\n=== 前 6 个模型的结构（顶点色 or 贴图？面数规模？）===")
for n in sorted(models)[:6]:
    j = glb_json(z.read(n))
    if j is None:
        print(f"  {n}: 非GLB或解析失败")
        continue
    tris = 0
    for m in j.get("meshes", []):
        for p in m.get("primitives", []):
            attrs = list(p.get("attributes", {}).keys())
            ai = p.get("indices")
            cnt = j["accessors"][ai]["count"] if ai is not None else j["accessors"][p["attributes"]["POSITION"]]["count"]
            tris += cnt // 3
    mats = []
    for m in j.get("materials", []):
        pbr = m.get("pbrMetallicRoughness", {})
        mats.append(("TEX" if "baseColorTexture" in pbr else "color", pbr.get("baseColorFactor")))
    print(f"  {os.path.basename(n)}: tris≈{tris}  images={len(j.get('images', []))}  materials={mats}")
