# NEXUS Performance Lab

Laboratório de desempenho para jogos no Windows (foco VALORANT e CS2): diagnostica, aplica alterações reais e reversíveis, mede e compara.

> **Status (2026-10-30):** compila e passa em 99/99 testes em Linux; versão anterior validada no Windows (55 testes).
> Interface desktop e descoberta de hardware em **modo somente leitura**. Aplicação de otimizações **bloqueada** até os
> testes em VM Windows. Comece por **`HANDOFF.md`**.

## Instalação (Windows 10/11, 64 bits)

Baixe da página de Releases (ou dos artifacts do workflow `release`) e confira o SHA-256 em `SHA256SUMS.txt`
(`Get-FileHash .\arquivo -Algorithm SHA256`). Não precisa do .NET instalado.

- **Instalador (recomendado):** `NEXUS-<versão>-win-x64-instalador.exe`. Instala em `%ProgramFiles%\NEXUS` (pede
  administrador), cria atalho no Menu Iniciar e, se você marcar, o serviço do aplicador. Desinstale por Configurações →
  Aplicativos: o desinstalador pergunta antes de apagar os seus dados e, à parte, a quarentena (padrão: manter).
- **Portátil:** `NEXUS-<versão>-win-x64-portatil.zip`. Extraia tudo numa pasta e abra `Nexus.Desktop.exe`. Os dados ficam
  em `%LocalAppData%\NEXUS` e `%ProgramData%\NEXUS`. Se mover a pasta, ligue de novo a verificação semanal. Para ações de
  administrador frequentes, prefira o instalador: numa pasta do seu usuário, outros programas podem trocar os arquivos que
  rodariam como administrador (`tools\Test-NexusFolderSafety.ps1` confere a pasta).

Gerar os pacotes: `python tools/packaging/publish.py` (pasta e ZIP) e o workflow `release.yml` (instalador, testes de
instalação numa VM descartável, SHA-256, Release em rascunho em tags `v*`). Detalhes: `docs/08-packaging.md`.

## Documentos

| Arquivo | Conteúdo |
|---|---|
| `docs/01-research-matrix.md` | 10 repositórios inspecionados (commit, licença, mecanismo, decisão) + docs oficiais |
| `docs/02-architecture.md` | Módulos, fronteira de privilégio, modelo de ameaças, protocolo do broker |
| `docs/03-capability-matrix.md` | O que existe, o que foi testado, o que falta |
| `docs/05-dependencies.md` | Dependências, licenças e obrigações |
| `docs/06-build-log.md` | Comandos, códigos de saída e evidências por ambiente |
| `docs/07-recovery-cases.md` | Casos ambíguos de recuperação, revisão independente e decisões pendentes |
| `docs/08-packaging.md` | Publicação, ZIP portátil, instalador, workflow de release, assinatura e ícone |
| `HANDOFF.md` | Estado verificado, pendências e comandos reproduzíveis |
| `catalog/actions.v1.json` | 13 ações classificadas + 14 mitos/ajustes rejeitados com motivo |

## Build e testes (requer .NET 10 SDK)

```powershell
dotnet restore Nexus.slnx
dotnet build Nexus.slnx -c Release --no-restore
dotnet test  Nexus.slnx -c Release --no-build --filter Category=Unit
powershell -ExecutionPolicy Bypass -File .\tools\etapa1-windows.ps1   # tudo, com log em %TEMP%
```

Os testes não alteram o sistema: usam backends em memória, diretórios temporários e processos auxiliares que só
escrevem em %TEMP%. Eles rodam também em Linux/macOS.

## Interface (somente leitura)

Duplo clique em **`Abrir NEXUS.cmd`** (compila na primeira vez; não precisa de Visual Studio nem de administrador). Executável: `src\Nexus.Desktop\bin\Release\net10.0-windows\Nexus.Desktop.exe`. Mostra hardware, diagnóstico, catálogo com o estado
real, simulação do plano (dry-run), histórico e capacidades. Não aplica nada.

## Broker e testes em VM

`nexus-broker` serve o protocolo fechado do broker por named pipe. As escritas reais só passam por um portão que exige
máquina virtual de teste liberada; no PC físico ele só responde a leituras e simulações. Para testar aplicação e rollback
reais com segurança: `tools\vm\README.md` (Windows Sandbox descartável).

## Medir e comparar

Página **Medir**: grava uma partida com o PresentMon (ferramenta gratuita da Intel; baixe em github.com/GameTechDev/PresentMon e
indique onde salvou) ou importa CSV do PresentMon/CapFrameX. Mostra o traço de cada quadro, FPS médio, 1% low, frametime P99 e
engasgos. Página **Comparar**: sessões marcadas como "antes" e "depois"; com 3 ou mais de cada lado, diz se a melhora é real
(intervalo de confiança de 95%); com menos, mostra a diferença e avisa que ela não prova nada. Medir não altera nada no PC.

## Recuperação sem a interface

```powershell
nexus-recover scan                      # operações interrompidas e se há executor ativo (somente leitura)
nexus-recover recover                   # restaura; recusa (exit 5) se houver execução ativa; conflitos ficam pendentes
nexus-recover recover --force           # restaura também itens alterados externamente
nexus-recover show <runId>
nexus-recover accept <entryId>          # mantém o estado atual e encerra uma entrada pendente (só grava no diário)
```

Regras: nunca escreve sobre algo que o NEXUS não escreveu sem `--force`; nunca declara "recuperação completa" com
falha parcial; nunca interfere numa execução ativa.
