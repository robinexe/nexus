# Ajustes de desempenho (catalog/tuning.v1.json)

Pesquisa de 01/11/2026 em docs da Microsoft, guias do GitHub (winutil, Win11Debloat, Sophia, Atlas, ReviOS, valleyofdoom/PC-Tuning, djdallmann),
fóruns e medições publicadas (Tom's Hardware, TechSpot, PCGH, Blur Busters). Regra: só entra o que tem mecanismo e fonte; o ganho é dito
com honestidade (a maioria é PEQUENO). Tudo passa pelo aplicador, com diário e desfazer.

## Ações

| id | título | classe | reinício |
|---|---|---|---|
| `fps.dwm.mpo.disable` | Desligar o Multiplane Overlay (corrige tremedeira e tela preta) | Conditional | Required |
| `fps.dwm.overlay-minfps.zero` | Corrigir apps congelando no 24H2 (Discord, Chrome) | Experimental | Required |
| `fps.mouse.acceleration.off.speed` | Mouse sem aceleração (1/3: precisão do ponteiro) | SupportedDocumented | NextLogon |
| `fps.mouse.acceleration.off.threshold1` | Mouse sem aceleração (2/3: limite 1) | SupportedDocumented | NextLogon |
| `fps.mouse.acceleration.off.threshold2` | Mouse sem aceleração (3/3: limite 2) | SupportedDocumented | NextLogon |
| `fps.power.throttling.off` | Desligar o Power Throttling (modo eficiência automático) | Conditional | Required |
| `fps.power.user-presence-qos.off` | Não rebaixar o app em foco quando você fica sem mexer (bateria) | SupportedDocumented | Required |
| `fps.power.min-processor-state.100.ac` | Estado mínimo do processador em 100% (na tomada) | Conditional | None |
| `fps.power.epp.performance.ac` | Preferência de energia do processador: desempenho (na tomada) | Conditional | None |
| `fps.power.usb-selective-suspend.off.ac` | Não suspender portas USB (na tomada) | Conditional | None |
| `fps.power.pcie-aspm.off.ac` | PCIe sem economia de energia no link (na tomada) | Conditional | None |
| `hw.nvme.latency-tolerance.ac` | NVMe sem estados de energia profundos (na tomada) | Experimental | None |
| `fps.gamedvr.policy.off` | Política: sem gravação de jogos em segundo plano | SupportedDocumented | NextLogon |
| `sys.do.download-mode.http-only` | Atualizações sem enviar para outros PCs (Otimização de Entrega) | SupportedDocumented | Recommended |
| `sys.do.background-bandwidth.cap50` | Updates em segundo plano usando no máximo 50% da internet | Conditional | None |
| `sys.ai.recall-enablement.off` | Recall indisponível | SupportedDocumented | Recommended |
| `sys.ai.clicktodo.off` | Desligar o Click to Do | SupportedDocumented | NextLogon |
| `sys.explorer.startup-delay.off` | Programas da inicialização abrem sem atraso | Experimental | NextLogon |
| `sys.ui.transparency.off` | Desligar transparência das janelas | SupportedDocumented | None |
| `sys.ui.minimize-animation.off` | Sem animação ao minimizar e maximizar | SupportedDocumented | NextLogon |
| `sys.ui.taskbar-animations.off` | Sem animações da barra de tarefas | SupportedDocumented | NextLogon |
| `sys.ui.menu-show-delay.200` | Submenus abrem mais rápido (400→200 ms) | SupportedDocumented | NextLogon |
| `sys.ntfs.last-access.disable` | NTFS sem gravar 'último acesso' a cada leitura | Conditional | Required |
| `sys.power.fast-startup.off` | Desligar a Inicialização Rápida (desligar = desligar de verdade) | SupportedDocumented | None |
| `sys.explorer.show-extensions` | Mostrar extensões de arquivo (.exe, .pdf…) | SupportedDocumented | None |
| `sys.notifications.toasts.off` | Desligar todas as notificações pop-up | Conditional | None |
| `net.ipv6.prefer-ipv4` | Preferir IPv4 sem desligar o IPv6 | Conditional | Required |
| `sys.svc.pcasvc.disabled` | Desativar o Assistente de Compatibilidade de Programas | Conditional | Recommended |
| `sys.svc.trkwks.disabled` | Desativar o Cliente de Rastreamento de Link Distribuído | SupportedDocumented | Recommended |

## Mitos (não entram, aparecem como 'não fazer')

- `myth.tuning.network-throttling-index`: NetworkThrottlingIndex limita só tráfego acima de ~10.000 pacotes/s enquanto há mídia tocando; jogo manda dezenas a centenas. Medição mostrou DPC do ndis.sys pior sem o limite. Não reduz ping. (https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/vista-multimedia-playback-and-network-throughput)
- `myth.tuning.tcpackfrequency-nagle`: TcpAckFrequency/TCPNoDelay valem só para TCP; jogos de ação usam UDP, e quando usam TCP já desligam o Nagle no próprio socket. A Microsoft não recomenda mudar. (https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/registry-entry-control-tcp-acknowledgment-behavior)
- `myth.tuning.mmcss-games-priority`: Na tarefa MMCSS 'Games', GPU Priority e SFIO Priority não são usados (documentação da Microsoft) e nenhum jogo observado registra threads nela. (https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service)
- `myth.tuning.disable-paging-executive`: DisablePagingExecutive/DisablePageCombining só restringem o gerenciador de memória; sem ganho medido. (https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/pushing-the-limits-of-windows-virtual-memory)
- `myth.tuning.disable-pagefile`: Ter arquivo de paginação libera RAM útil e é necessário para despejo de falha. Desligar não dá desempenho. (https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows)
- `myth.tuning.dns-lowers-ping`: DNS só resolve o nome antes de conectar; depois de conectado não afeta o ping do jogo. (https://adguard-dns.io/en/blog/best-dns-servers-for-gaming.html)
- `myth.tuning.qos-reserves-20-percent`: A Microsoft desmente que o Agendador de Pacotes QoS reserve 20% da banda. (https://ftp.zx.net.nz/pub/archive/ftp.microsoft.com/MISC/KB/en-us/316/666.HTM)
- `myth.tuning.disable-maintenance`: A manutenção automática só roda com o PC ocioso e é ela que faz TRIM/otimização de disco. Desligar prejudica. (https://learn.microsoft.com/en-us/windows/win32/taskschd/task-maintenence)
- `myth.tuning.debloat-iso-fps`: Comparações Windows 11 normal vs 'debloated' (Nano11) ficaram dentro da margem de erro em FPS; só a interface fica mais ágil. (https://www.pcgameshardware.de/Windows-Software-277633/News/Standard-gegen-Nano-Debloat-Gaming-Performance-1472429/)
- `myth.tuning.hvci-off-silently`: Desligar Integridade de Memória (HVCI) dá 2-10% em alguns jogos, mas reduz a proteção e anticheats como Vanguard/FACEIT podem exigir HVCI. O NEXUS protege DeviceGuard por política: só a página de Segurança do Windows deve mudar isso, com o usuário ciente. (https://support.microsoft.com/en-us/windows/options-to-optimize-gaming-performance-in-windows-11-a255f612-2949-4373-a566-ff6f3f474613)

## Ficou de fora (precisa de handler novo ou decisão)

- Perfis do driver NVIDIA (Low Latency, limite de FPS, V-Sync para G-SYNC, modo de energia por jogo): handler NVAPI DRS.
- Padrões globais do DirectX (otimizações em janela, VRR, Auto HDR): merge na string DirectXUserGlobalSettings.
- Wi-Fi sem economia na bateria (valor DC do plano): o handler de energia só escreve AC.
- Tarefas agendadas da Microsoft (Compatibility Appraiser, CEIP, DiskDiagnostic): o handler recusa tarefas \\Microsoft\\ por desenho.
- Horário ativo do Windows Update: o handler de políticas não toca WindowsUpdate por desenho (segurança).
- Integridade de Memória (HVCI) / VBS: DeviceGuard é protegido pela SafetyPolicy; ganho real 2-10%, mas reduz proteção e anticheats exigem.
- MSI mode, afinidade de interrupção, propriedades avançadas da placa de rede: laboratório, risco de tela azul.
- Diagnósticos (só informar): ReBAR (obrigatório em Intel Arc), RAM em canal único, XMP/EXPO desligado, MUX em notebook.
