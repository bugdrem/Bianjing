import zipfile, sys, os

src = r"E:\迅雷下载\kenney_nature-kit.zip"
if not os.path.exists(src):
    print("NOT FOUND:", src)
    sys.exit(1)

z = zipfile.ZipFile(src)
names = z.namelist()
print("总条目:", len(names))

models = [n for n in names if n.lower().endswith((".glb", ".gltf"))]
print("模型文件(.glb/.gltf):", len(models))

trees = [n for n in models if "tree" in n.lower()]
print("\n=== 树相关模型 ===")
for n in sorted(trees):
    info = z.getinfo(n)
    print(f"{info.file_size/1024:8.1f} KB  {n}")

print("\n=== 前 25 个模型（看目录结构）===")
for n in sorted(models)[:25]:
    print(" ", n)
