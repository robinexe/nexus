# NEXUS Performance Lab

Laboratório de desempenho para jogos no Windows (foco VALORANT e CS2): diagnostica, aplica alterações reais e reversíveis, mede e compara.

> **Status (2026-10-30):** compila e passa em 99/99 testes em Linux; versão anterior validada no Windows (55 testes).
> Interface desktop e descoberta de hardware em **modo somente leitura**. Aplicação de otimizações **bloqueada** até os
> testes em VM Windows. Comece por **`HANDOFF.md`**.

## Documentos

| Arquivo | Conteúdo |
|---|---|
| `docs/01-research-matrix.md` | 10 repositórios inspecionados (commit, licença, mecanismo, decisão) + docs oficiais |
| `docs/02-architecture.md` | Módulos, fronteira de privilégio, modelo de ameaças, protocolo do broker |
| `docs/03-capability-matrix.md` | O que existe, o que foi testado, o que falta |
| `docs/05-dependencies.md` | Dependências, licenças e obrigações |
| `docs/06-build-log.md` | Comandos, códigos de saída e evidências por ambiente |
| `docs/07-recovery-cases.md` | Casos ambíguos de recuperação, revisão independente e decisões pendentes |
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
