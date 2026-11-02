#!/usr/bin/env python3
"""Notas de versão tiradas do CHANGELOG.md (usado pelo .github/workflows/release.yml).

    python tools/packaging/release_notes.py --version 1.0.0 --short   # uma linha: aviso de atualização ("notes" do manifesto)
    python tools/packaging/release_notes.py --version 1.0.0 --full    # a seção inteira: corpo do GitHub Release
    ... --out notas.md   # grava o texto num arquivo UTF-8 (no Windows, evita a página de código do console)

A seção é "## <X.Y.Z>" (o sufixo -ci.N / -rc.N é ignorado: uma 1.0.0-ci.7 usa as notas da 1.0.0). O resumo curto é a linha
que começa com ">" dentro da seção. Sem seção ou sem resumo: código 3 e nada na saída (o workflow usa um texto padrão).
Só biblioteca padrão; lê e escreve UTF-8.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SHORT_MAX = 300  # o manifesto aceita até 2000; o aviso na janela é uma faixa curta


def section(text: str, version: str) -> str | None:
    core = re.match(r"v?(\d+\.\d+\.\d+)", version.strip())
    if not core:
        return None
    lines = text.splitlines()
    start = None
    for i, line in enumerate(lines):
        if re.fullmatch(rf"##\s+v?{re.escape(core.group(1))}\b.*", line.strip()):
            start = i + 1
            break
    if start is None:
        return None
    end = next((j for j in range(start, len(lines)) if lines[j].startswith("## ")), len(lines))
    body = "\n".join(lines[start:end]).strip()
    return body or None


def short(body: str) -> str | None:
    for line in body.splitlines():
        if line.startswith(">"):
            s = line.lstrip(">").strip()
            return s[:SHORT_MAX] if s else None
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", required=True)
    ap.add_argument("--changelog", default=str(ROOT / "CHANGELOG.md"))
    ap.add_argument("--out", help="arquivo de saída (UTF-8); sem ele, imprime")
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--short", action="store_true")
    mode.add_argument("--full", action="store_true")
    args = ap.parse_args()

    body = section(Path(args.changelog).read_text(encoding="utf-8-sig"), args.version)
    out = None if body is None else (short(body) if args.short else body)
    if not out:
        return 3
    if args.out:
        Path(args.out).write_text(out + "\n", encoding="utf-8", newline="\n")
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
