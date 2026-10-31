# Laboratório do processador (`Nexus.Lab.Cpu`)

Página **Processador** (grupo "Laboratório"). "Engenharia reversa" do processador deste PC: o que ele **é** (lido) e como
ele **se comporta** (medido), e o que o está segurando. Sem driver de kernel: nada de WinRing0, MSR, `\\.\PhysicalMemory`
ou serviços instalados (o Defender bloqueia esses drivers e eles são um risco real de segurança). Só a instrução CPUID, as
APIs de energia e topologia do Windows e o cronômetro de alta resolução (`Stopwatch`).

- Leitura e medição: direto da biblioteca, na página, em `Task.Run`/threads dedicadas. **Medir não muda nada no PC.**
- Escrita: só duas correções do plano de energia, pelo aplicador, como ações do catálogo `catalog/cpulab.v1.json`
  (`Host.ApplyAsync` / `Host.RevertAsync`), com confirmação, diário, releitura, desfazer, UAC e ponto de restauração.
- Resultados: `%LocalAppData%\NEXUS\lab\cpu\cpu-<data>-<rapida|sustentada>.json` (só dados do NEXUS, escrita atômica
  `.tmp` → `Move`), para comparar antes/depois. Arquivo corrompido é ignorado.

## 1. Identidade

| O quê | Fonte | Arquivo |
|---|---|---|
| Fabricante, família/modelo/stepping (com família/modelo estendidos), assinatura | CPUID folhas 0 e 1 (`X86Base.CpuId`) | `CpuId.cs` |
| Nome da marca | CPUID 0x80000002–4 | `CpuId.cs` |
| Microarquitetura + codinome | Tabela própria (Intel família 6 de Skylake a Panther Lake; AMD 17h/19h/1Ah de Zen 1 a Zen 5). Fontes: Intel SDM vol. 4 (DisplayFamily_DisplayModel), PPR/Revision Guides da AMD, `arch/x86/include/asm/intel-family.h`. Modelo desconhecido = "fora da tabela", nunca chute | `Microarchitecture.cs` |
| Recursos | SSE4.2, AVX, AVX2, FMA3, AVX-512F, AVX-VNNI (7.1 EAX4), AVX-512 VNNI, SHA, AES-NI, BMI2, POPCNT, VT-x/AMD-V, SMT, híbrido (7.0 EDX15), hipervisor (1 ECX31) | `CpuId.cs` |
| Caches | Intel folha 4, AMD 0x8000001D: tipo, nível, vias, linha, partições, conjuntos, quantos processadores lógicos compartilham | `CpuId.cs` |
| Topologia | `GetLogicalProcessorInformationEx(RelationAll)`: núcleos com suas threads, `EfficiencyClass` (P/E), caches com máscaras (blocos de L3 = CCX), grupos, pacotes, NUMA | `Topology.cs` |
| Frequência anunciada | registro `HARDWARE\DESCRIPTION\System\CentralProcessor\0\~MHz`, `CallNtPowerInformation(ProcessorInformation)` (MaxMhz, MhzLimit), CPUID 0x16 (só Intel; a AMD não publica o turbo máximo) | `PowerState.cs` |

Em ARM (sem CPUID) a identidade é "indisponível" com o motivo; topologia, energia e medições continuam.
Em Intel híbrido, a folha 4 descreve as caches do núcleo em que a thread estava (P ou E); a topologia do Windows mostra as
duas.

Exemplo esperado no PC do responsável: "AMD Ryzen 5 5600H with Radeon Graphics · Zen 3 Cezanne · 6 núcleos / 12 threads ·
16 MB L3" (assinatura 0x00A50F00, família 19h, modelo 50h).

## 2. Medições (cada uma com o método na própria página, em "Como cada medição funciona")

| Medição | Método | Saída |
|---|---|---|
| Frequência por núcleo | Cadeia de 32 operações inteiras dependentes por iteração (`add`/`xor` entre registradores, latência de 1 ciclo em todo x86 desde ~2006). Operações/s = ciclos/s. Thread dedicada, prioridade alta, fixada no núcleo (`SetThreadGroupAffinity`), aquecimento de 150 ms e melhor de 5 amostras de 40 ms. Código gerado conferido (`DOTNET_JitDisasm`): só `add rax,rsi; xor rax,rdx` em sequência | GHz por núcleo, núcleo preferido, dispersão entre núcleos P |
| Latência de cache/RAM | Perseguição de ponteiros: uma linha de 64 B por posição, ordem aleatória (ciclo único de Sattolo), memória nativa alinhada (fora do GC), 4 KB → 512 MB (√2 por passo até 64 MB, 2× depois; limitado a 1/8 da RAM). Aquece passando pelo conjunto inteiro, calibra o número de saltos para ~120 ms, melhor de 2 | Curva ns e ciclos (ciclos = ns × GHz do núcleo medido) |
| Degraus | Sem CPUID: ajuste de degraus constantes ao log da latência (programação dinâmica, até 7 degraus, para quando o erro típico < 6%), junta vizinhos com < 20% de diferença, patamar = 3+ pontos (o último pode ter menos). Último acima de 40 ns = "RAM". Depois compara com o CPUID: concorda se o tamanho declarado estiver entre (último ponto do patamar ÷ 1,5) e (primeiro ponto fora × 1,5) | "O CPUID diz L3 16 MB; a medição mostra o degrau em ~16 MB–22,6 MB. Confere." |
| Núcleo a núcleo | Duas threads fixadas passam um contador por uma única linha de cache com `Interlocked.CompareExchange` (vetor de 48 longs com a bola no meio, sem falso compartilhamento). Metade da ida e volta, melhor de 3; 20 000 passes. Primeira thread de cada núcleo físico; acima de 16 núcleos, amostra espalhada | Matriz ns (mapa de calor) |
| Clusters | Maior salto entre latências ordenadas (≥ 1,4×) separa perto/longe; união dos pares "perto" = grupos | "2 grupos: dentro ~20 ns, entre grupos ~80 ns" |
| Sustentado (30/45/60 s) | Um núcleo sozinho (referência), pausa curta, depois a cadeia em todos os processadores lógicos em fatias de 100 ms; média por segundo | GHz × tempo; queda (início = maior média móvel no 1º quarto; sustentado = mediana do último terço; ≥ 7% = queda; ≥ 15% = grave; instável se o desvio no fim > 8%); escala multi-thread |

Bateria rápida ("Medir", ≈ 20 s num 6–8 núcleos): frequência por núcleo → latência no núcleo preferido → núcleo a núcleo
→ 4 s com todos carregados. Tudo cancelável (Cancelar), sem travar a interface (progresso por `IProgress`), com aviso de
bateria (confirmação se o carregador estiver desligado) e confirmação antes do teste sustentado.

Limites honestos, também ditos na página: acima de alguns MB a latência inclui faltas de TLB (páginas de 4 KB), como em
qualquer programa real; na carga sustentada cada thread SMT também roda a 1 operação/ciclo, então a escala é a de uma carga
ideal (jogos escalam menos); medições são ruidosas com programas pesados abertos.

## 3. Diagnóstico (`Diagnosis.cs`, puro e testado com fatos falsos)

| Achado | Evidência | Fonte | Ação |
|---|---|---|---|
| Estado máximo < 100% na tomada | `PowerReadACValueIndex` PROCTHROTTLEMAX + GHz medido | learn.microsoft.com …/options-for-perf-state-engine-maxperformance | `cpulab.power.max-state.ac` → 100 (Admin) |
| Turbo desligado na tomada | PERFBOOSTMODE = 0 + GHz medido | learn.microsoft.com …/options-for-perf-state-engine-perfboostmode | `cpulab.power.boost-mode.ac` → 2 "Agressivo" (Admin) |
| Estado máximo / turbo limitados na bateria | valores DC | idem | Instrução (só informativo) |
| Turbo não aparece sem causa no plano | núcleo mais rápido ≤ base × 1,03 | idem | Instrução (carregador, perfil do fabricante) |
| Na bateria / economia de energia | `GetSystemPowerStatus` (ACLineStatus, SystemStatusFlag) | learn.microsoft.com …/ns-winbase-system_power_status | Instrução |
| Modo de energia "melhor eficiência" | `PowerGetEffectiveOverlayScheme` | support.microsoft.com/topic/c2aff038-22c9-f46d-5ca0-78696fdf2de8 | Instrução |
| Limite de frequência agora | `CallNtPowerInformation` MhzLimit < 95% de MaxMhz | learn.microsoft.com …/processor-power-information-str | Instrução |
| Estacionamento de núcleos | CPMINCORES AC < 100 | — (efeito depende do escalonador) | `power.processor.min-cores-parking` (já existente, Experimental: "medir antes e depois") |
| Queda sob carga sustentada | série medida | medição; limites PPT/PL1/PL2 | Instrução: superfície dura/base, limpeza das saídas de ar, perfil do fabricante; **sem promessa** |
| Integridade de memória (HVCI) | `NtQuerySystemInformation(SystemCodeIntegrityInformation)` bit 0x400 (estado efetivo) ou registro DeviceGuard | Microsoft Support, "Options to optimize gaming performance in Windows 11" (out/2022: "there may be a performance impact with Memory Integrity and VMP on") | **Só instrução**, com o custo de segurança explicado. O NEXUS não desliga proteção do Windows |
| Hipervisor/VBS ativo sem HVCI | CPUID 1 ECX31 / registro | idem | Informativo |
| Modo de Jogo desligado | `AutoGameModeEnabled` (leitura da descoberta) | idem | `win.gamemode.enable` (já existente, Usuário) |
| Núcleo preferido / dispersão ≥ 3% | medição | CPPC2 (AMD) / Turbo Boost Max 3.0 (Intel) | Informativo: é normal |
| Nada segurando | — | — | Resumo verde |

## Ações do catálogo

| Id | Handler | Privilégio | O que faz | Desfazer |
|---|---|---|---|---|
| `cpulab.power.max-state.ac` | `PowerSettingValue` (existente) | Admin | PROCTHROTTLEMAX (AC) do esquema ativo → 100 | Valor anterior no mesmo esquema |
| `cpulab.power.boost-mode.ac` | `PowerSettingValue` (existente) | Admin | PERFBOOSTMODE (AC) → 1 ou 2 (`allowedValues`; a página pede 2) | Valor anterior no mesmo esquema |

Ambas `Conditional`: a página só oferece o botão quando a leitura mostra o limite. O esquema é capturado no planejamento
(`activeScheme`), então o desfazer volta ao mesmo esquema mesmo que o usuário troque de plano. Nenhum handler novo:
`CpuLabHandlers.Create` continua vazio.

## Riscos

- Correções: mais calor e consumo sob carga (é o comportamento padrão do Windows). Alguém pode ter limitado de propósito
  para esfriar o notebook: a confirmação mostra o valor lido e o desfazer volta a ele.
- Medições: carga máxima por segundos (bateria rápida) ou até 60 s (sustentado); a ventoinha acelera. Nada é escrito no
  sistema; a memória do teste de latência (até 512 MB, nunca mais que 1/8 da RAM) é liberada no fim de cada tamanho.

## O que NÃO faz (e por quê)

- Não lê temperatura, tensão, potência do pacote, PPT/PL1/PL2 ou MSR: exige driver de kernel (WinRing0 e afins), bloqueado
  pelo Defender e inseguro. A queda térmica é **inferida** pela frequência medida ao longo do tempo.
- Não faz undervolt, overclock, Curve Optimizer nem muda limites de potência (firmware/driver do fabricante).
- Não desliga a integridade de memória/VBS: é proteção real; só explica o custo e onde mudar.
- Não promete ganho com limpeza/base: depende do projeto de refrigeração; sugere medir antes e depois.
- Não mede largura de banda de memória (é do Laboratório de memória).

## Testes (`tests/Nexus.Tests/Modules/CpuLabTests.cs`, todos `Category=Unit`, rodam no Linux)

CPUID com registros de amostra (5600H, 12900K, 7800X3D, 13700H, 9700X), hipervisor, CPUID ausente; tabela de
microarquiteturas (22 casos + modelos desconhecidos); topologia GLPI sintética (6c/12t, híbrido com 2 domínios de L3,
buffer truncado); degraus numa curva sintética (com e sem ruído) e comparação com o CPUID; clusters numa matriz sintética
(2 CCX, uniforme, P/E); queda numa série sintética (queda, plana, oscilante); diagnóstico com fatos falsos; catálogo
(carrega, ações referenciadas existem, aplicar/desfazer pelo executor com backend de energia falso, valor obrigatório do
turbo); medições reais reduzidas sem P/Invoke do Windows (`NoPinning`, `CpuLabOptions.Tiny`, coleção sem paralelismo);
armazenamento e comparação.

## Como validar no Windows

1. `dotnet build Nexus.slnx -c Release` e abrir o NEXUS → Laboratório → Processador.
2. Conferir o hero (5600H: "Zen 3 Cezanne · 6 núcleos / 12 threads · 16 MB L3"), a tabela de caches (L1d 32 KB 8 vias,
   L2 512 KB, L3 16 MB compartilhada por 12 threads) e os recursos (AVX2 sim, AVX-512 não, AMD-V sim, SMT sim).
3. Na tomada, clicar **Medir**: núcleo mais rápido deve ficar perto de 4,2 GHz (turbo do 5600H), acima da base 3,3 GHz;
   curva com degraus em ~32 KB, ~512 KB e ~16 MB; matriz 6×6 num grupo só (Cezanne tem um CCX de 6 núcleos).
4. **Teste sustentado** 60 s: ver se a frequência cai e quanto. Repetir depois de mudar o perfil do NitroSense para comparar.
5. Correção (só se aparecer): por exemplo, em Opções de energia avançadas pôr "Estado máximo do processador" em 99% na
   tomada, voltar à página, "Ler de novo": deve aparecer "Processador limitado a 99%" com o botão; aplicar (UAC), medir,
   e "Desfazer a última correção" deve voltar a 99%. Fazer isso numa VM ou aceitando a mudança consciente no PC.
6. Os arquivos ficam em `%LocalAppData%\NEXUS\lab\cpu\`.
