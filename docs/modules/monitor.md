# Módulo Monitor (`src/Nexus.Monitoring`, página "Monitor" no grupo Medir)

Monitor de desempenho ao vivo, **somente leitura**. Não há ação de catálogo nem handler de escrita
(`MonitoringHandlers.Create` devolve uma lista vazia de propósito; um teste garante isso).

## O que faz

| Área | O que mostra | Fonte |
|---|---|---|
| CPU | total, por núcleo lógico (barras), frequência efetiva e base | PDH `\Processor Information(*)\% Processor Utility` (cai para `% Processor Time` se faltar), `% Processor Performance` × `Processor Frequency` |
| Memória | em uso (total − disponível), disponível, cache, comprometida/limite | `GlobalMemoryStatusEx` + `K32GetPerformanceInfo` |
| Disco | % ativo (100 − `% Idle Time`), leitura/escrita B/s, fila, por disco físico (com as letras dos volumes) | PDH `\PhysicalDisk(*)\…` |
| Rede | ↓/↑ B/s por adaptador, % do link | PDH `\Network Interface(*)\Bytes Sent/sec`, `Bytes Received/sec`, `Current Bandwidth` |
| GPU | uso por motor (3D, cópia, codificação/decodificação de vídeo…), uso por processo, VRAM dedicada em uso | PDH `\GPU Engine(*)\Utilization Percentage`, `\GPU Adapter Memory(*)\Dedicated Usage` |
| Processos | os 8 mais pesados por CPU ou memória, com GPU | `Process.GetProcesses` (diferença de `TotalProcessorTime` / tempo / nº de processadores lógicos) |
| Saúde dos discos | OK / Atenção / Crítico com motivos | `Get-PhysicalDisk` + `Get-StorageReliabilityCounter` (= `MSFT_PhysicalDisk` + `MSFT_StorageReliabilityCounter`, `root\Microsoft\Windows\Storage`) |
| Temperaturas | GPU NVIDIA (temperatura, uso, clock, potência); zonas térmicas ACPI | `nvidia-smi --query-gpu=temperature.gpu,utilization.gpu,clocks.gr,power.draw --format=csv,noheader,nounits`; PDH `\Thermal Zone Information(*)\Temperature` |
| Gargalo | "Limitado pela GPU", "CPU no limite", "Parece limitado por um núcleo", "Memória quase cheia", "Disco saturado", "PC ocioso", "Nenhum gargalo evidente" | média dos últimos 10 s do histórico |

Histórico: 5 minutos a 1 Hz em buffer circular (`RingBuffer<T>`, sem alocação depois de criado). Os mini-gráficos são
`Sparkline` (uma `StreamGeometry` congelada por leitura, traço + área), sem elemento por ponto.

### Regras de agregação (iguais às do Gerenciador de Tarefas quando ele tem regra pública)

- **CPU**: `% Processor Utility` passa de 100% com turbo; limitamos a 100. Núcleos ordenados por grupo e número ("0,0", "0,1", …, "1,0").
- **GPU**: cada motor físico (adaptador + `eng_N`) soma todos os processos (limitado a 100); o uso de um tipo é o do motor mais
  ocupado daquele tipo; o uso da placa é o do tipo mais ocupado. Por processo: o motor em que ele mais usa. Placa principal = a
  com mais VRAM dedicada em uso (desempate: mais ocupada) — normalmente a placa dedicada num notebook híbrido.
- **Memória "em uso"** = total − disponível. Cache faz parte do disponível: não é memória perdida.

## Custo (meta < 1% de CPU)

- Uma única consulta PDH aberta com todos os contadores; uma coleta por segundo.
- Processos a cada 2 s (abre um handle por processo para ler o tempo de CPU).
- `nvidia-smi` a cada 5 s, em segundo plano, só se existir.
- Saúde dos discos: uma vez ao abrir a página (e no botão "Ler de novo") — o PowerShell leva alguns segundos.
- **A coleta só roda com a página aberta**: a página assina `PropertyChanged` do host e para quando `SelectedPage` muda; volta
  ao reabrir. Com a janela minimizada, os tiques são pulados. O usuário também pode pausar.

## Critérios de saúde de disco (`DiskHealthClassifier`)

| Nível | Critério |
|---|---|
| Crítico | Windows diz `Unhealthy`; desgaste ≥ 90%; qualquer erro de leitura/escrita **não corrigido**; temperatura ≥ 75 °C (SSD) ou ≥ 60 °C (HDD) |
| Atenção | Windows diz `Warning`; estado operacional diferente de OK; desgaste ≥ 70%; temperatura ≥ 65 °C (SSD) ou ≥ 50 °C (HDD); HDD com ≥ 40.000 h ligado |
| OK | nada acima; o Windows diz `Healthy` ou nenhum contador está fora do normal |
| Sem dados | o Windows não informou estado nem contadores |

Erros de leitura **corrigidos** (ECC) são mostrados mas não rebaixam: correção é o funcionamento normal do disco.
"Desgaste" (`Wear`) é a % da vida útil nominal já gasta, como o fabricante informa ao Windows.

**Administrador**: `Get-StorageReliabilityCounter` costuma exigir elevação. Sem ela, a página explica que temperatura, desgaste
e erros não estão disponíveis e classifica só pelo estado de saúde do Windows. USB e controladoras RAID muitas vezes não
repassam esses contadores mesmo como administrador.

## Detector de gargalo (`BottleneckDetector`)

Média dos últimos 10 s (mínimo 5 leituras; picos de 1 s não contam). Ordem:

1. Memória: comprometida ≥ 90% do limite ou RAM em uso ≥ 92% → "Memória quase cheia".
2. GPU ≥ 95% e CPU < 85% → "Limitado pela GPU" (normal e esperado em jogo pesado sem limite de FPS).
3. CPU ≥ 90% → "CPU no limite".
4. Núcleo mais ocupado ≥ 95%, CPU < 60% e GPU < 80% (ou sem leitura) → "Parece limitado por um núcleo" (é pista, não prova:
   o Windows troca threads de núcleo).
5. Disco mais ocupado ≥ 90% ativo com fila ≥ 2 → "Disco saturado".
6. CPU e GPU < 15% → "PC ocioso"; senão "Nenhum gargalo evidente" (lembrando que limite de FPS/V-Sync segura os quadros).

O detector só vê contadores do Windows, não o jogo por dentro; a página manda confirmar com uma medição de frametime (Medir).

## Temperaturas: o que é honesto dizer

- **CPU**: o Windows não tem API pública e confiável para a temperatura do núcleo. Ferramentas que mostram isso carregam um
  driver de kernel (ex.: WinRing0) para ler registradores MSR — o NEXUS **não** faz isso (risco de segurança: o WinRing0 é
  bloqueado pelo Defender como driver vulnerável).
- **Zonas ACPI** (`\Thermal Zone Information`, o mesmo dado de `MSAcpi_ThermalZoneTemperature`): sensores definidos pelo
  fabricante da placa-mãe; frequentemente ficam parados num valor fixo ou medem outra coisa. Mostramos com esse aviso e
  descartamos valores impossíveis (≤ 1 °C ou ≥ 130 °C).
- **GPU**: só NVIDIA, via `nvidia-smi` do próprio driver. Caminhos fixos (`%SystemRoot%\System32\nvidia-smi.exe` e
  `%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe`), nunca pelo PATH. AMD/Intel: sem leitura pública.
- **Discos**: temperatura vem dos contadores de confiabilidade (acima).

## Riscos

Nenhuma escrita. Processos externos executados (somente leitura, sem janela, com tempo-limite e caminho fixo):
`powershell.exe` do sistema (`-NoProfile -NonInteractive -EncodedCommand`, script em `DiskHealthScript.Text`, testado para
não conter verbos de escrita) e `nvidia-smi.exe` (só `--query-gpu`). Nada de rede, telemetria ou download.

## Como desfaz

Não há o que desfazer: nada é alterado.

## O que NÃO faz (e por quê)

- Não lê temperatura de CPU nem de GPU AMD/Intel (exigiria driver de kernel de terceiros).
- Não grava logs de desempenho em disco (o histórico é de 5 min, em memória). Para análise de jogo, use Medir (PresentMon).
- Não "otimiza" nada a partir do monitor: o gargalo explica, não aplica. Ajustes passam pelas páginas de otimização, com diário e desfazer.
- Não encerra processos (o Gerenciador de Tarefas faz isso e mostra o que está fechando).

## Testes

`tests/Nexus.Tests/Modules/MonitoringTests.cs` (Unit): agregação por núcleo (turbo, grupos, sem total), GPU por motor/tipo/processo
e escolha da placa, memória/disco/rede/térmica, CPU por processo (PID reaproveitado), buffer circular, detector de gargalo
(todas as regras), parse do nvidia-smi (N/A, várias placas, lixo), classificação e parse da saúde de disco (valores numéricos do
CIM, sem contadores), sessão com fontes falsas e garantia de módulo sem handlers/catálogo.

## Como validar no Windows

1. Abrir a página Monitor: o ponto verde e "Ao vivo" aparecem; os números batem com o Gerenciador de Tarefas (aba Desempenho).
2. Rodar um jogo/benchmark de GPU: GPU 3D perto de 100% e o cartão de gargalo vira "Limitado pela GPU" em ~10 s.
3. Trocar de página e voltar: o status passa a "Parado…" e retoma; com a janela minimizada o uso de CPU do NEXUS cai a ~0.
4. NEXUS normal vs. como administrador: a saúde dos discos ganha temperatura/desgaste/horas como administrador.
5. Com placa NVIDIA: a linha "GPU NVIDIA 0: … (nvidia-smi)" aparece em Temperaturas.
6. Custo: no Gerenciador de Tarefas, o processo Nexus.Desktop com a página aberta deve ficar abaixo de 1% de CPU.
