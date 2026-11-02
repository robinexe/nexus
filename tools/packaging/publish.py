#!/usr/bin/env python3
"""Publica o NEXUS para Windows numa pasta única e gera o ZIP portátil.

    python tools/packaging/publish.py                       # win-x64, saída em artifacts/
    python tools/packaging/publish.py --rid win-x64 --rid win-arm64 --version-suffix rc.1
    python tools/packaging/publish.py --checksums artifacts  # só grava artifacts/SHA256SUMS.txt

O que faz, por RID:
  1. dotnet publish self-contained (sem precisar do .NET instalado no PC), ReadyToRun (inicia mais rápido), SEM trimming
     (WPF e reflexão não suportam), de: nexus-broker (aplicador), nexus (linha de comando), nexus-task (a mesma linha de
     comando sem console, para as tarefas agendadas não abrirem janela), nexus-recover e Nexus.Desktop.
  2. Junta as cinco publicações em artifacts/<rid>/NEXUS/. Arquivos com o mesmo nome precisam ser idênticos byte a byte
     (o runtime é o mesmo); qualquer diferença interrompe a publicação em vez de escolher um ao acaso.
  3. Confere a estrutura: os cinco executáveis lado a lado (a interface e o nexus iniciam o nexus-broker.exe da própria pasta;
     as tarefas agendadas rodam o nexus-task.exe da pasta, que tem de ser aplicativo de janela, sem console), o catálogo completo em catalog\\, nenhum executável dependente de .NET
     instalado, arquitetura PE certa.
  4. Copia tools\\service (serviço opcional do aplicador) e os scripts de tools\\packaging usados pelo instalador; confere o
     PresentMon embutido (tools\\PresentMon\\PresentMon.exe, SHA-256 conhecido), a licença dele e THIRD-PARTY-NOTICES.txt.
  5. Grava VERSAO.txt e LEIA-ME-PORTATIL.txt e cria artifacts/NEXUS-<versão>-<rid>-portatil.zip (entradas ordenadas e com
     data fixa: o mesmo conteúdo gera o mesmo ZIP).

Lock files: antes de publicar, a solução é restaurada com --locked-mode (versões e hashes conferidos contra os
packages.lock.json versionados). Publicar com -r <RID> acrescenta a cada lock file uma seção "<tfm>/<rid>"; para não sujar o
repositório (nem obrigar todo projeto a versionar seções por RID), a publicação roda numa CÓPIA dos arquivos versionados
(artifacts/src) e, depois, confere que nos lock files da cópia nada mudou além das seções por RID acrescentadas.

Só biblioteca padrão do Python. Roda em Windows e Linux (com EnableWindowsTargeting, já ligado em Directory.Build.props).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECTS = [  # ordem de publicação; a junção exige arquivos comuns idênticos, então a ordem não muda o resultado
    ("src/Nexus.Broker.Host/Nexus.Broker.Host.csproj", "nexus-broker.exe"),
    ("src/Nexus.Cli/Nexus.Cli.csproj", "nexus.exe"),
    ("src/Nexus.Cli.Task/Nexus.Cli.Task.csproj", "nexus-task.exe"),
    ("src/Nexus.Recovery.Cli/Nexus.Recovery.Cli.csproj", "nexus-recover.exe"),
    ("src/Nexus.Desktop/Nexus.Desktop.csproj", "Nexus.Desktop.exe"),
]
PE_MACHINE = {"win-x64": 0x8664, "win-arm64": 0xAA64}
# Subsistema PE: 2 = janela (sem console), 3 = console. A tarefa agendada roda nexus-task.exe; se ele virasse console, o
# Agendador voltaria a abrir uma janela preta a cada execução.
PE_SUBSYSTEM = {"nexus-task.exe": 2, "nexus.exe": 3, "nexus-recover.exe": 3, "Nexus.Desktop.exe": 2}
EXTRA_FILES = [  # (origem relativa à raiz, destino relativo à pasta publicada)
    ("tools/service/Install-Broker.ps1", "tools/service/Install-Broker.ps1"),
    ("tools/selftest/Rodar-Autoteste.cmd", "Rodar-Autoteste.cmd"),
    ("tools/service/Uninstall-Broker.ps1", "tools/service/Uninstall-Broker.ps1"),
    ("tools/service/README.md", "tools/service/README.md"),
    ("tools/packaging/Remove-NexusTasks.ps1", "tools/Remove-NexusTasks.ps1"),
    ("tools/packaging/Test-NexusFolderSafety.ps1", "tools/Test-NexusFolderSafety.ps1"),
    ("tools/packaging/Set-NexusPerfLogGroup.ps1", "tools/Set-NexusPerfLogGroup.ps1"),
]
# PresentMon 2.6.0 oficial (Intel, MIT) embutido: o build da interface e da CLI já copia para tools\PresentMon; aqui só se
# confere que chegou intacto (mesmo SHA-256 de src/Nexus.Measurement/PresentMonTrust.cs e de docs/05-dependencies.md).
PRESENTMON = ("tools/PresentMon/PresentMon.exe", "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af")
PRESENTMON_FILES = ["tools/PresentMon/LICENSE.txt", "THIRD-PARTY-NOTICES.txt"]
ZIP_DATE = (2020, 1, 1, 0, 0, 0)
SRC_COPY = ROOT / "artifacts" / "src"


def log(msg: str) -> None:
    print(msg, flush=True)


def fail(msg: str) -> None:
    sys.exit(f"ERRO: {msg}")


def run(cmd: list[str]) -> None:
    log("> " + " ".join(cmd))
    r = subprocess.run(cmd, cwd=ROOT)
    if r.returncode != 0:
        fail(f"comando falhou ({r.returncode}): {' '.join(cmd)}")


def version_prefix() -> str:
    text = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    m = re.search(r"<VersionPrefix>([0-9]+\.[0-9]+\.[0-9]+)</VersionPrefix>", text)
    if not m:
        fail("VersionPrefix não encontrado em Directory.Build.props")
    return m.group(1)


def commit_hash() -> str:
    sha = os.environ.get("GITHUB_SHA", "")
    if not sha:
        try:
            sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
        except (OSError, subprocess.CalledProcessError):
            sha = "desconhecido"
    return sha[:10] if re.fullmatch(r"[0-9a-f]{7,40}", sha) else "desconhecido"


def dotnet() -> str:
    exe = shutil.which("dotnet") or str(Path.home() / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet"))
    if not Path(exe).exists() and not shutil.which(exe):
        fail("dotnet não encontrado")
    return exe


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def pe_header(path: Path) -> tuple[int, int]:
    """(máquina, subsistema) do cabeçalho PE; (-1, -1) se não for PE."""
    with path.open("rb") as f:
        head = f.read(4096)
    if head[:2] != b"MZ":
        return -1, -1
    off = struct.unpack_from("<I", head, 0x3C)[0]
    if head[off:off + 4] != b"PE\0\0":
        return -1, -1
    machine = struct.unpack_from("<H", head, off + 4)[0]
    subsystem = struct.unpack_from("<H", head, off + 24 + 68)[0]  # cabeçalho opcional começa em +24; Subsystem em +68 (PE32 e PE32+)
    return machine, subsystem


def pe_machine(path: Path) -> int:
    return pe_header(path)[0]


def copy_sources(dest: Path) -> None:
    """Cópia dos arquivos versionados (e novos não ignorados) para publicar sem reescrever os lock files do repositório."""
    try:
        listed = subprocess.run(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT,
                                capture_output=True, check=True).stdout.decode("utf-8").split("\0")
    except (OSError, subprocess.CalledProcessError) as e:
        fail(f"git ls-files falhou: {e}")
    shutil.rmtree(dest, ignore_errors=True)
    for rel in filter(None, listed):
        if rel.startswith(("artifacts/", ".git")):
            continue
        s = ROOT / rel
        if not s.is_file():
            continue  # apagado no diretório de trabalho
        d = dest / rel
        d.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(s, d)


def packages_only(section: dict | None) -> dict:
    return {k: v for k, v in (section or {}).items() if v.get("type") != "Project"}


def check_lock_files(copy: Path) -> int:
    """Nos lock files da cópia só podem ter sido ACRESCENTADAS seções "<tfm>/<rid>"; o resto é igual ao versionado."""
    count = 0
    for lock in sorted(copy.rglob("packages.lock.json")):
        original = ROOT / lock.relative_to(copy)
        if not original.exists():
            continue
        before = json.loads(original.read_text(encoding="utf-8-sig"))["dependencies"]
        after = json.loads(lock.read_text(encoding="utf-8-sig"))["dependencies"]
        for tfm, deps in before.items():
            # Entradas "Project" (referências entre projetos do próprio repositório) trazem a versão do NEXUS, que muda a
            # cada versão; o que importa conferir são os pacotes (nome, versão, hash).
            if packages_only(after.get(tfm)) != packages_only(deps):
                fail(f"{lock.relative_to(copy)}: a seção {tfm} mudou na publicação (pacotes diferentes dos conferidos)")
        extra = [k for k in after if k not in before and "/" not in k]
        if extra:
            fail(f"{lock.relative_to(copy)}: seções inesperadas {extra}")
        count += 1
    return count


def publish(rid: str, args: argparse.Namespace, version: str, suffix: str, commit: str) -> Path:
    stage_root = Path(args.out) / "stage" / rid
    target = Path(args.out) / rid / "NEXUS"
    if not args.skip_publish:
        shutil.rmtree(stage_root, ignore_errors=True)
    shutil.rmtree(target, ignore_errors=True)
    target.mkdir(parents=True)
    MERGED_VERSIONS.clear()

    for project, _exe in PROJECTS:
        stage = stage_root / Path(project).stem
        if not args.skip_publish:
            cmd = [dotnet(), "publish", str(SRC_COPY / project), "-c", "Release", "-r", rid, "--self-contained", "true",
                   "-o", str(stage), "-nologo", "-v", "q",
                   "-p:EnableSourceControlManagerQueries=false", "-p:EnableSourceLink=false",
                   f"-p:PublishReadyToRun={'false' if args.no_r2r else 'true'}",
                   "-p:PublishTrimmed=false", "-p:PublishSingleFile=false",
                   "-p:SatelliteResourceLanguages=pt-BR",
                   "-p:DebugType=none", "-p:GenerateDocumentationFile=false",
                   f"-p:SourceRevisionId={commit}", f"-p:VersionPrefix={version}",
                   "-p:NexusPackaged=true"]  # marca de cópia publicada: liga a atualização automática (docs/11-atualizacao.md)
            if suffix:
                cmd.append(f"-p:VersionSuffix={suffix}")
            run(cmd)
        merge(stage, target)

    for src, dst in EXTRA_FILES:
        s = ROOT / src
        if not s.exists():
            fail(f"arquivo esperado não existe: {src}")
        d = target / dst
        d.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(s, d)
    return target


def parse_version(v: str) -> tuple[int, ...]:
    return tuple(int(x) for x in re.findall(r"[0-9]+", v)[:4])


def managed_versions(folder: Path) -> dict[str, tuple[tuple[int, ...], tuple[int, ...]]]:
    """Nome do arquivo -> (assemblyVersion, fileVersion), lido dos *.deps.json da publicação."""
    found: dict[str, tuple[tuple[int, ...], tuple[int, ...]]] = {}
    for deps in folder.glob("*.deps.json"):
        data = json.loads(deps.read_text(encoding="utf-8-sig"))
        for libs in data.get("targets", {}).values():
            for lib in libs.values():
                for path, meta in (lib.get("runtime") or {}).items():
                    if "assemblyVersion" in meta:
                        # O mesmo nome pode vir de dois runtime packs (ex.: WindowsBase): o SDK copia o de maior versão.
                        v = (parse_version(meta["assemblyVersion"]), parse_version(meta.get("fileVersion", "0")))
                        name = Path(path).name
                        found[name] = max(found.get(name, v), v)
    return found


# Versões dos assemblies já juntados na pasta final (nome -> versões), para decidir conflitos.
MERGED_VERSIONS: dict[str, tuple[tuple[int, ...], tuple[int, ...]]] = {}


def merge(stage: Path, target: Path) -> None:
    """Junta uma publicação na pasta final.

    Arquivo com o mesmo nome e bytes diferentes só é aceito se for um assembly gerenciado listado nos deps.json dos dois
    lados; vale a mesma regra do host do .NET quando dois frameworks trazem o mesmo assembly: fica a maior assemblyVersion,
    depois a maior fileVersion; empate = o que já estava (aplicador, publicado primeiro). Casos reais: WindowsBase.dll
    (fachada 4.0 do runtime base x WindowsBase 10.0 real do WPF: fica a do WPF, que contém os mesmos encaminhamentos),
    System.Diagnostics.EventLog.dll (pacote x runtime do WPF, mesma versão) e SQLitePCLRaw.provider.e_sqlite3.dll (variantes
    net6.0 e net6.0-windows7.0 do mesmo pacote e versão). Qualquer outro conflito (nativo, catálogo, configuração) para tudo.
    """
    incoming = managed_versions(stage)
    conflicts = []
    for f in sorted(p for p in stage.rglob("*") if p.is_file()):
        rel = f.relative_to(stage)
        d = target / rel
        if d.exists():
            if sha256(d) == sha256(f):
                continue
            name = rel.as_posix()
            if "/" in name or name not in incoming or name not in MERGED_VERSIONS:
                conflicts.append(name)
                continue
            if incoming[name] > MERGED_VERSIONS[name]:
                log(f"  {name}: {incoming[name][0]} de {stage.name} substitui {MERGED_VERSIONS[name][0]} (versão maior)")
                shutil.copy2(f, d)
                MERGED_VERSIONS[name] = incoming[name]
            else:
                log(f"  {name}: mantida a já presente {MERGED_VERSIONS[name][0]} (de {stage.name}: {incoming[name][0]})")
            continue
        d.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(f, d)
        if rel.as_posix() in incoming:
            MERGED_VERSIONS[rel.as_posix()] = incoming[rel.as_posix()]
    if conflicts:
        fail(f"arquivos com o mesmo nome e conteúdo diferente ao juntar {stage.name}: {', '.join(conflicts[:20])}")


def verify(target: Path, rid: str) -> list[str]:
    report = []
    for _project, exe in PROJECTS:
        p = target / exe
        if not p.is_file():
            fail(f"falta {exe} na pasta publicada")
        machine, subsystem = pe_header(p)
        if machine != PE_MACHINE[rid]:
            fail(f"{exe}: arquitetura PE 0x{machine:X}, esperado 0x{PE_MACHINE[rid]:X} ({rid})")
        if exe in PE_SUBSYSTEM and subsystem != PE_SUBSYSTEM[exe]:
            fail(f"{exe}: subsistema PE {subsystem}, esperado {PE_SUBSYSTEM[exe]} (2 = janela, 3 = console)")
        report.append(f"{exe}: ok ({rid}, {'janela' if subsystem == 2 else 'console' if subsystem == 3 else subsystem})")

    expected = sorted(p.name for p in (ROOT / "catalog").glob("*.json"))
    got = sorted(p.name for p in (target / "catalog").glob("*.json"))
    if expected != got:
        fail(f"catálogo incompleto na pasta publicada: esperado {expected}, encontrado {got}")
    for name in expected:
        if sha256(ROOT / "catalog" / name) != sha256(target / "catalog" / name):
            fail(f"catalog/{name} diferente do repositório")
    report.append(f"catalog: {len(got)} arquivos, iguais ao repositório")

    for rc in sorted(target.glob("*.runtimeconfig.json")):
        opts = json.loads(rc.read_text(encoding="utf-8")).get("runtimeOptions", {})
        if "framework" in opts or "frameworks" in opts:
            fail(f"{rc.name} depende de .NET instalado (esperado self-contained)")
        if "includedFrameworks" not in opts:
            fail(f"{rc.name} sem includedFrameworks (esperado self-contained)")
    report.append("runtimeconfig: todos self-contained")

    for must in ["hostfxr.dll", "coreclr.dll", "e_sqlite3.dll", "PresentationFramework.dll", "System.ServiceProcess.ServiceController.dll"]:
        if not (target / must).is_file():
            fail(f"falta {must} na pasta publicada")
    report.append("runtime, WPF, SQLite nativo e ServiceController presentes")

    pm, pm_hash = PRESENTMON
    if not (target / pm).is_file():
        fail(f"falta {pm} (PresentMon embutido) na pasta publicada")
    if sha256(target / pm) != pm_hash:
        fail(f"{pm} com SHA-256 diferente do PresentMon 2.6.0 oficial")
    for f in PRESENTMON_FILES:
        if not (target / f).is_file():
            fail(f"falta {f} na pasta publicada")
    report.append("PresentMon 2.6.0 embutido (SHA-256 conferido), licença e avisos de terceiros presentes")
    return report


def write_texts(target: Path, rid: str, full_version: str, commit: str) -> None:
    (target / "VERSAO.txt").write_text(
        f"NEXUS {full_version}\r\ncommit {commit}\r\nplataforma {rid}\r\n", encoding="utf-8")
    (target / "LEIA-ME-PORTATIL.txt").write_text(
        "NEXUS (versão portátil)\r\n"
        "\r\n"
        "Para abrir: extraia o ZIP inteiro numa pasta e dê duplo clique em Nexus.Desktop.exe. Não precisa instalar nem do .NET.\r\n"
        "Os executáveis precisam ficar juntos nesta pasta: a interface (Nexus.Desktop.exe) e a linha de comando (nexus.exe)\r\n"
        "iniciam o aplicador (nexus-broker.exe) desta mesma pasta, e o catálogo de ações fica em catalog\\.\r\n"
        "\r\n"
        "Dados: %LocalAppData%\\NEXUS (configurações, relatórios, diário e quarentena das ações de usuário) e %ProgramData%\\NEXUS\r\n"
        "(diário e quarentena das ações de administrador). Apagar esta pasta NÃO apaga esses dados nem desfaz alterações:\r\n"
        "use \"Desfazer\" no NEXUS antes.\r\n"
        "\r\n"
        "Tarefas agendadas: a tarefa do Windows guarda o caminho do nexus-task.exe desta pasta (a versão sem janela do nexus.exe).\r\n"
        "Se mover ou renomear a pasta, a tarefa deixa de funcionar; abra o NEXUS e ligue o agendamento de novo.\r\n"
        "\r\n"
        "Segurança no modo administrador: ações de administrador iniciam nexus-broker.exe elevado (aviso do Windows). Numa pasta\r\n"
        "do seu usuário (Downloads, Área de Trabalho) qualquer programa rodando com a sua conta pode trocar esses arquivos antes\r\n"
        "do aviso. Para usar ações de administrador com frequência, prefira o instalador (pasta em Arquivos de Programas, que só\r\n"
        "administradores alteram). Para conferir esta pasta: powershell -ExecutionPolicy Bypass -File tools\\Test-NexusFolderSafety.ps1\r\n"
        "\r\n"
        "Medir: o PresentMon 2.6.0 oficial da Intel (licença MIT) acompanha o NEXUS em tools\\PresentMon. Antes de cada uso o NEXUS\r\n"
        "confere a impressão digital (SHA-256) ou a assinatura digital da Intel; um arquivo trocado não é executado. Avisos de\r\n"
        "terceiros: THIRD-PARTY-NOTICES.txt.\r\n",
        encoding="utf-8")


def make_zip(target: Path, zip_path: Path) -> None:
    zip_path.unlink(missing_ok=True)
    files = sorted((p for p in target.rglob("*") if p.is_file()), key=lambda p: p.relative_to(target).as_posix())
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in files:
            info = zipfile.ZipInfo("NEXUS/" + f.relative_to(target).as_posix(), ZIP_DATE)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with f.open("rb") as src:
                z.writestr(info, src.read())


def write_checksums(directory: Path) -> Path:
    out = directory / "SHA256SUMS.txt"
    files = sorted(p for p in directory.iterdir() if p.is_file() and p.suffix.lower() in {".zip", ".exe"})
    if not files:
        fail(f"nada para conferir em {directory}")
    out.write_text("".join(f"{sha256(p)}  {p.name}\n" for p in files), encoding="ascii", newline="\n")
    log(out.read_text(encoding="ascii"))
    return out


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rid", action="append", choices=sorted(PE_MACHINE), help="win-x64 (padrão) e/ou win-arm64")
    ap.add_argument("--out", default=str(ROOT / "artifacts"))
    ap.add_argument("--version-suffix", default="", help="ex.: rc.1 (vira 1.0.0-rc.1)")
    ap.add_argument("--no-r2r", action="store_true", help="sem ReadyToRun (publicação mais rápida, início mais lento)")
    ap.add_argument("--no-restore-check", action="store_true", help="pula o restore --locked-mode da solução")
    ap.add_argument("--skip-publish", action="store_true", help="reaproveita artifacts/stage (só junta, confere e zipa)")
    ap.add_argument("--no-zip", action="store_true", help="publica e confere, sem zipar (para assinar antes)")
    ap.add_argument("--zip-only", action="store_true", help="só confere e zipa artifacts/<rid>/NEXUS já publicada (e assinada)")
    ap.add_argument("--checksums", metavar="DIR", help="só grava DIR/SHA256SUMS.txt dos .zip/.exe e sai")
    args = ap.parse_args()

    if args.checksums:
        write_checksums(Path(args.checksums))
        return

    suffix = args.version_suffix.strip()
    if suffix and not re.fullmatch(r"[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*", suffix):
        fail("--version-suffix inválido (use letras, números e pontos; ex.: rc.1)")
    version = version_prefix()
    full_version = f"{version}-{suffix}" if suffix else version
    commit = commit_hash()
    rids = args.rid or ["win-x64"]
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    log(f"NEXUS {full_version}+{commit} para {', '.join(rids)}")

    if args.zip_only:
        # Depois da assinatura (release.yml): a pasta já existe e não pode ser refeita, senão perde as assinaturas.
        for rid in rids:
            target = out / rid / "NEXUS"
            for line in verify(target, rid):
                log("  " + line)
            zip_path = out / f"NEXUS-{full_version}-{rid}-portatil.zip"
            make_zip(target, zip_path)
            log(f"  zip: {zip_path} ({zip_path.stat().st_size / 1048576:.0f} MB)")
        return

    global SRC_COPY
    SRC_COPY = out / "src"
    if not args.skip_publish:
        if not args.no_restore_check:
            run([dotnet(), "restore", "Nexus.slnx", "--locked-mode", "-nologo", "-v", "q"])
        copy_sources(SRC_COPY)

    for rid in rids:
        target = publish(rid, args, version, suffix, commit)
        if not args.skip_publish:
            log(f"  lock files: {check_lock_files(SRC_COPY)} conferidos (só seções por RID acrescentadas)")
        write_texts(target, rid, full_version, commit)
        for line in verify(target, rid):
            log("  " + line)
        size = sum(p.stat().st_size for p in target.rglob("*") if p.is_file())
        log(f"  pasta: {target} ({size / 1048576:.0f} MB)")
        if not args.no_zip:
            zip_path = out / f"NEXUS-{full_version}-{rid}-portatil.zip"
            make_zip(target, zip_path)
            log(f"  zip: {zip_path} ({zip_path.stat().st_size / 1048576:.0f} MB)")

    # Para o workflow: versão completa num arquivo simples (o instalador usa).
    (out / "version.txt").write_text(f"{full_version}\n{commit}\n", encoding="ascii", newline="\n")


if __name__ == "__main__":
    main()
