# Módulo Ferramentas do sistema (`Nexus.SystemTools`)

Página **Ferramentas do sistema** (grupo "Sistema"). Cinco seções: Rede, Redes Wi-Fi salvas, Drivers no repositório do
Windows, Apps pré-instalados e Pontos de restauração. Nada aqui acelera jogos; são ferramentas de manutenção, e a página
diz isso.

- Leitura (contadores, inventários): direto da biblioteca, somente leitura (`SystemToolsReader`).
- Escrita: só pelo aplicador, como ações do catálogo `catalog/system.v1.json` (`Host.ApplyAsync` / `Host.RevertAsync`).
- Exceção controlada (abrir ferramenta do próprio Windows que o usuário vê, depois de `Host.Confirm`): Restauração do
  Sistema (`rstrui.exe`), Proteção do Sistema, Configurações de rede, página do app na Microsoft Store, pasta de backups
  (`VendorTools`).

## Ações do catálogo

| Id | Handler | Privilégio | O que faz | Desfazer |
|---|---|---|---|---|
| `system.network.dns-flush` | `SystemDnsFlush` | Usuário | `DnsFlushResolverCache` (= `ipconfig /flushdns`) | Nada a restaurar (efêmera) |
| `system.network.arp-flush` | `SystemArpFlush` | Admin | `netsh interface ip delete arpcache` | Nada a restaurar (efêmera) |
| `system.wifi.profile-remove` | `SystemWifiProfile` | Admin | Exporta o perfil (WlanGetProfile, chave em texto) → DPAPI → apaga (WlanDeleteProfile) | Reimporta (WlanSetProfile) |
| `system.driver.package-remove` | `SystemDriverPackage` | Admin | `pnputil /export-driver oemNN.inf` → `pnputil /delete-driver oemNN.inf` (sem `/force`) | `pnputil /add-driver <inf> /install` |
| `system.driver.backup-all` | `SystemDriverBackup` | Admin | `pnputil /export-driver *` para pasta datada | Nada a desfazer (os arquivos ficam) |
| `system.restore.create-point` | `SystemRestorePoint` | Admin | `SRSetRestorePointW` + confere na lista de pontos | Nada a desfazer (efêmera) |
| `system.appx.remove-user` | `SystemAppxPackage` | Usuário | `Remove-AppxPackage` do usuário (lista fechada) | LIMITADO: `Add-AppxPackage -Register` se os arquivos ainda estão no PC; senão, link da Store |

Parâmetros escolhidos pelo usuário (`userParameters`, validados pelo broker e revalidados pelo handler com `\z`):
`wifiProfile` (sem caracteres de controle, até 255), `driverInf` (`oem[0-9]{1,5}\.inf`), `appxName`
(`[A-Za-z0-9][A-Za-z0-9.\-]{2,99}`, e ainda precisa estar na lista do `AppxCatalog`).

### Ações "efêmeras"

DNS, ARP, backup de todos os drivers e criar ponto de restauração são `"persistence": "Ephemeral"` (núcleo, rodada 2):
o executor não lê estado antes nem relê depois; registra a intenção no diário, executa e marca Verified se o handler não
lançar. Não disparam o ponto de restauração automático do aplicador (antes, ARP/backup/ponto, por serem Admin + Persistent,
criavam um ponto antes de criar um ponto), não prendem sessão, e "Desfazer" responde explicitamente "ação sem estado: nada a
desfazer" sem chamar o handler. Interrompidas por queda, a recuperação as encerra como aceitas. O antigo marcador de 15 s
(`EphemeralMarker`) foi removido; entradas antigas do diário (Persistent, original `Ephemeral:pronto`) continuam
desfazendo como "já no original", sem escrever nada.

### Lote (várias redes, drivers ou apps numa confirmação)

A página tem caixas de seleção em Wi-Fi, drivers e apps e o botão "Remover selecionados": um pedido só ao aplicador, com a
mesma ação repetida (uma instância por item, cada uma com seu alvo, seu backup e sua entrada no diário). Uma confirmação, um
aviso do Windows (UAC) e um ponto de restauração automático para o lote todo. Se um item falha, os outros seguem (falha de
um item não desfaz os demais; cada um é compensado sozinho). "Desfazer lote" reverte a execução inteira em ordem inversa; o
"Desfazer" de cada linha continua disponível e também desfaz o lote em que ela foi removida (o diário é por execução).
Até 64 itens por pedido.

## Evidências

- DNS: <https://learn.microsoft.com/windows-server/administration/windows-commands/ipconfig>
- ARP: <https://learn.microsoft.com/windows-server/administration/windows-commands/netsh-interface>,
  <https://learn.microsoft.com/windows-server/administration/windows-commands/arp>
- Wi-Fi: WlanGetProfile / WlanDeleteProfile / WlanSetProfile (learn.microsoft.com/windows/win32/api/wlanapi/)
- Drivers: <https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-command-syntax>
- Ponto de restauração: SRSetRestorePointW; limite de frequência (24 h) em CreateRestorePoint (SystemRestore)
- Appx: Remove-AppxPackage / Add-AppxPackage (learn.microsoft.com/powershell/module/appx/)
- Reset de Winsock (não implementado): <https://learn.microsoft.com/windows-server/administration/windows-commands/netsh-winsock>

## Proteções

**Wi-Fi.** Perfis de política de grupo nunca são removidos. O backup (XML com a senha) só é gravado protegido por DPAPI
(escopo do usuário; escopo da máquina quando o aplicador roda como outra conta, ex.: serviço) em
`DataDirectory\backup\wifi\<hash>.wifi.json`, relido e conferido ANTES de apagar. Com o aplicador como outra conta, perfis
"só deste usuário" não são visíveis ao serviço: a ação diz isso (NotApplicable) e recusa perfis por usuário (Blocked).
Restore não sobrescreve um perfil de mesmo nome criado depois; se o adaptador mudou, reimporta no adaptador presente.

**Drivers.** Só `oemNN.inf` (terceiros). Nunca: pacote em uso (`pnputil /enum-devices`, Windows 10 2004+; sem essa leitura,
nada é removido), classes críticas (discos, controladoras IDE/SATA/armazenamento, volumes, sistema, processadores, firmware,
teclados, USB, SBP2), classe desconhecida, e vídeo/rede sem uma versão MAIS NOVA do mesmo pacote (mesmo INF original,
fornecedor e classe). A remoção é sem `/force`: o próprio Windows recusa se algum dispositivo usa o pacote; nesse caso o
executor registra "falhou, nada mudou". O backup (`DataDirectory\backup\drivers\oemNN\` + `nexus-manifest.json`) é
conferido (o INF original precisa existir) antes de remover; backup anterior com o mesmo nome é preservado com sufixo.
Ao reinstalar, o Windows pode dar outro `oemMM.inf`: Detect reconhece o mesmo pacote e versão pelo manifesto.

**Apps.** Lista fechada (`AppxCatalog`): Seguros (Candy Crush/King, TikTok, Clipchamp, Notícias/Clima/Bing, Solitaire,
Obter Ajuda, Dicas, Hub de Comentários, Office Hub, Skype, Cortana, Copilot, Spotify/Disney+/Prime Video/Facebook
promocionais, McAfee promocional...) e Atenção (Media Player, Link to Phone, Email e Calendário, Outlook, Notas,
Assistência Rápida, Xbox/Game Bar — "só se não usa Game Pass" —, Fotos, Calculadora, Câmera, Paint, Captura...). Proteção
vence a lista: Store, StorePurchaseApp, Instalador de Aplicativo (winget), VCLibs, .NET, UI.Xaml, Windows App SDK,
Segurança do Windows, Xbox Identity Provider e TCUI (jogos usam para login), Edge/WebView, codecs, idiomas, componentes do
shell e `Windows.*`/`MicrosoftWindows.*`. Também nunca: framework, `NonRemovable`, assinatura `System`, ou fora da lista.
Nunca remove o pacote provisionado (afeta contas novas). O nome completo do pacote é validado por regex antes de ir ao
PowerShell; o script vai por `-EncodedCommand` e os valores entre aspas simples escapadas.
Reversão LIMITADA, dita na confirmação: `Add-AppxPackage -Register <InstallLocation>\AppxManifest.xml` só se a pasta
ainda existe em `WindowsApps`/`SystemApps`; senão a restauração falha com o link `ms-windows-store://pdp/?PFN=...` (a
página oferece "Abrir na Store"). Com o aplicador como outra conta, a remoção usa `-User <SID>` (SID ligado pelo broker,
porque o alvo tem `scope: HKCU`) e a restauração recusa (o Windows só registra o app na sessão do próprio usuário).

**Ponto de restauração.** Status do Windows traduzido (1058 = Proteção do Sistema desligada; Windows Server não tem o
recurso). Se o Windows aceitar mas pular (limite de 24 h), a ação só é "feita" se houver um ponto criado nos últimos 10
minutos (ex.: o que o aplicador criou antes de aplicar); senão falha com a explicação.

## O que NÃO faz, e por quê

- **Reset de Winsock/TCP-IP** (`netsh winsock reset`, `netsh int ip reset`): exige reinício, apaga IP/DNS/VPN manuais e
  não tem reversão segura; não acelera nada. A página explica e abre as Configurações de rede (Redefinição de rede do
  Windows). Registrado como `rejected` no catálogo.
- **Remover pacotes provisionados**: muda a imagem para todas as contas novas; ganho só de espaço. `rejected`.
- **Remover app fora da lista**: sem saber o que depende dele, não remove.
- **`pnputil /delete-driver /force`** ou remover driver em uso: nunca.
- **Restaurar o sistema pelo NEXUS**: abre o `rstrui.exe`, onde o próprio Windows mostra o que muda e pede confirmação.
- **Contar o cache DNS por `DnsGetCacheDataTable`** (API não documentada, com risco de vazamento/queda): a contagem vem de
  `ipconfig /displaydns` (linhas de traços, independente do idioma); se não der, mostra "n/d".
- Pedir duas vezes o mesmo item no mesmo lote: o broker recusa a duplicata exata (mesma ação e mesmo alvo).

## Comparação com o Kudu

| Kudu | NEXUS |
|---|---|
| Network Cleanup (flush DNS/ARP, reset Winsock) | DNS (sem admin) e ARP com contagem antes/depois; Winsock explicado e delegado ao Windows (sem ação irreversível) |
| (sem Wi-Fi) | Perfis Wi-Fi com última conexão, remoção com backup criptografado e reimportação |
| Driver Manager | Inventário com classe, fornecedor, versão, data, em uso, versões antigas duplicadas; remoção só de antigos sem uso, com backup e reinstalação; backup de todos |
| Debloater | Lista fechada com "seguro"/"atenção", proteções explícitas, só usuário, reversão limitada dita antes |
| Restore Points | Lista, status da Proteção do Sistema por unidade e política, criar com verificação real, abrir a Restauração do Sistema |

## Validar no Windows

1. `dotnet build Nexus.slnx -c Release` e `dotnet test tests/Nexus.Tests -c Release --no-build --filter Category=Unit`.
2. Abrir o NEXUS → Ferramentas do sistema: conferir contadores DNS/ARP, lista Wi-Fi (rótulos em pt), drivers ("em uso",
   "versão antiga"), apps da lista e pontos (sem admin, a lista de pontos diz que exige administrador).
3. Em VM: GitHub Actions → vm-integracao. `SystemToolsIntegrationTests` tem `Category=VmIntegration` e roda no passo
   de integração já existente (DNS real; Wi-Fi pulado sem adaptador; driver sem uso removido e reinstalado se a VM tiver
   um; backup de drivers; ponto de restauração criado ou motivo explicado — o Windows Server do CI não tem o recurso;
   inventário de apps).
