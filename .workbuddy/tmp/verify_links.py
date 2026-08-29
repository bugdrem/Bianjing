import urllib.request, re, sys

UA = {"User-Agent": "Mozilla/5.0"}


def head(url):
    try:
        req = urllib.request.Request(url, headers=UA, method="HEAD")
        with urllib.request.urlopen(req, timeout=40) as r:
            size = r.headers.get("Content-Length")
            return f"HTTP {r.status}  {(int(size)/1048576):.1f} MB" if size else f"HTTP {r.status}"
    except Exception as e:
        return f"失败: {e}"


def links(url, pat):
    try:
        req = urllib.request.Request(url, headers=UA)
        html = urllib.request.urlopen(req, timeout=40).read().decode("utf-8", "replace")
        out = []
        for m in re.findall(pat, html, re.I):
            if m not in out:
                out.append(m)
        return out
    except Exception as e:
        return [f"(抓取失败: {e})"]


print("== 1) OpenGameArt 直链验证 ==")
u1 = "https://opengameart.org/sites/default/files/stylized_nature_megakitstandard.zip"
print(f"  {head(u1)}\n  {u1}")

print("\n== 2) Quaternius 官网页面的 zip 链接 ==")
print("页面: https://quaternius.com/packs/stylizednaturemegakit.html")
for l in links("https://quaternius.com/packs/stylizednaturemegakit.html",
               r'https?://[^\s"\']+\.zip'):
    print(f"  {l}  ->  {head(l) if l.startswith('http') else ''}")

print("\n== 3) KayKit 官网（自然/森林相关下载链接）==")
for l in links("https://kaylousberg.com/", r'https?://[^\s"\']+\.(?:zip|rar|7z)')[:12]:
    print(f"  {l}")
