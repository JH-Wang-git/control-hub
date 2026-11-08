import json, io
W = [
 dict(id="w1", kind="clock",   row=0, col=0, rowSpan=2, colSpan=2),
 dict(id="w2", kind="date",    row=0, col=2, rowSpan=2, colSpan=2),
 dict(id="w3", kind="cpu",     row=2, col=0, rowSpan=2, colSpan=2),
 dict(id="w4", kind="mem",     row=2, col=2, rowSpan=2, colSpan=2),
 dict(id="w5", kind="gpu",     row=4, col=0, rowSpan=2, colSpan=2),
 dict(id="w6", kind="gputemp", row=4, col=2, rowSpan=2, colSpan=2),
 dict(id="w7", kind="cputemp", row=6, col=0, rowSpan=1, colSpan=2),
 dict(id="w8", kind="fps",     row=6, col=2, rowSpan=1, colSpan=2),
 dict(id="w9", kind="app",     row=7, col=0, rowSpan=1, colSpan=4),
]
for path in [r"D:\DSH\Software\control-hub\config.json", r"D:\DSH\Software\control-hub\dist\config.json"]:
    with io.open(path, encoding="utf-8") as f:
        cfg = json.load(f)
    for p in cfg["pages"]:
        if p.get("kind") == "status":
            p["rows"] = 8; p["cols"] = 4; p["widgets"] = W
    with io.open(path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    print("patched", path)