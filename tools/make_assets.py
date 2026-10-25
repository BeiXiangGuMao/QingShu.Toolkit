#!/usr/bin/env python3
"""青枢工具箱 · 应用图标资产生成（圆角渐变蓝底 + 白色“枢”字）"""
from PIL import Image, ImageDraw, ImageFont
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "src" / "QingShu.Toolkit" / "Assets"
OUT.mkdir(parents=True, exist_ok=True)

# 字体：优先粗体中文
candidates = [
    "/usr/share/fonts/truetype/chinese/NotoSansSC-Bold.ttf",
    "/usr/share/fonts/truetype/chinese/NotoSansSC-Regular.ttf",
    "/usr/share/fonts/truetype/noto-serif-sc/NotoSerifSC-Bold.otf",
]
font_path = next((p for p in candidates if Path(p).exists()), None)
if font_path is None:
    import subprocess
    out = subprocess.run(["fc-list"], capture_output=True, text=True).stdout
    for line in out.splitlines():
        if "Noto Sans SC" in line or "CJK" in line:
            font_path = line.split(":")[1].strip() if ":" in line else None
            if font_path:
                break

def render_icon(size: int) -> Image.Image:
    S = 8  # 超采样
    big = size * S
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 圆角渐变底 #2563EB → #3B82F6
    top, bottom = (37, 99, 235), (59, 130, 246)
    grad = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(big):
        t = y / max(1, big - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        gd.line([(0, y), (big, y)], fill=c)
    mask = Image.new("L", (big, big), 0)
    md = ImageDraw.Draw(mask)
    radius = int(big * 0.22)
    md.rounded_rectangle([0, 0, big - 1, big - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    # 高光弧线（右上）
    hl = ImageDraw.Draw(img)
    hl.arc([int(big*0.45), int(-big*0.35), int(big*1.35), int(big*0.55)], 0, 140,
           fill=(255, 255, 255, 60), width=int(big * 0.045))
    # 主字“枢”
    font = ImageFont.truetype(font_path, int(big * 0.56)) if font_path else ImageFont.load_default()
    bbox = d.textbbox((0, 0), "枢", font=font)
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text(((big - w) / 2 - bbox[0], (big - h) / 2 - bbox[1]), "枢",
           font=font, fill=(255, 255, 255, 255))
    # 左下角小信号弧（科技感）
    for i, r in enumerate((0.30, 0.42, 0.54)):
        hl.arc([int(big*0.12), int(big*(1.0 - r*0.55)), int(big*0.12 + big*r), int(big*1.02)],
               200, 320, fill=(255, 255, 255, 70), width=int(big * 0.02))
    return img.resize((size, size), Image.LANCZOS)

# ICO（多尺寸）
base = render_icon(256)
base.save(OUT / "app.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])

# PNG 资产
render_icon(256).save(OUT / "AppIcon.png")
render_icon(150).save(OUT / "Square150x150Logo.png")
render_icon(44).save(OUT / "Square44x44Logo.png")
render_icon(50).save(OUT / "StoreLogo.png")
# Wide 310x150（透明背景上居中放 150 图）
wide = Image.new("RGBA", (310, 150), (0, 0, 0, 0))
icon150 = render_icon(150)
wide.paste(icon150, (80, 0), icon150)
wide.save(OUT / "Wide310x150Logo.png")

print("assets generated at", OUT)
for f in sorted(OUT.iterdir()):
    print(" -", f.name, f.stat().st_size, "bytes")
