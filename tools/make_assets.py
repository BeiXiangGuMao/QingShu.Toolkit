#!/usr/bin/env python3
"""青枢工具箱 · 应用图标资产生成（源图：tools/logo-source.png）

源图为设计稿导出的深色圆角底 + 薄荷绿渐变 Q 图形（仓库内存 512px 采样版，
自带圆角透明通道）。重生成全部资产：python3 tools/make_assets.py
输出：app.ico（16-256 多尺寸）/ AppIcon.png / Square44x44 / Square150x150 /
     StoreLogo / Wide310x150（透明底居中 150 图）
"""
from PIL import Image
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "QingShu.Toolkit" / "Assets"
OUT.mkdir(parents=True, exist_ok=True)
SRC = Path(__file__).resolve().parent / "logo-source.png"


def render_icon(size: int) -> Image.Image:
    img = Image.open(SRC).convert("RGBA")
    return img.resize((size, size), Image.LANCZOS)


# ICO（多尺寸；exe 图标 + 标题栏 AppWindow.SetIcon 共用）
base = render_icon(256)
base.save(OUT / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

# PNG 资产（标题栏 UI 图 + MSIX/Packaging 清单引用）
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
