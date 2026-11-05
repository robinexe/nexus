# Dependências

Regra do projeto: nenhuma dependência nova entra sem registro aqui (licença, forma de integração, o que é
redistribuído e obrigação concreta) e sem aviso ao responsável. **O NEXUS ainda não tem licença escolhida.**
Esta tabela não decide isso.

Versões centralizadas em `Directory.Packages.props`; resolução travada pelos `packages.lock.json`.

## Diretas

| Pacote | Versão | Licença | Integração | Redistribuído? | Obrigação concreta |
|---|---|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.14 | MIT (`requireLicenseAcceptance=true` no NuGet) | PackageReference em `Nexus.Persistence` (e transitivamente no CLI e nos testes) | Sim: DLLs gerenciadas no output do app | Incluir aviso de copyright e texto da licença MIT na distribuição |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | Só no projeto de testes | Não | Nenhuma para a distribuição |
| xunit | 2.9.3 | Apache-2.0 | Só no projeto de testes | Não | Nenhuma para a distribuição |
| System.ServiceProcess.ServiceController | 10.0.14 | MIT (Microsoft, dotnet/runtime) | PackageReference em `Nexus.Broker.Host` (serviço do Windows); traz `System.Diagnostics.EventLog` 10.0.14 (MIT) | Sim: DLLs no output do broker | Incluir aviso de copyright e texto da licença MIT na distribuição. Adicionado em 31/10/2026, avisado ao responsável na conversa |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Só no projeto de testes | Não | Nenhuma para a distribuição |

## Binários de terceiros embutidos (fora do NuGet)

| Componente | Versão | Licença | Origem | Integração | Redistribuído? | Obrigação concreta |
|---|---|---|---|---|---|---|
| PresentMon (Intel) | 2.6.0 (x64) | MIT, Copyright (C) 2017-2024 Intel Corporation | <https://github.com/GameTechDev/PresentMon> release v2.6.0, arquivo `PresentMon-2.6.0-x64.exe` sem alteração; assinatura Authenticode de **Intel Corporation** (cadeia Sectigo Public Code Signing CA R36); SHA-256 `b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af` | `tools/presentmon/` no repositório; o build da interface e da CLI copia para `<saída>\tools\PresentMon\PresentMon.exe` + `LICENSE.txt`; `publish.py` confere o hash; o instalador põe em `%ProgramFiles%\NEXUS\tools\PresentMon`. O Medir executa só depois de conferir hash conhecido ou assinatura válida da Intel (`PresentMonTrust`) | Sim | Aviso de copyright e texto MIT: `tools\PresentMon\LICENSE.txt` e `THIRD-PARTY-NOTICES.txt` (raiz, copiado para a saída e para o instalador) |

Por que embutido (e não "o usuário baixa"): o pedido do responsável foi deixar o Medir pronto automaticamente; o NEXUS não pode
baixar nada sozinho (regra 8); e um PresentMon baixado pelo usuário numa pasta do perfil é um executável que se eleva
(`--restart_as_admin`) numa pasta gravável por qualquer programa da conta. Embutido na pasta protegida do instalador e conferido
antes de cada uso, o risco é o mesmo dos executáveis do próprio NEXUS. Atualizar: trocar o arquivo em `tools/presentmon`, o hash em
`PresentMonTrust.BundledSha256`/`publish.py`/`release.yml`/`THIRD-PARTY-NOTICES.txt` e esta tabela.

`THIRD-PARTY-NOTICES.txt` (raiz) reúne os avisos de tudo o que é redistribuído: PresentMon (MIT), Microsoft.Data.Sqlite,
ServiceController/EventLog, .NET e WPF (MIT), SQLitePCLRaw (Apache-2.0, texto completo) e SQLite (domínio público). O build da
interface e da CLI copia para a saída; vai no ZIP e no instalador (com atalho "NEXUS - avisos de terceiros" no Menu Iniciar).

## Transitivas relevantes para a distribuição

| Pacote | Versão resolvida | Licença | Trazido por | Redistribuído? | Obrigação concreta |
|---|---|---|---|---|---|
| SQLitePCLRaw.bundle_e_sqlite3 / .core / .provider.e_sqlite3 | 2.1.13 | Apache-2.0 | Microsoft.Data.Sqlite | Sim (DLLs gerenciadas) | Incluir cópia da licença Apache-2.0 e o NOTICE, se houver |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 (empacotamento); a biblioteca SQLite nativa é domínio público | Microsoft.Data.Sqlite | Sim: `e_sqlite3.dll` nativa em `runtimes/win-x64/native` | Licença Apache-2.0 do empacotamento; nenhuma para o SQLite em si |

## Histórico de mudanças

| Data | Mudança | Motivo |
|---|---|---|
| 2026-10-30 | Microsoft.Data.Sqlite 10.0.0 → **10.0.14** | NU1903 / GHSA-2m69-gcr7-jv3q / CVE-2025-6965 (SQLite < 3.50.2, CVSS 7.2) em SQLitePCLRaw.lib.e_sqlite3 ≤ 2.1.11. A 10.0.14 é o patch mais recente da mesma linha e puxa a 2.1.13. Não é dependência nova: mesmo pacote, mesma licença. |
| 2026-10-30 | Evidência do NU1903 sem supressão | Ver `06-build-log.md` (restore com 10.0.0 vs 10.0.14; SQLite nativo carregado 3.53.3). |
| 2026-11-01 | **PresentMon 2.6.0** (Intel, MIT) embutido em `tools/presentmon` | Pedido do responsável: Medir pronto para usar sem baixar nada. SHA-256 `b2a706bc6ad4…`, assinado por Intel Corporation. Avisado ao responsável na conversa. Sem pacote NuGet novo. |
| 2026-10-30 | Demais versões confirmadas | As versões fixadas originalmente restauraram sem NU1102; nenhuma troca foi necessária. |

## Frameworks do .NET (não são pacotes NuGet)

| Framework | Usado por | Licença | Redistribuído? | Obrigação concreta |
|---|---|---|---|---|
| Microsoft.NETCore.App 10.0 | todos os projetos | MIT | Não, se publicado como dependente do runtime instalado; sim, se publicado self-contained | Self-contained: incluir os avisos de terceiros do .NET (THIRD-PARTY-NOTICES) |
| Microsoft.WindowsDesktop.App 10.0 (WPF) | `Nexus.Desktop` (`UseWPF`) | MIT | Mesma regra acima | Mesma regra acima |

`Nexus.Desktop` não usa nenhum pacote MVVM ou de interface externo (ObservableObject e RelayCommand são do próprio
projeto). A Etapa 2 não adicionou nenhuma dependência nova.

## Recursos do Windows usados opcionalmente

| Recurso | Uso | Observação |
|---|---|---|
| Windows Sandbox (recurso opcional do Windows Pro/Enterprise) | Executar `tests/Nexus.IntegrationTests` isolado | Não é dependência do NEXUS e não é redistribuído; ativar é decisão do usuário (administrador + reinício). **Não existe no Windows Home** (o PC atual do responsável) |

## CI (adicionado em 2026-10-31, aviso ao responsável na conversa de 31/10)

| Ação | Versão | Licença | Uso | Redistribuído? |
|---|---|---|---|---|
| `actions/checkout` | v5 | MIT | `.github/workflows/vm-integracao.yml` | Não |
| `actions/setup-dotnet` | v5 | MIT | idem (SDK pelo `global.json`) | Não |
| `actions/upload-artifact` | v5 | MIT | idem (log, `.trx` e descoberta da VM) | Não |

Rodam só na VM descartável do GitHub (`windows-latest`); não entram no build nem na distribuição do NEXUS.

A Etapa 3 (broker, kit de VM) não adicionou pacotes.

## Skills do Claude para design (adicionado em 2026-11-05, aviso ao responsável na conversa de 05/11)

Ferramenta de desenvolvimento em `.claude/skills/` (instruções em Markdown para o assistente). **Não entra na solução,
no build, no instalador nem no ZIP**; nenhum pacote NuGet ou npm foi adicionado. Fontes, commits e licenças em
`.claude/skills/README.md`: MIT (ui-ux-pro-max, frontend-ui-engineering, 18 Skills de design, 8 Skills GSAP) e Apache-2.0
(frontend-design), com o texto da licença dentro de cada pasta. `animation-forge` não tem licença publicada: fica só na
máquina local, fora do git. O script opcional `ui-ux-pro-max/scripts/search.py` usa Python 3 (só biblioteca padrão) na
máquina de quem desenvolve. Obrigação para a distribuição: nenhuma.

## Ferramentas fora da solução

`tools/Nexus.SmokeJournal` (gerador do diário de teste), `tools/Nexus.Discover` (relatório de texto da descoberta) e
`tests/Nexus.TestHelper` (processo auxiliar dos testes de coordenação) usam só projetos do próprio NEXUS. Nenhum pacote novo.
