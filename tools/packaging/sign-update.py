#!/usr/bin/env python3
"""Gera e assina o manifesto de atualização do NEXUS (nexus-update.json + nexus-update.json.sig).

    python tools/packaging/sign-update.py --installer artifacts/NEXUS-0.9.1-win-x64-instalador.exe --version 0.9.1 \\
        --out artifacts/update [--notes "Correções."] [--key-env NEXUS_UPDATE_SIGNING_KEY]

- A chave privada (PKCS#8 PEM, EC P-256) vem da variável de ambiente indicada (no CI: o segredo NEXUS_UPDATE_SIGNING_KEY).
  Ela é gravada só num arquivo temporário com permissão restrita e apagada no fim; nunca é impressa.
- O endereço do instalador é o asset do Release público: https://github.com/robinexe/nexus-releases/releases/download/v<versão>/<arquivo>.
- Assinatura: ECDSA P-256 com SHA-256, DER (RFC 3279), em base64 — o mesmo formato de `openssl dgst -sha256 -sign`, conferido
  pelo NEXUS (src/Nexus.Updates/UpdatePolicy.cs) sobre os bytes EXATOS do arquivo gravado.
- Usa o openssl (no runner Windows vem com o Git); sem openssl, tenta o pacote Python "cryptography".
- Depois de assinar, confira com o código do próprio aplicativo: nexus update --verify-manifest <out>/nexus-update.json

Só biblioteca padrão do Python (mais openssl ou cryptography).
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

REPO = "robinexe/nexus-releases"
MAX_SIZE = 300 * 1024 * 1024
SEMVER = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?")


def fail(msg: str) -> None:
    sys.exit(f"ERRO: {msg}")


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def find_openssl() -> str | None:
    found = shutil.which("openssl")
    if found:
        return found
    for candidate in (r"C:\Program Files\Git\usr\bin\openssl.exe", r"C:\Program Files\Git\mingw64\bin\openssl.exe"):
        if Path(candidate).is_file():
            return candidate
    return None


def sign(data_path: Path, key_pem: str) -> bytes:
    """Assinatura DER (RFC 3279) de SHA-256(data)."""
    openssl = find_openssl()
    if openssl:
        fd, key_file = tempfile.mkstemp(suffix=".pem")
        try:
            os.chmod(key_file, 0o600)
            with os.fdopen(fd, "w", encoding="ascii") as f:
                f.write(key_pem)
            sig_file = key_file + ".sig"
            r = subprocess.run([openssl, "dgst", "-sha256", "-sign", key_file, "-out", sig_file, str(data_path)], capture_output=True, text=True)
            if r.returncode != 0:
                fail(f"openssl falhou ({r.returncode}): {r.stderr.strip()[:300]}")
            der = Path(sig_file).read_bytes()
            Path(sig_file).unlink(missing_ok=True)
            return der
        finally:
            Path(key_file).unlink(missing_ok=True)
    try:
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import ec
    except ImportError:
        fail("nem openssl nem o pacote Python 'cryptography' estão disponíveis para assinar")
    key = serialization.load_pem_private_key(key_pem.encode("ascii"), password=None)
    if not isinstance(key, ec.EllipticCurvePrivateKey) or key.curve.name != "secp256r1":
        fail("a chave precisa ser EC P-256")
    return key.sign(data_path.read_bytes(), ec.ECDSA(hashes.SHA256()))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--installer", required=True, help="instalador .exe (o arquivo que vai no Release)")
    ap.add_argument("--version", required=True, help="versão SemVer, ex.: 0.9.1 ou 0.9.0-ci.12")
    ap.add_argument("--out", required=True, help="pasta de saída (nexus-update.json e nexus-update.json.sig)")
    ap.add_argument("--notes", default="", help="novidades em português, curto")
    ap.add_argument("--minimum-version", default="", help="opcional: abaixo desta versão o NEXUS só avisa, não instala sozinho")
    ap.add_argument("--key-env", default="NEXUS_UPDATE_SIGNING_KEY", help="variável de ambiente com a chave privada PEM")
    args = ap.parse_args()

    if not SEMVER.fullmatch(args.version):
        fail(f"versão inválida: {args.version}")
    if args.minimum_version and not SEMVER.fullmatch(args.minimum_version):
        fail(f"--minimum-version inválida: {args.minimum_version}")
    installer = Path(args.installer)
    if not installer.is_file():
        fail(f"instalador não encontrado: {installer}")
    size = installer.stat().st_size
    if size <= 0 or size > MAX_SIZE:
        fail(f"tamanho do instalador fora do limite (300 MB): {size}")
    if not re.fullmatch(r"[A-Za-z0-9._-]+\.exe", installer.name):
        fail(f"nome do instalador com caracteres inesperados: {installer.name}")
    key_pem = os.environ.get(args.key_env, "").strip()
    if not key_pem:
        fail(f"variável {args.key_env} vazia (segredo ausente)")
    key_pem = key_pem.replace("\r\n", "\n") + "\n"

    manifest = {
        "schema": "nexus.update/1",
        "version": args.version,
        "channel": "stable",
        "publishedAt": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "notes": args.notes.strip()[:2000],
        "installer": {
            "url": f"https://github.com/{REPO}/releases/download/v{args.version}/{installer.name}",
            "sha256": sha256(installer),
            "size": size,
        },
    }
    if args.minimum_version:
        manifest["minimumVersion"] = args.minimum_version

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest_path = out / "nexus-update.json"
    # Bytes gravados = bytes assinados = bytes que o NEXUS confere (UTF-8, sem BOM, \n).
    manifest_path.write_bytes((json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    der = sign(manifest_path, key_pem)
    if not der or der[0] != 0x30:
        fail("assinatura não está em DER")
    (out / "nexus-update.json.sig").write_bytes(base64.b64encode(der) + b"\n")
    print(manifest_path.read_text(encoding="utf-8"))
    print(f"Assinado: {out / 'nexus-update.json.sig'}")


if __name__ == "__main__":
    main()
