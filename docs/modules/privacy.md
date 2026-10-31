# Módulo Privacidade

Página **Privacidade** (grupo "Proteger"). Código: `src/Nexus.Privacy`, `src/Nexus.Desktop/ViewModels/Modules/PrivacyPage.cs`,
`src/Nexus.Desktop/Views/Pages/Privacy.xaml`. Catálogo: `catalog/privacy.v1.json`. Testes: `tests/Nexus.Tests/Modules/PrivacyTests.cs`
(unitários) e `tests/Nexus.IntegrationTests/Modules/PrivacyIntegrationTests.cs` (VM).

## O que faz

- Lê, sem escrever nada, o estado de **48 itens** (62 ações do catálogo) e mostra um **placar**: itens protegidos sobre itens
  que se aplicam à versão do Windows (ex.: `23/44`). Itens de outra versão (Widgets no Windows 10, "Notícias e interesses" no
  Windows 11, Recall fora do 24H2) ficam fora da conta, como "Não se aplica".
- Agrupa por tema: telemetria e diagnóstico, anúncios e sugestões, atividade e histórico, pesquisa/Copilot/IA, voz e digitação,
  localização/sincronização/apps, Edge e Office. Cada item mostra o que faz, **o que você perde**, se vale só para a sua conta
  ou para o PC (administrador), se a fonte é da Microsoft ou da comunidade, e os detalhes técnicos (chave, valor atual, valor
  protegido, mecanismo, URLs das fontes).
- Perfis:
  - **Recomendado** (31 itens): nada de perda relevante. Ex.: telemetria em "Obrigatórios", ID de publicidade, sugestões e
    propagandas do Iniciar/Configurações/Explorador, histórico de atividades, Bing no Iniciar, destaques da pesquisa, Cortana,
    coleta de digitação, políticas de privacidade do Edge.
  - **Rígido** (+15 itens): perdas reais, todas explicadas na confirmação. Ex.: Relatório de Erros, rastreamento de apps e de
    documentos recentes, pesquisa na nuvem, Copilot, Recall, Widgets, fala online, ativação por voz, sincronização de
    configurações, apps em segundo plano, sugestões de pesquisa do Edge, telemetria do Office.
  - **Personalizado**: os interruptores da lista. Localização e "Encontrar meu dispositivo" são **opcionais**: nunca entram num
    perfil automático.
- "Aplicar" sempre passa pelo aplicador (`Host.ApplyAsync`) com a lista de itens e perdas na confirmação. O aplicador grava o
  diário antes de escrever, relê para conferir, e (para ações de administrador) cria ponto de restauração. Como o aplicador aceita
  até 32 ações por pedido, perfis grandes viram 2 confirmações ("Parte 1 de 2"), sem separar ações de um mesmo item.
- **Desfazer** reverte a última aplicação da página pelo diário (também dá na página Aplicar).
- Câmera, microfone e localização por app: **só leitura** (estado geral do ConsentStore) e botão que abre a tela das
  Configurações. Bloquear às cegas quebraria Discord, chat de voz e transmissões.
- Serviço DiagTrack: só referência à página Serviços (não é duplicado aqui).

## Como funciona (segurança)

- Ações fora de `SOFTWARE\Policies` usam o handler `RegistryValue` do núcleo, com a `SafetyPolicy` geral.
- A `SafetyPolicy` proíbe `HKLM\SOFTWARE\Policies` e `HKCU\SOFTWARE\Policies` inteiros (lá vivem Defender, SmartScreen, Windows
  Update...). Como a maioria dos controles de privacidade documentados são políticas, o módulo tem **um** handler próprio,
  `PrivacyPolicyValue`, que só escreve os **38 valores exatos** (hive + chave + nome do valor) de `PrivacyPolicyAllowlist`.
  O catálogo não amplia essa lista: qualquer outro alvo é bloqueado no Assess, no Apply e na restauração. Os testes conferem que
  a lista não contém Defender/SmartScreen/Windows Update/Control/Services, que cada entrada é usada pelo catálogo e que um valor
  vizinho na mesma chave (ex.: `System\EnableSmartScreen`) é recusado.
- Políticas em HKCU exigem administrador (a chave `Policies` do usuário só é gravável por administradores). Quando o aplicador
  roda com outra conta, o broker grava o SID de quem pediu no alvo e a escrita vai para `HKU\<SID>` (mesmo mecanismo do B10).
- Restaurar "ausente" remove só o valor; a chave só é removida se o NEXUS a criou e ela ficou vazia.
- A página usa os mesmos handlers do aplicador para **detectar**, através de `ReadOnlyRegistryBackend` (qualquer escrita lança
  exceção). "Protegido" na página é exatamente o que o aplicador veria como "já configurado". Alguns itens aceitam mais de um valor
  como protegido (ex.: `AllowTelemetry` 0 no Enterprise protege tanto quanto 1).

## Honestidade (o que NÃO faz e por quê)

- **Não melhora FPS.** Nenhum item é vendido como otimização de desempenho.
- **Telemetria não vai a "zero" no Home/Pro.** O nível "Desligado (Segurança)" (`AllowTelemetry=0`) só existe no Enterprise,
  Education e Server; no Home/Pro o Windows trata como "Obrigatórios". O NEXUS aplica 1 (Obrigatórios) e diz isso.
- **Não desliga o serviço DiagTrack aqui** (fica na página Serviços, com riscos próprios), não bloqueia hosts no arquivo HOSTS
  nem cria regras de firewall (quebram Windows Update, Store e jogos com contas Microsoft, e são difíceis de desfazer com
  segurança), e não mexe em Defender/SmartScreen (proteção, não privacidade).
- **Não bloqueia câmera/microfone/localização de todos os apps de uma vez**: mostra o estado e abre as Configurações.
- **Políticas aparecem como "gerenciado pela sua organização"** nas Configurações e no Edge. É só um aviso; o Desfazer remove.
- **Várias políticas de "experiências do consumidor" só valem no Enterprise/Education** (`DisableWindowsConsumerFeatures`,
  `DisableSoftLanding`, Spotlight). Para o Home/Pro, o NEXUS usa os valores por usuário que as próprias Configurações gravam
  (ContentDeliveryManager etc.). Esses valores **não são documentados pela Microsoft**: a fonte é comunidade (Sophia Script), a
  classificação é `Conditional` e a página mostra "parte com fonte da comunidade". Atualizações grandes podem religá-los; o placar
  mostra o item exposto de novo.
- **Recall:** a Microsoft documenta que ativar `DisableAIDataAnalysis` **apaga** as capturas já salvas. Desfazer a política não as
  traz de volta. Por isso o item está só no Rígido, com aviso.
- **Copilot:** `TurnOffWindowsCopilot` é obsoleta segundo a Microsoft e não cobre o novo app Copilot (desinstalável na página
  Programas).
- **Office:** as políticas são documentadas para o Microsoft 365 Apps; em outras edições o efeito pode variar (classificação
  `Conditional`).
- **"Não rastrear" do Edge:** a maioria dos sites ignora; é um sinal, não um bloqueio.

## Como desfazer

1. Botão **Desfazer a última aplicação** na página (reverte cada parte, em ordem inversa, pelo diário).
2. Página **Aplicar** (histórico de execuções).
3. Para ações de administrador, o ponto de restauração criado antes.
4. Itens de usuário também podem ser religados pelas Configurações do Windows; políticas, só pelo Desfazer (ou removendo o valor).

## Como validar no Windows

- `dotnet build Nexus.slnx -c Release` e `dotnet test tests/Nexus.Tests -c Release --filter "FullyQualifiedName~PrivacyTests"`.
- Abrir o NEXUS → Privacidade: o placar aparece sem pedir administrador (só leitura). Conferir 2 ou 3 itens nos "Detalhes
  técnicos" contra o `regedit`.
- Escrita real: só na VM do CI (`PrivacyIntegrationTests`, categoria `VmIntegration`): aplica um item de usuário e a política
  `AllowTelemetry` pelo `BrokerService`, confere no registro, reverte e confere o valor original (inclusive "ausente").

## Comparação com o Kudu Privacy Shield

O Kudu anuncia "30+ ajustes" de privacidade. Aqui são 48 itens / 62 ações, cada um com fonte (URL), build mínimo/máximo,
escopo (usuário/PC), risco, o que se perde, detecção do estado atual, placar, perfis e desfazer pelo diário. Itens que o NEXUS
deliberadamente não faz (HOSTS, firewall, DiagTrack aqui, câmera/microfone em bloco) estão listados acima com o motivo.

## Itens do catálogo

### Telemetria e diagnóstico

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Dados de diagnóstico no mínimo (Obrigatórios) | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry` = 1 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system#allowtelemetry), [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization#diagnostic-data-settings), [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-feedback) | O Programa Windows Insider exige dados opcionais. O Visualizador de Dados de Diagnóstico fica com menos coisa para mostrar. |
| Pedidos de feedback do Windows | Recomendado | `HKCU\Software\Microsoft\Siuf\Rules\NumberOfSIUFInPeriod` = 0<br>`HKCU\Software\Microsoft\Siuf\Rules\PeriodInNanoSeconds` = 0<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\DoNotShowFeedbackNotifications` = 1 | Admin/Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-feedback), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#donotshowfeedbacknotifications) | Nada relevante. O Hub de Feedback continua funcionando quando você o abre por conta própria. |
| Experiências personalizadas com seus dados de diagnóstico | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy\TailoredExperiencesWithDiagnosticDataEnabled` = 0<br>`HKCU\SOFTWARE\Policies\Microsoft\Windows\CloudContent\DisableTailoredExperiencesWithDiagnosticData` = 1 (build ≥ 15063) | Admin/Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowtailoredexperienceswithdiagnosticdata), [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-feedback) | Nada relevante: as dicas podem continuar aparecendo, só deixam de ser personalizadas. |
| Programa de Aperfeiçoamento da Experiência (CEIP) | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\SQMClient\Windows\CEIPEnable` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-icm#ceipenable) | Nada. No Windows 10/11 o CEIP foi praticamente substituído pelos dados de diagnóstico: o efeito é pequeno, mas não há perda. |
| Apps lendo diagnóstico de outros apps | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsGetDiagnosticInfo` = 2 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-diag), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#letappsgetdiagnosticinfo) | Apps da Store que monitoram outros apps (raros) deixam de ver essas informações. Programas tradicionais (.exe) não são afetados. |
| Relatório de Erros do Windows (WER) | Rígido | `HKCU\Software\Microsoft\Windows\Windows Error Reporting\Disabled` = 1 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/win32/wer/wer-settings) | Você perde as soluções sugeridas após travamentos e o histórico de problemas no Monitor de Confiabilidade. Fabricantes deixam de receber o relatório do seu travamento, o que pode atrasar correções de jogos e drivers. |

### Anúncios e sugestões

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| ID de publicidade | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo\Enabled` = 0<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo\DisabledByGroupPolicy` = 1 | Admin/Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-general), [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#disableadvertisingid) | Nada: apps com anúncios continuam com anúncios, só deixam de cruzar seus dados entre apps. |
| Sites lendo sua lista de idiomas | Recomendado | `HKCU\Control Panel\International\User Profile\HttpAcceptLanguageOptOut` = 1 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-general) | Alguns sites podem abrir no idioma errado na primeira visita. |
| Sugestões de apps no Iniciar (Windows 10) | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338388Enabled` = 0 (build ≤ 19045) | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_10/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowwindowsconsumerfeatures) | Nada. |
| Recomendações de dicas e apps no Iniciar (Windows 11) | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_IrisRecommendations` = 0 (build ≥ 22621) | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#33-recommendations) | Nada relevante: arquivos recentes continuam (são outro item). |
| Dicas e sugestões ao usar o Windows | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338389Enabled` = 0 | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowwindowstips) | Nada relevante. |
| Conteúdo sugerido no app Configurações | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-338393Enabled` = 0<br>`HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-353694Enabled` = 0<br>`HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-353696Enabled` = 0 | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1) | Nada. |
| Instalação automática de apps sugeridos | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SilentInstalledAppsEnabled` = 0 | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowwindowsconsumerfeatures) | Nada. Não desinstala o que já veio; para isso use a página Programas. |
| Boas-vindas e "concluir configuração" após atualizações | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SubscribedContent-310093Enabled` = 0<br>`HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement\ScoobeSystemSettingEnabled` = 0 | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1) | Nada relevante. |
| Sugestões de terceiros no Windows Spotlight | Recomendado | `HKCU\SOFTWARE\Policies\Microsoft\Windows\CloudContent\DisableThirdPartySuggestions` = 1 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowthirdpartysuggestionsinwindowsspotlight) | Nada. Sugestões de produtos da própria Microsoft podem continuar. |
| Notificações do provedor de sincronização no Explorador | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\ShowSyncProviderNotifications` = 0 | Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1) | Nada. O OneDrive continua funcionando normalmente. |

### Atividade e histórico

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Histórico de atividades (Linha do Tempo) | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableActivityFeed` = 0<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\System\PublishUserActivities` = 0<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\System\UploadUserActivities` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-act-history), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#enableactivityfeed), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#publishuseractivities), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#uploaduseractivities) | A Linha do Tempo do Windows 10 e o "continuar de onde parou" entre PCs deixam de funcionar. No Windows 11 a Linha do Tempo já não existe. |
| Área de transferência na nuvem | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\AllowCrossDeviceClipboard` = 0 (build ≥ 17763) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-clcp), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#allowcrossdeviceclipboard) | Copiar num PC e colar em outro deixa de funcionar. O histórico local (Win+V) continua. |
| Rastreamento de apps abertos | Rígido | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackProgs` = 0 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-general) | A lista "Mais usados" do Iniciar fica vazia e a pesquisa deixa de priorizar seus apps frequentes. |
| Itens abertos recentemente | Rígido | `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackDocs` = 0 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#33-recommendations) | Você perde os atalhos para arquivos recentes (Iniciar, barra de tarefas, Explorador). É conveniência, não segurança. |

### Pesquisa, Copilot e IA

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Bing e resultados da web na pesquisa do Iniciar | Recomendado | `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\BingSearchEnabled` = 0<br>`HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions` = 1 (build ≥ 19041) | Admin/Usuário | [comunidade](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-windowsexplorer#disablesearchboxsuggestions), [observado](https://github.com/farag2/Sophia-Script-for-Windows/blob/master/src/Sophia_Script_for_Windows_11/Module/Sophia.psm1) | A pesquisa do Iniciar deixa de mostrar resultados da web. O Explorador para de sugerir pesquisas recentes na caixa de busca. |
| Destaques da pesquisa | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\EnableDynamicContentInWSB` = 0 (build ≥ 19042) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search#allowsearchhighlights) | Nada relevante. |
| Pesquisa usando sua localização | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\AllowSearchToUseLocation` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-cortana-gp), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search#allowsearchtouselocation) | Resultados "perto de você" na pesquisa deixam de existir. |
| Cortana | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\AllowCortana` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-cortana-gp), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowcortana) | Nada para a maioria: o app Cortana foi descontinuado em 2023. |
| Pesquisa em OneDrive e contas na nuvem | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\AllowCloudSearch` = 0 (build ≥ 16299) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search#allowcloudsearch) | Arquivos que estão só na nuvem deixam de aparecer na pesquisa do Iniciar. |
| Copilot no Windows (painel antigo) | Rígido | `HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot\TurnOffWindowsCopilot` = 1 (build ≥ 19044) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai#turnoffwindowscopilot) | Você perde o Copilot integrado. A Microsoft avisa que esta política é obsoleta e não cobre o novo app Copilot (que pode ser desinstalado na página Programas). |
| Recall (capturas de tela com IA) | Rígido | `HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis` = 1 (build ≥ 26100) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai#disableaidataanalysis) | Você perde o Recall. ATENÇÃO: a Microsoft documenta que, ao ativar esta política, as capturas já salvas são apagadas. Desfazer a política não as traz de volta. |
| Widgets (Windows 11) | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests` = 0 (build ≥ 22000) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-newsandinterests#allownewsandinterests), [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-widgets) | Você perde o painel de Widgets (clima, notícias, placares) e o botão na barra de tarefas. |
| Notícias e interesses na barra de tarefas (Windows 10) | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds\EnableFeeds` = 0 (build ≤ 19045) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-news) | Você perde o clima e as manchetes na barra de tarefas. |

### Voz, digitação e escrita

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Personalização de escrita à mão e digitação | Recomendado | `HKCU\Software\Microsoft\InputPersonalization\RestrictImplicitTextCollection` = 1<br>`HKCU\Software\Microsoft\InputPersonalization\RestrictImplicitInkCollection` = 1 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-ink) | As sugestões de texto e o reconhecimento de escrita à mão deixam de aprender palavras suas (nomes, gírias). |
| Envio de dados de digitação para a Microsoft | Recomendado | `HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\TextInput\AllowLinguisticDataCollection` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-textinput#allowlinguisticdatacollection), [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-ink) | Nada para você; só deixa de contribuir para os modelos da Microsoft. |
| Reconhecimento de fala online | Rígido | `HKCU\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy\HasAccepted` = 0 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-speech) | A digitação por voz (Win+H) e assistentes que dependem do reconhecimento na nuvem deixam de funcionar ou pioram muito. O Acesso por Voz offline continua. |
| Apps ativados por voz | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsActivateWithVoice` = 2 (build ≥ 17134)<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsActivateWithVoiceAboveLock` = 2 (build ≥ 17134) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-voice-act), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#letappsactivatewithvoice), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#letappsactivatewithvoiceabovelock) | Assistentes ativados por voz ("Ei, ...") deixam de responder à palavra de ativação. |

### Localização, sincronização e apps

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Download automático de mapas offline | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Maps\AutoDownloadAndUpdateMapData` = 0<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\Maps\AllowUntriggeredNetworkTrafficOnSettingsPage` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-offlinemaps) | Mapas offline só atualizam quando você pedir. |
| Backup de mensagens na nuvem | Recomendado | `HKCU\SOFTWARE\Microsoft\Messaging\CloudServiceSyncEnabled` = 0 | Usuário | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-syncsettings) | Nada para a maioria dos PCs. |
| Sincronizar configurações entre PCs | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Windows\SettingSync\DisableSettingSync` = 2<br>`HKLM\SOFTWARE\Policies\Microsoft\Windows\SettingSync\DisableSettingSyncUserOverride` = 1 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-syncsettings), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-settingsync#disablesettingsync) | Ao entrar em outro PC com a mesma conta, suas preferências não vêm junto. No Windows 11 o "Backup do Windows" pode continuar oferecendo parte disso. |
| Apps da Store em segundo plano | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsRunInBackground` = 2 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-background), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#letappsruninbackground) | Apps da Store deixam de receber notificações e de atualizar em segundo plano (Mail, Phone Link, Xbox, WhatsApp da Store...). Programas tradicionais (.exe), Steam e launchers não são afetados. |
| Serviço de localização (opcional) | Opcional | `HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors\DisableLocation` = 1 | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#bkmk-priv-location) | Clima e mapas não acham sua cidade, o fuso horário automático e o "Encontrar meu dispositivo" param, e apps que pedem localização deixam de recebê-la. |
| Encontrar meu dispositivo (opcional) | Opcional | `HKLM\SOFTWARE\Policies\Microsoft\FindMyDevice\AllowFindMyDevice` = 0 (build ≥ 15063) | Admin | [Microsoft](https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#find-my-device), [Microsoft](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowfindmydevice) | Se o notebook for perdido ou roubado, você não consegue localizá-lo pela conta Microsoft. |

### Microsoft Edge

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Edge: dados de diagnóstico só obrigatórios | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Edge\DiagnosticData` = 1 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/diagnosticdata) | Nada relevante. O Edge continua seguro e atualizado. |
| Edge: anúncios e notícias personalizados | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Edge\PersonalizationReportingEnabled` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/personalizationreportingenabled) | Nada relevante: o conteúdo só fica menos personalizado. |
| Edge: recomendações e notificações promocionais | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Edge\ShowRecommendationsEnabled` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/showrecommendationsenabled) | Nada. |
| Edge: pedidos de feedback | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Edge\UserFeedbackAllowed` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/userfeedbackallowed) | Você não consegue mais enviar feedback pelo próprio Edge. |
| Edge: assistente de compras | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Edge\EdgeShoppingAssistantEnabled` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/edgeshoppingassistantenabled) | Você perde os cupons e as comparações automáticas do Edge. |
| Edge: enviar "Não rastrear" | Recomendado | `HKLM\SOFTWARE\Policies\Microsoft\Edge\ConfigureDoNotTrack` = 1 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/configuredonottrack) | Nada. Honestamente, a maioria dos sites ignora esse pedido; é um sinal, não um bloqueio. |
| Edge: sugestões de pesquisa enquanto digita | Rígido | `HKLM\SOFTWARE\Policies\Microsoft\Edge\SearchSuggestEnabled` = 0 | Admin | [Microsoft](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-policies/searchsuggestenabled) | A barra de endereço deixa de sugerir pesquisas da web enquanto você digita (histórico e favoritos continuam). |

### Microsoft Office

| Item | Perfil | Onde (valor = protegido) | Privilégio | Fonte | O que você perde |
|---|---|---|---|---|---|
| Office: dados de diagnóstico | Rígido | `HKCU\SOFTWARE\Policies\Microsoft\office\common\clienttelemetry\SendTelemetry` = 3 | Admin | [Microsoft](https://learn.microsoft.com/en-us/microsoft-365-apps/privacy/manage-privacy-controls#control-privacy-settings-by-editing-the-registry) | Nada no uso normal. A Microsoft avisa que isso limita a capacidade dela de diagnosticar problemas do seu Office. |
| Office: experiências conectadas opcionais | Rígido | `HKCU\SOFTWARE\Policies\Microsoft\office\16.0\common\privacy\ControllerConnectedServicesEnabled` = 2 | Admin | [Microsoft](https://learn.microsoft.com/en-us/microsoft-365-apps/privacy/manage-privacy-controls#control-privacy-settings-by-editing-the-registry) | Você perde esses recursos opcionais (inserir imagens online, Mapas 3D, alguns recursos de pesquisa). |
