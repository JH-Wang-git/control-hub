import json, io
for path in [r"D:\DSH\Software\control-hub\config.json", r"D:\DSH\Software\control-hub\dist\config.json"]:
    try:
        with io.open(path, encoding="utf-8") as f:
            cfg = json.load(f)
    except Exception as e:
        print(path, "ERR", e); continue
    tok = cfg.get("token")
    st = [p for p in cfg["pages"] if p.get("kind") == "status"]
    print(path, "token=", tok, "pages=", [(p["id"], p.get("kind"), p.get("rows"), p.get("cols"), len(p.get("widgets") or [])) for p in cfg["pages"]])