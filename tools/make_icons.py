from PIL import Image, ImageDraw
import os

out = r"D:\DSH\Software\control-hub\wwwroot"

def make(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 背景：圓角深色方块
    pad = size * 0.06
    r = size * 0.22
    d.rounded_rectangle([pad, pad, size - pad, size - pad], radius=r, fill=(17, 24, 33, 255),
                        outline=(37, 99, 235, 255), width=max(2, int(size * 0.018)))
    # 2x2 按钮网格
    gap = size * 0.055
    inner = size * 0.20
    cell = (size - inner * 2 - gap) / 2
    glow = [(37, 99, 235), (34, 197, 94), (168, 85, 247), (245, 158, 11)]
    for i in range(4):
        cx = inner + (i % 2) * (cell + gap)
        cy = inner + (i // 2) * (cell + gap)
        rr = cell * 0.26
        d.rounded_rectangle([cx, cy, cx + cell, cy + cell], radius=rr, fill=glow[i] + (255,))
        # 高光
        d.rounded_rectangle([cx + cell*0.18, cy + cell*0.18, cx + cell*0.82, cy + cell*0.42],
                            radius=rr*0.6, fill=(255, 255, 255, 46))
    img.save(os.path.join(out, f"icon-{size}.png"))
    print("wrote", size)

make(192)
make(512)
