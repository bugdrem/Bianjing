import json, urllib.request, sys

BASE = "https://godotengine.org/asset-library/api/asset"

def fetch(filter_kw, max_results=15):
    url = f"{BASE}?filter={filter_kw}&max_results={max_results}&sort=updated"
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.load(r)

for kw in ["tree", "nature", "vegetation", "forest", "plant"]:
    try:
        data = fetch(kw)
        items = data.get("result", data if isinstance(data, list) else [])
    except Exception as e:
        print(f"## {kw}: 查询失败 {e}")
        continue
    print(f"\n===== filter={kw}（{len(items)} 条）=====")
    for it in items:
        print(f"- {it.get('title')}  | 作者: {it.get('author')}  | 授权: {it.get('cost')}"
              f"  | godot: {it.get('godot_version')}  | 下载源: {it.get('download_provider')}")
        print(f"    id={it.get('asset_id')}  {it.get('download_url')}")
