#!/usr/bin/env python3
"""从生成的图标 PNG 裁剪出图标本体，产出 ico / icns 源 / Avalonia 窗口图标。"""
import os
from PIL import Image

SRC = "/Users/zhangyucheng/WorkBuddy/2026-09-08-15-19-03/BaoWuLearn/design/Modern_flat_application_icon_d_2026-09-11T09-00-55.png"
OUT_DIR = "/Users/zhangyucheng/WorkBuddy/2026-09-08-15-19-03/BaoWuLearn/design"
ICONSET = os.path.join(OUT_DIR, "app.iconset")

img = Image.open(SRC).convert("RGB")
W, H = img.size
print(f"source: {W}x{H}")

# 1) 找图标本体 bounding box：背景是浅灰(~217)，与背景差异明显的像素即图标
px = img.load()
bg = px[5, 5]
print(f"corner bg color: {bg}")

minx, miny, maxx, maxy = W, H, 0, 0
step = 2  # 采样步长足够了
for y in range(0, H, step):
    for x in range(0, W, step):
        # 跳过右下角水印区（"AI生成 WORKBUDDY" 字样）
        if x > 780 and y > 930:
            continue
        c = px[x, y]
        if abs(c[0]-bg[0]) + abs(c[1]-bg[1]) + abs(c[2]-bg[2]) > 30:
            if x < minx: minx = x
            if y < miny: miny = y
            if x > maxx: maxx = x
            if y > maxy: maxy = y

print(f"raw bbox: ({minx},{miny})-({maxx},{maxy}) size={maxx-minx}x{maxy-miny}")

# 2) 取正方形（以宽为准），居中
side = maxx - minx
cx, cy = (minx + maxx) // 2, (miny + maxy) // 2
half = side // 2
box = (cx - half, cy - half, cx - half + side, cy - half + side)
icon = img.crop(box)
print(f"cropped: {icon.size} box={box}")

# 3) 缩放出 1024 母版
master = icon.resize((1024, 1024), Image.LANCZOS)
master_png = os.path.join(OUT_DIR, "icon-1024.png")
master.save(master_png)

# 4) Avalonia 窗口图标（256）
icon_dir = "/Users/zhangyucheng/WorkBuddy/2026-09-08-15-19-03/BaoWuLearn/src/BaoWuLearn.Desktop/Assets"
os.makedirs(icon_dir, exist_ok=True)
master.resize((256, 256), Image.LANCZOS).save(os.path.join(icon_dir, "appicon.png"))

# 5) Windows ico（多尺寸）
ico_sizes = [256, 128, 64, 48, 32, 16]
master.save(os.path.join(icon_dir, "app.ico"), format="ICO",
            sizes=[(s, s) for s in ico_sizes])

# 6) macOS iconset
os.makedirs(ICONSET, exist_ok=True)
for s in [16, 32, 64, 128, 256, 512]:
    master.resize((s, s), Image.LANCZOS).save(os.path.join(ICONSET, f"icon_{s}x{s}.png"))
    if s <= 512:
        master.resize((s*2, s*2), Image.LANCZOS).save(os.path.join(ICONSET, f"icon_{s}x{s}@2x.png"))

print("done:")
print(" ", master_png)
print(" ", os.path.join(icon_dir, "appicon.png"))
print(" ", os.path.join(icon_dir, "app.ico"))
print(" ", ICONSET)
