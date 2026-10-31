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
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | Só no projeto de testes | Não | Nenhuma para a distribuição |

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
| Windows Sandbox (recurso opcional do Windows Pro/Enterprise) | Executar `tests/Nexus.IntegrationTests` isolado | Não é dependência do NEXUS e não é redistribuído; ativar é decisão do usuário (administrador + reinício) |

A Etapa 3 (broker, kit de VM) não adicionou pacotes.

## Ferramentas fora da solução

`tools/Nexus.SmokeJournal` (gerador do diário de teste), `tools/Nexus.Discover` (relatório de texto da descoberta) e
`tests/Nexus.TestHelper` (processo auxiliar dos testes de coordenação) usam só projetos do próprio NEXUS. Nenhum pacote novo.
