# Módulo Segurança (`src/Nexus.Security`, página Segurança, grupo "Proteger")

## O que faz

1. **Placar de proteção (só leitura).** Uma leitura por WMI/CIM (script do Windows PowerShell da própria Microsoft, oculto,
   sem perfil, que só lê e devolve JSON) e pelo registro:
   - Microsoft Defender (`root\Microsoft\Windows\Defender`, `MSFT_MpComputerStatus` / `MSFT_MpPreference`): antivírus ativo,
     modo (normal/passivo), tempo real, nuvem (MAPS), versão e idade das assinaturas, última verificação rápida/completa,
     Proteção contra Adulteração, PUA, Acesso controlado a pastas, exclusões (só com administrador nas versões recentes).
   - Antivírus/firewall de terceiros (`root\SecurityCenter2`, `AntiVirusProduct`/`FirewallProduct`) com decodificação do
     `productState` (`ProductState.Decode`; formato não documentado pela Microsoft, por isso a página mostra o valor bruto).
   - Firewall por perfil (`MSFT_NetFirewallProfile`; reserva: `SharedAccess\Parameters\FirewallPolicy`), SmartScreen
     (`Explorer\SmartScreenEnabled` + política), UAC (`EnableLUA`, `ConsentPromptBehaviorAdmin`), Secure Boot
     (`SecureBoot\State\UEFISecureBootEnabled`), TPM (`Win32_Tpm`, ou o dispositivo PnP sem administrador), BitLocker por
     volume (`Win32_EncryptableVolume`, ou a propriedade do Shell `System.Volume.BitLockerProtection` sem administrador),
     reinício pendente do Windows Update (`RebootRequired` / `RebootPending`).
   - Cada item tem cor (verde/âmbar/vermelho/cinza), o porquê, o que fazer e um botão que abre a tela oficial
     (`windowsdefender://...`, `ms-settings:windowsupdate`).
2. **Verificações do Defender (exceção controlada do brief).** Rápida, completa, personalizada (pasta escolhida num diálogo,
   validada: absoluta, existente, sem reparse point) e atualização de assinaturas, pelo `MpCmdRun.exe` oficial (plataforma mais
   nova em `%ProgramData%\Microsoft\Windows Defender\Platform`, ou Program Files), depois de confirmação. Processo monitorado:
   tempo decorrido, linhas que o MpCmdRun escreve e botão Cancelar (encerra só o MpCmdRun iniciado). **O MpCmdRun não informa
   porcentagem**; a página diz isso em vez de inventar uma barra. Histórico de ameaças (`MSFT_MpThreat` +
   `MSFT_MpThreatDetection`) com status e recomendação; remoção/quarentena ficam com o Defender (botão abre a Segurança do Windows).
3. **Pontos suspeitos (heurísticas próprias, somente leitura, parsers puros e testados).** Cada achado tem gravidade, por que é
   suspeito, como verificar e o que fazer. Nada é removido automaticamente.
   - Inicialização (Run/RunOnce HKCU/HKLM 64/32, Policies\Explorer\Run, pastas Inicializar): executável sem assinatura em
     Temp/Downloads/Público (alta) ou AppData (média), comandos ofuscados, entradas apontando para arquivo inexistente (baixa).
     Assinatura via `WinVerifyTrust` sem rede, só para pastas de usuário (é a parte cara).
   - Tarefas agendadas (`Get-ScheduledTask`): `powershell -enc` (o texto é decodificado e mostrado), `-w hidden`, IEX/
     DownloadString, mshta/rundll32/regsvr32 com URL ou script, certutil `-urlcache`, bitsadmin `/transfer`, scripts de Temp.
     Padrões: MITRE ATT&CK T1059.001, T1218.005/.010/.011, T1105.
   - Arquivo hosts: redirecionamento de domínios de segurança/atualização/login/lojas (alta), bloqueio deles (média),
     bloqueio de telemetria/anúncios (informativo: provavelmente intencional, não é tocado). Também `DataBasePath` desviado.
   - Proxy do usuário, script PAC e proxy WinHTTP (o valor binário `WinHttpSettings` é decodificado).
   - Extensões forçadas por política (`ExtensionInstallForcelist` de Chrome/Edge/Brave/Chromium/Opera; Firefox
     `Extensions\Install`), alta quando a URL de atualização não é a loja oficial.
   - Exclusões do Defender amplas (raiz de disco, perfil, AppData, Temp, Windows, `*.exe`, `powershell.exe`...), muitas exclusões.
   - IFEO `Debugger` (acessibilidade e antivírus = alta; Process Explorer/System Informer no lugar do Gerenciador = informativo).
   - `AppInit_DLLs` (alta só se `LoadAppInit_DLLs=1` e Secure Boot desligado, que é quando ele de fato carrega).
   - Winlogon `Shell` diferente de `explorer.exe`, `Userinit` com entradas extras, `Shell` no HKCU.
4. **Correções reversíveis pelo aplicador (catálogo `catalog/security.v1.json`).**
   | Ação | Handler | Nível | Como desfaz |
   |---|---|---|---|
   | `security.hosts.disable-hijack` | `SecurityHostsFile` | Admin | Comenta as linhas corrigíveis (`# [NEXUS desativou]`), nunca apaga. Original guardado em `DataDirectory\quarantine\security\hosts\<sha256>.bak`; Restore devolve byte a byte **só se** o hosts ainda for exatamente o que o NEXUS escreveu (hash ao lado da cópia). Preserva BOM/UTF-16; recusa reparse point e conteúdo fora de UTF-8. |
   | `security.persistence.machine.remove` | `SecurityPersistenceValue` | Admin | Remove o valor `Debugger` das entradas IFEO escolhidas. Tipo e dados no manifesto; Restore recria. |
   | `security.persistence.user.remove` | `SecurityPersistenceValue` | User | Remove `HKCU\...\Winlogon\Shell` (perfil de quem pede: `userSid`). |
   | `security.appinit.clear` / `.wow64` | `RegistryValue` (núcleo) | Admin | Deixa `AppInit_DLLs` vazio (o padrão); o diário guarda o texto anterior. |
   | `security.winlogon.shell.reset` | `RegistryValue` (núcleo) | Admin | `Shell = explorer.exe`; o diário guarda o anterior. |
   | `security.registry.orphans.machine` / `.user` | `SecurityRegistryOrphan` | Admin / User | Ver inspetor. |
5. **Inspetor de registro (não é limpador).** Encontra sobras: Desinstalação apontando para desinstalador e pasta inexistentes
   (MSI, componentes do sistema, atualizações e discos ausentes nunca contam), App Paths quebrados, MUICache e SharedDLLs de
   arquivos inexistentes, e extensões do usuário sem programa (só informativo). A página mostra o mito com clareza:
   **remover sobras não acelera o PC** (Microsoft, KB 2563254, "Microsoft support policy for the use of registry cleaning
   utilities"). O usuário escolhe; até 28 identificadores por instância da ação e até 224 por pedido (núcleo v2: a
   página manda a mesma ação várias vezes no pedido, com seleções disjuntas — uma confirmação, uma execução, um Desfazer).
   O Desfazer da página reverte todas as execuções feitas nela (órfãs do PC e do usuário vão em pedidos separados, por nível).
   Hosts: sem o registro `.written` do conteúdo escrito, a restauração é recusada em vez de sobrescrever.

## Segurança do desenho (escolha por identificador, nunca por caminho)

- A interface não escreve: só lê, e pede escrita via `Host.ApplyAsync`. O argumento `selection` é uma lista de
  identificadores de 8 hex (`RegistryInspector.SelectionId`: SHA-256 de categoria|hive|visão|caminho|valor, sem SID).
  O catálogo valida por regex (`[0-9a-f]{8}(,[0-9a-f]{8}){0,27}`) e o handler **refaz a varredura** e só remove entradas que
  a regra reconhece AGORA como órfãs/suspeitas e cujo identificador foi pedido. Um identificador de entrada válida (ex.: um
  programa instalado) simplesmente não casa: resultado "já configurado", nada removido (teste).
- Locais permitidos fixos por handler (Uninstall, App Paths, MUICache, SharedDLLs; IFEO\*\Debugger; HKCU Winlogon\Shell),
  revalidados também na restauração (manifesto adulterado apontando para outro lugar é recusado). `SafetyPolicy` do núcleo
  é aplicada a cada entrada.
- Antes de remover: exportação completa (todas as subchaves, tipo e dados de cada valor, inclusive `(Padrão)`, MultiString
  e Binary) gravada em `DataDirectory\quarantine\security\registry\<sha256>.json` (escrita atômica, relida e conferida).
  O estado no diário é `RegistryEntrySet:present:<sha256>`, então a restauração encontra a cópia pelo hash e recusa
  cópia corrompida. Valores de tipos que o NEXUS não sabe recriar exatamente (REG_NONE, listas de recursos) **bloqueiam**
  a remoção.
- Restore nunca sobrescreve: se o programa foi reinstalado e a chave voltou, ela é mantida.
- Aplicador como outra conta (serviço): HKCU vai para `HKU\<SID>` do `userSid`; sem SID, a ação de usuário é bloqueada;
  variáveis de perfil não são expandidas pelo processo do aplicador.

## Riscos

- Heurísticas dão falso positivo (ex.: proxy local de antivírus, `-WindowStyle Hidden` em tarefa de fabricante). Por isso
  são "para revisar", com gravidade conservadora e instruções de como conferir.
- Hosts: linhas colocadas de propósito pelo usuário para domínios protegidos deixam de valer (desfazível).
- Remover IFEO/Shell intencionais (Process Explorer, quiosque) desliga essa personalização (desfazível).
- A leitura usa `powershell.exe` com `-ExecutionPolicy Bypass -EncodedCommand` (o script é fixo, embutido, só lê). Alguns
  EDRs corporativos registram isso como evento; o próprio NEXUS sinaliza `-enc` em tarefas de terceiros, não no seu processo.

## O que NÃO faz, e por quê

- **Motor de assinaturas próprio:** seria pior que o Defender (atualizado várias vezes por dia, nuvem, comportamento).
- **Remover/quarentenar ameaças:** é do Defender, que sabe restaurar falso positivo e lidar com arquivo em uso.
- **Ligar/desligar tempo real, PUA, nuvem, exclusões:** a Proteção contra Adulteração bloqueia mudanças por programas (com
  razão) e `HKLM\SOFTWARE\Microsoft\Windows Defender` e `...\Policies` estão na lista proibida do `SafetyPolicy`. A página
  abre a tela certa da Segurança do Windows.
- **Corrigir `Userinit`:** um erro ali impede o logon; só alerta com o valor padrão esperado.
- **Alterar políticas de navegador (ExtensionInstallForcelist), DataBasePath, proxy:** alerta e explica; `Policies` é proibido.
- **"Limpar o registro" para desempenho:** mito (ver `rejected` no catálogo).
- **Listar atualizações disponíveis do Windows Update:** a busca do agente de atualização leva minutos e faz rede; só lemos
  reinício pendente e abrimos o Windows Update.

## Comparação com o Kudu

| Kudu | NEXUS |
|---|---|
| Malware Scanner | Usa o Defender (rápida/completa/pasta/assinaturas), mostra histórico com recomendação, e acrescenta 9 famílias de heurísticas de persistência/sequestro que o Defender não explica ao usuário; correções reversíveis com diário. |
| Registry Cleaner | Inspetor com regras conservadoras, remoção só do que o usuário escolhe, cópia completa + desfazer, revalidação no aplicador, e explicação honesta de que não acelera nada. |

## Como validar no Windows

1. `dotnet build Nexus.slnx -c Release` e `dotnet test tests/Nexus.Tests -c Release --filter Category=Unit` (testes do módulo:
   `tests/Nexus.Tests/Modules/SecurityTests.cs`).
2. Abrir o NEXUS > Segurança: conferir o placar contra a Segurança do Windows (cada "Abrir no Windows" leva à tela certa).
   Sem administrador, exclusões aparecem como "só visíveis para administrador" e TPM/BitLocker vêm das leituras alternativas.
3. "Verificação rápida": confirmar, ver tempo e mensagens; "Cancelar" encerra só o MpCmdRun.
4. "Inspecionar registro": revisar a lista; remover algo só numa VM (a escrita real continua atrás do portão do aplicador).
5. VM do CI: `tests/Nexus.IntegrationTests/Modules/SecurityIntegrationTests.cs` (órfã de Desinstalação no HKCU com subchave e
   binário, IFEO Debugger e hosts, todos com reversão real; HKLM/hosts só elevado).
