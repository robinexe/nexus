#!/usr/bin/env python3
"""Gera o ícone do NEXUS a partir de tools/packaging/icon/nexus.svg (fonte única).

Saídas (versionadas no repositório; o CI não precisa regenerar):
  src/Nexus.Desktop/Assets/nexus.ico          16, 20, 24, 32, 40, 48, 64, 128 e 256 px (PNG dentro do ICO)
  src/Nexus.Desktop/Assets/nexus-256.png      prévia
  tools/packaging/icon/wizard-small-*.bmp     imagem pequena do instalador (55 e 110 px; Inno Setup)

Requisitos: rsvg-convert (librsvg; no Debian/Ubuntu: apt install librsvg2-bin; no Windows: MSYS2 ou choco) e Pillow.
Uso: python3 tools/packaging/icon/build_icon.py      (rode da raiz do repositório ou de qualquer pasta)

Reprodutível: cada tamanho é rasterizado direto do SVG (não reduzido de uma imagem grande) e gravado sem metadados.
"""
from __future__ import annotations

import io
import shutil
import struct
import subprocess
import sys
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
SVG = HERE / "nexus.svg"
ASSETS = ROOT / "src" / "Nexus.Desktop" / "Assets"
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
WIZARD_SIZES = [55, 110]
BACKGROUND = (0x0E, 0x13, 0x20)


def render(size: int) -> Image.Image:
    exe = shutil.which("rsvg-convert")
    if exe is None:
        sys.exit("rsvg-convert não encontrado (pacote librsvg2-bin).")
    png = subprocess.run([exe, "-w", str(size), "-h", str(size), str(SVG)], check=True, capture_output=True).stdout
    img = Image.open(io.BytesIO(png))
    img.load()
    return img.convert("RGBA")


def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)  # Pillow não grava data/hora: saída estável
    return buf.getvalue()


def write_ico(path: Path, images: list[Image.Image]) -> None:
    """ICO com entradas PNG (aceito pelo Windows Vista+ e pelo compilador de recursos do .NET)."""
    blobs = [png_bytes(i) for i in images]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for img, blob in zip(images, blobs):
        w, h = img.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    images = [render(s) for s in ICO_SIZES]
    write_ico(ASSETS / "nexus.ico", images)
    (ASSETS / "nexus-256.png").write_bytes(png_bytes(images[-1]))
    for s in WIZARD_SIZES:
        # O Inno Setup usa BMP sem transparência: compõe sobre o fundo do ícone.
        flat = Image.new("RGB", (s, s), BACKGROUND)
        icon = render(s)
        flat.paste(icon, (0, 0), icon)
        flat.save(HERE / f"wizard-small-{s}.bmp", format="BMP")
    print(f"ok: {ASSETS / 'nexus.ico'} ({', '.join(map(str, ICO_SIZES))} px) e imagens do instalador")


if __name__ == "__main__":
    main()
