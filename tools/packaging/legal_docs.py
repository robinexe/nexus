#!/usr/bin/env python3
"""Termos de uso e Política de privacidade (docs/legal/*.md) em .txt e .html para a pasta publicada.

    python tools/packaging/legal_docs.py <pasta>     # grava <pasta>/legal/{TERMOS-DE-USO,PRIVACIDADE}.{txt,html}

- .txt: UTF-8 com BOM e CRLF (o Inno Setup mostra TERMOS-DE-USO.txt na página de licença do instalador; o Bloco de Notas
  abre certo).
- .html: página única, sem script nem recurso externo, para o NEXUS abrir no navegador (Ajustes → Sobre).

Converte só o Markdown que esses dois arquivos usam (títulos, parágrafos, listas, tabelas, **negrito**, `código`). Só
biblioteca padrão. Usado por tools/packaging/publish.py.
"""
from __future__ import annotations

import html
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOCS = [("TERMOS-DE-USO", "Termos de uso do NEXUS"), ("PRIVACIDADE", "Política de privacidade do NEXUS")]


def _cells(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def _is_rule(line: str) -> bool:
    return bool(re.fullmatch(r"\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?", line.strip()))


def _plain(s: str) -> str:
    return re.sub(r"\*\*(.+?)\*\*", r"\1", s, flags=re.DOTALL).replace("`", "")


def to_text(md: str) -> str:
    out: list[str] = []
    header: list[str] = []
    for line in md.splitlines():
        if line.startswith("#"):
            title = _plain(line.lstrip("#").strip())
            level = len(line) - len(line.lstrip("#"))
            out.append(title.upper() if level == 1 else title)
            if level <= 2:
                out.append(("=" if level == 1 else "-") * len(title))
            continue
        if line.lstrip().startswith("|"):
            if _is_rule(line):
                continue
            cells = [_plain(c) for c in _cells(line)]
            if not header:
                header = cells
                continue
            out.append("- " + "; ".join(f"{h}: {c}" if h else c for h, c in zip(header, cells)))
            continue
        header = []
        out.append(line)
    text = _plain("\r\n".join(out)).strip() + "\r\n"  # negrito pode atravessar a quebra de linha
    return re.sub(r"(\r\n){3,}", "\r\n\r\n", text)


def _inline(s: str) -> str:
    s = html.escape(s, quote=False)
    s = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", s)
    return re.sub(r"`([^`]+)`", r"<code>\1</code>", s)


def to_html(md: str, title: str) -> str:
    body: list[str] = []
    para: list[str] = []
    items: list[str] = []  # itens da lista em texto cru (o negrito pode atravessar a quebra de linha)
    table: list[list[str]] = []

    def flush() -> None:
        if para:
            body.append("<p>" + _inline(" ".join(para)) + "</p>")
            para.clear()
        if items:
            body.append("<ul>" + "".join(f"<li>{_inline(i)}</li>" for i in items) + "</ul>")
            items.clear()
        if table:
            head, *rows = table
            body.append("<table><thead><tr>" + "".join(f"<th>{_inline(c)}</th>" for c in head) + "</tr></thead><tbody>"
                        + "".join("<tr>" + "".join(f"<td>{_inline(c)}</td>" for c in r) + "</tr>" for r in rows)
                        + "</tbody></table>")
            table.clear()

    for raw in md.splitlines():
        line = raw.rstrip()
        if not line.strip():
            flush()
        elif line.startswith("#"):
            flush()
            level = min(len(line) - len(line.lstrip("#")), 3)
            body.append(f"<h{level}>{_inline(line.lstrip('#').strip())}</h{level}>")
        elif line.lstrip().startswith("|"):
            if para or items:
                flush()
            if not _is_rule(line):
                table.append(_cells(line))
        elif line.startswith("- "):
            if para or table:
                flush()
            items.append(line[2:].strip())
        elif items and line.startswith("  "):
            items[-1] += " " + line.strip()
        else:
            if items or table:
                flush()
            para.append(line.strip())
    flush()
    css = ("body{font:15px/1.6 'Segoe UI',system-ui,sans-serif;max-width:860px;margin:0 auto;padding:24px 16px;"
           "background:#0E1320;color:#E6EAF2}h1,h2,h3{line-height:1.25}h1{color:#FFB547}h2{margin-top:2em;color:#FFB547}"
           "a{color:#5CD6FF}code{background:#1A2133;padding:1px 5px;border-radius:4px}"
           "table{border-collapse:collapse;width:100%;margin:12px 0}th,td{border:1px solid #2A3348;padding:6px 8px;"
           "text-align:left;vertical-align:top}th{background:#1A2133}"
           "@media (prefers-color-scheme: light){body{background:#fff;color:#1A1F2B}h1,h2{color:#8A5A00}"
           "code,th{background:#F1F3F7}th,td{border-color:#D5DAE3}}")
    return ("<!doctype html>\n<html lang=\"pt-BR\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            f"<title>{html.escape(title)}</title><style>{css}</style></head><body>\n"
            + "\n".join(body) + "\n</body></html>\n")


def write(target: Path) -> list[Path]:
    """Grava os quatro arquivos em <target>/legal e devolve os caminhos."""
    out_dir = target / "legal"
    out_dir.mkdir(parents=True, exist_ok=True)
    written = []
    for stem, title in DOCS:
        md = (ROOT / "docs" / "legal" / f"{stem}.md").read_text(encoding="utf-8-sig")
        txt = out_dir / f"{stem}.txt"
        txt.write_bytes(b"\xef\xbb\xbf" + to_text(md).encode("utf-8"))
        page = out_dir / f"{stem}.html"
        page.write_text(to_html(md, title), encoding="utf-8", newline="\n")
        written += [txt, page]
    return written


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    for p in write(Path(sys.argv[1])):
        print(p)
