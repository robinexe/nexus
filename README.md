# NEXUS

Desempenho e manutenção do Windows para quem joga, **sem quebrar nada**. O NEXUS lê o seu PC, explica o que encontrou e,
quando você confirma, aplica ajustes reais. Cada mudança mostra o que faz, de onde vem a informação, o risco e como
desfazer; tudo fica num histórico e pode ser desfeito.

Versão atual: **1.0.0** (novidades em [`CHANGELOG.md`](CHANGELOG.md)).

## O que faz

- **Assistente**: lê o PC e monta um plano em etapas (limpeza, inicialização, privacidade, energia, drivers, ajustes), aplica
  com progresso real, gera um relatório e tem "Desfazer tudo".
- **Manutenção**: limpeza (para a quarentena, nunca apaga direto), espaço em disco, desinstalador com sobras, inicialização e
  serviços, privacidade, segurança (placar do Defender e verificação de arquivos), ferramentas do sistema, tarefas agendadas.
- **Desempenho com honestidade**: ajustes com fonte (Microsoft e fabricantes), mitos marcados como mitos, ajustes da placa
  de vídeo (DirectX, NVIDIA por jogo), laboratório de processador e memória, "o que só você pode fazer" (BIOS, XMP, MUX).
- **Medir**: FPS e tempo de quadro com o PresentMon oficial da Intel (incluído), antes x depois no seu PC.
- **Drivers**: Windows Update oficial e drivers de vídeo NVIDIA/AMD dos servidores do fabricante, com assinatura conferida e
  cópia do driver anterior.

## Requisitos

- Windows 10 (versão 1809 ou mais nova) ou Windows 11, **64 bits (x64)**.
- Não precisa instalar o .NET (vem junto).
- Conta de administrador só para as mudanças de sistema (o Windows pede permissão na hora).

## Instalar

Baixe da página de Releases:

- **Instalador (recomendado):** `NEXUS-1.0.0-win-x64-instalador.exe`. Instala em `%ProgramFiles%\NEXUS`, mostra os
  [termos de uso](docs/legal/TERMOS-DE-USO.md) e cria o atalho no Menu Iniciar. Depois disso o NEXUS se atualiza sozinho
  (pode desligar em Ajustes → Atualizações).
- **Portátil:** `NEXUS-1.0.0-win-x64-portatil.zip`. Extraia tudo numa pasta e abra `Nexus.Desktop.exe`. Para usar ações de
  administrador com frequência, prefira o instalador: numa pasta do seu usuário, outros programas poderiam trocar os
  arquivos que rodam como administrador (`tools\Test-NexusFolderSafety.ps1` confere a pasta).

### Aviso do Windows (SmartScreen) e como conferir o download

O NEXUS **ainda não tem certificado de assinatura de código** (custa caro e exige empresa registrada). Por isso o Windows
pode mostrar "O Windows protegeu o computador" ao abrir o instalador e "Editor desconhecido" no aviso de permissão. Isso não
quer dizer que o arquivo tem vírus; quer dizer que o Windows não sabe quem o publicou.

Antes de clicar em "Mais informações → Executar assim mesmo", confira que o arquivo é exatamente o publicado:

```powershell
Get-FileHash .\NEXUS-1.0.0-win-x64-instalador.exe -Algorithm SHA256
```

O resultado tem de ser igual à linha do arquivo em `SHA256SUMS.txt` (na mesma página de Releases). Diferente: não abra e
baixe de novo. As atualizações automáticas já são conferidas pelo próprio NEXUS (manifesto com assinatura digital e SHA-256).

### Desinstalar

Configurações do Windows → Aplicativos → NEXUS. **Antes**, use "Desfazer" no NEXUS para o que quiser reverter. O desinstalador
pergunta se apaga os dados (padrão: manter), porque sem o histórico o NEXUS não consegue mais desfazer o que alterou; a
quarentena tem pergunta própria.

## Privacidade

Sem conta, sem telemetria, sem anúncios. Os dados ficam no seu PC (`%LocalAppData%\NEXUS` e `%ProgramData%\NEXUS`). O NEXUS
só usa a internet para procurar a própria atualização (GitHub, assinada), drivers (Windows Update, NVIDIA, AMD), atualizações
de programas pelos gerenciadores que você tem, e links que você clica. Detalhes: [`docs/legal/PRIVACIDADE.md`](docs/legal/PRIVACIDADE.md).

O NEXUS é um projeto independente, sem afiliação com Microsoft, NVIDIA, AMD ou Intel. Componentes de terceiros e licenças:
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).

## Para desenvolvedores

Requer o .NET 10 SDK (`global.json`). Comece por [`HANDOFF.md`](HANDOFF.md).

```powershell
dotnet restore Nexus.slnx --locked-mode
dotnet build Nexus.slnx -c Release --no-restore           # 0 avisos esperados
dotnet test  Nexus.slnx -c Release --no-build --filter Category=Unit
```

Os testes unitários não alteram o sistema (backends em memória e pastas temporárias) e rodam também em Linux. Testes com
escrita real rodam numa VM Windows descartável do GitHub Actions (`.github/workflows/vm-integracao.yml`).

- **`Abrir NEXUS.cmd`**: abre o NEXUS **instalado** (onde as ações de administrador funcionam). `Abrir NEXUS.cmd dev`
  compila e abre a cópia de desenvolvimento (`src\Nexus.Desktop\bin\Release`), que não roda ações de administrador porque a
  pasta é gravável.
- **Pacotes e versões**: `python tools/packaging/publish.py` e o workflow `release.yml`; como lançar uma versão em
  [`docs/08-packaging.md`](docs/08-packaging.md) (seção 8).
- **Linha de comando**: `nexus --help` (verificar, recomendar, simular, aplicar pelo aplicador, desfazer, histórico, medir).

### Recuperação sem a interface

```powershell
nexus-recover scan              # operações interrompidas (somente leitura)
nexus-recover recover           # restaura; recusa se houver execução ativa; conflitos ficam pendentes
nexus-recover show <runId>
nexus-recover accept <entryId>  # mantém o estado atual e encerra uma entrada pendente (só grava no diário)
```

### Documentos

| Arquivo | Conteúdo |
|---|---|
| `docs/02-architecture.md` | Módulos, fronteira de privilégio, modelo de ameaças, protocolo do aplicador |
| `docs/modules/*.md` | Cada módulo: o que faz, fontes, riscos, como desfaz, o que não faz e por quê |
| `docs/05-dependencies.md` | Dependências, licenças e obrigações |
| `docs/07-recovery-cases.md` | Casos de recuperação e decisões |
| `docs/08-packaging.md` | Publicação, instalador, ZIP, release, versões, assinatura |
| `docs/11-atualizacao.md` | Atualização automática assinada |
| `docs/legal/` | Termos de uso e Política de privacidade |
