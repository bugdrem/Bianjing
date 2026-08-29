import zipfile, os, shutil

src = r"E:\迅雷下载\kenney_nature-kit.zip"
dst_dir = r"D:\Godot\Bianjing\assets\trees"
os.makedirs(dst_dir, exist_ok=True)

z = zipfile.ZipFile(src)

# 三个树种：阔叶 / 针叶(松) / 果树
picks = {
    "Models/GLTF format/tree_default.glb": "tree_default.glb",        # 阔叶
    "Models/GLTF format/tree_pineDefaultA.glb": "tree_pineDefaultA.glb",  # 针叶松
    "Models/GLTF format/tree_plateau.glb": "tree_plateau.glb",        # 果树（造型与阔叶区分）
}

for inner, out_name in picks.items():
    out_path = os.path.join(dst_dir, out_name)
    with z.open(inner) as f, open(out_path, "wb") as o:
        shutil.copyfileobj(f, o)
    print(f"OK  {out_name}  ({os.path.getsize(out_path)/1024:.1f} KB)")

# 顺带把授权文件放进 assets（CC0 虽不强制署名，但留档便于追溯来源）
lic = [n for n in z.namelist() if "license" in n.lower() or "licence" in n.lower()]
print("\n包内授权文件:", lic)
for n in lic:
    base = os.path.basename(n)
    if not base:
        continue
    out_path = os.path.join(r"D:\Godot\Bianjing\assets", "kenney_" + base)
    with z.open(n) as f, open(out_path, "wb") as o:
        shutil.copyfileobj(f, o)
    print(f"OK  授权 -> assets/kenney_{base}")
