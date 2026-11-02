# Política de privacidade do NEXUS

Versão de 2 de novembro de 2026, para o NEXUS 1.0.

Resumo: **o NEXUS não tem conta, não coleta dados pessoais e não envia telemetria.** O que ele lê do seu computador fica no
seu computador. Ele só usa a internet nos casos listados abaixo.

## 1. O que o NEXUS lê

Para explicar e otimizar o seu PC, o NEXUS lê informações locais: modelo do processador, placa de vídeo, memória e disco,
versão do Windows e dos drivers, programas instalados, itens da inicialização, serviços, configurações de privacidade e de
energia, uso de espaço em disco, redes Wi-Fi salvas e o estado do Microsoft Defender, entre outras. Essas leituras **não saem
do seu computador**.

## 2. O que fica guardado no seu computador

| Onde | O quê |
|---|---|
| `%LocalAppData%\NEXUS` (sua conta) | Preferências (`settings.json`), relatórios das verificações e do Assistente, medições de FPS, histórico (diário) e quarentena das ações da sua conta, regras de limpeza que você criou, registros de erro e de diagnóstico do próprio NEXUS, downloads da atualização do NEXUS e de drivers de vídeo. |
| `%ProgramData%\NEXUS` (computador) | Histórico (diário) e quarentena das ações de administrador, cópias de segurança de drivers. Só administradores alteram. |
| Agendador de Tarefas do Windows | As tarefas que você ligar (verificação e limpeza segura agendadas). |
| Registro do Windows | `HKLM\SOFTWARE\NEXUS` (marca de quem o NEXUS colocou no grupo "Usuários do log de desempenho", para o desinstalador desfazer) e, se você ligar, o item de iniciar com o Windows. |

- A **quarentena** guarda os arquivos que uma limpeza tirou do lugar, com o caminho original, para poder devolvê-los. Ela
  pode conter arquivos seus. Você esvazia quando quiser (essa ação não tem volta).
- **Relatórios e o arquivo exportado da leitura do PC** podem conter o nome do computador, endereços IP e caminhos de
  programas. Eles só saem do seu PC se **você** os enviar a alguém.
- Desinstalar o NEXUS **não apaga** esses dados por padrão: o desinstalador pergunta (com padrão "Não"), porque sem o
  histórico o NEXUS não consegue mais desfazer o que alterou.

## 3. Quando o NEXUS usa a internet

Só nestes casos, e sempre direto com o serviço oficial (sem servidor do NEXUS no meio):

| Quando | Com quem | O que é enviado |
|---|---|---|
| Procurar atualização do NEXUS (automático a cada 6 h, desligável em Ajustes → Atualizações) | GitHub (`github.com/robinexe/nexus-releases`) | Um pedido do manifesto de versão, com a versão do NEXUS no cabeçalho `User-Agent`. Nenhum identificador seu. O instalador baixado só roda se a assinatura digital do NEXUS conferir. |
| Procurar drivers (página Atualizar drivers, Assistente) | Windows Update (Microsoft), pela própria API do Windows | O que o Windows Update normalmente recebe ao procurar atualizações; segue a política de privacidade da Microsoft. |
| Ver a versão mais nova do driver de vídeo (ao abrir a página Atualizar drivers) e baixá-lo (só quando você clica) | Sites oficiais da NVIDIA (`nvidia.com`, `geforce.com`) ou da AMD (`amd.com`) | Modelo/série da placa de vídeo, versão do Windows e idioma, como o formulário de download do site do fabricante. O arquivo baixado só é instalado se a assinatura digital do fabricante conferir. |
| Procurar atualizações de programas (página Programas, Assistente) | Os gerenciadores de pacote instalados no seu PC (winget, Chocolatey, Scoop, npm) | O que cada gerenciador envia aos repositórios dele; o NEXUS só os executa. |
| Ferramentas da Microsoft que você aciona (atualizar assinaturas do Defender, Microsoft Store) | Microsoft | O que a própria ferramenta envia. |
| Links que você clica (fontes, documentação, páginas de download) | O site do link | Abre no seu navegador, como qualquer link. |

O NEXUS **não** envia estatísticas de uso, relatórios de erro, leituras do PC nem qualquer identificador para o autor ou para
terceiros. Não há anúncios nem rastreadores.

## 4. Medição de FPS (PresentMon)

A medição usa o PresentMon oficial da Intel (licença MIT), que acompanha o NEXUS e roda **só no seu computador**, lendo
eventos do Windows sobre a apresentação de quadros. Nada da medição é enviado.

## 5. Seus direitos (LGPD)

Como o NEXUS não coleta nem recebe dados pessoais, o autor não tem dados seus para mostrar, corrigir ou apagar. Tudo o que o
NEXUS guarda está nas pastas da seção 2, sob o seu controle: você pode abrir, copiar ou apagar quando quiser (lembrando que
apagar o histórico impede o NEXUS de desfazer as mudanças).

## 6. Mudanças nesta política

Se uma versão futura passar a acessar a internet de outro jeito, esta política será atualizada **antes**, e a mudança
aparecerá nas novidades da versão. A versão que vale é a que acompanha a versão instalada.
