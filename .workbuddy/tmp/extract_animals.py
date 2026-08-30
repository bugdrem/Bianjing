import zipfile, os, shutil

src = r"E:\Users\bugdream\Downloads\Farm Animal Pack-glb.zip"
dst = r"D:\Godot\Bianjing\assets\animals"
os.makedirs(dst, exist_ok=True)

z = zipfile.ZipFile(src)
n = 0
for info in z.infolist():
    if info.filename.lower().endswith(".glb"):
        out = os.path.join(dst, os.path.basename(info.filename))
        with z.open(info) as f, open(out, "wb") as o:
            shutil.copyfileobj(f, o)
        print(f"OK  {os.path.basename(info.filename)}  ({info.file_size/1024:.1f} KB)")
        n += 1
print(f"共提取 {n} 个模型 -> {dst}")
