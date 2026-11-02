# Termos de uso do NEXUS

Versão de 2 de novembro de 2026, para o NEXUS 1.0.

Leia com calma. São poucas regras, escritas em português simples. Ao instalar ou usar o NEXUS, você concorda com elas.
Se não concordar, não instale (ou desinstale) o NEXUS.

## 1. O que é o NEXUS

O NEXUS é um programa para Windows 10 e 11 que lê informações do seu computador, explica o que encontrou e, **se você
pedir e confirmar**, faz ajustes de desempenho, limpeza e manutenção. Cada ajuste mostra o que faz, de onde vem a
informação (documentação da Microsoft ou do fabricante), o risco e como desfazer.

## 2. Você no controle

- O NEXUS **não muda nada sozinho**. Toda mudança precisa da sua confirmação. Mudanças de sistema também pedem a
  permissão do Windows (o aviso "Deseja permitir que este aplicativo faça alterações?") e criam um ponto de restauração.
- As únicas coisas que rodam sem você estar olhando são as que **você mesmo** ligou: a verificação agendada (só lê e gera um
  relatório) e a limpeza segura agendada (só caches e temporários de baixo risco, sempre para a quarentena). Você desliga as
  duas quando quiser, em Ajustes.
- A atualização automática do NEXUS vem ligada e pode ser desligada em Ajustes → Atualizações (ver a Política de
  privacidade).

## 3. Desfazer e quarentena

- O NEXUS grava cada mudança num histórico **antes** de fazê-la, confere o resultado e sabe desfazer. Arquivos removidos
  numa limpeza vão para uma quarentena e podem voltar ao lugar.
- Algumas poucas ações **não têm volta** (por exemplo, a exclusão segura de arquivos e esvaziar a quarentena ou a Lixeira).
  Nesses casos o NEXUS avisa claramente e pede uma confirmação extra.
- Apagar os dados do NEXUS (ou responder "Sim" quando o desinstalador perguntar) apaga também o histórico; sem ele, o NEXUS
  não consegue mais desfazer o que alterou.

## 4. Uso por sua conta e risco

O NEXUS foi feito para não quebrar nada e cada mudança é testada, registrada e reversível. Mesmo assim, cada computador
é diferente (hardware, drivers, outros programas, políticas da empresa ou da escola), e **nenhum programa consegue
garantir que nada vai dar errado**.

Por isso:

- O NEXUS é fornecido **"no estado em que se encontra"**, sem garantia de que vai funcionar no seu computador, de que vai
  melhorar o desempenho ou de que estará livre de defeitos.
- Na medida permitida pela lei, o autor do NEXUS **não se responsabiliza** por perdas de dados, falhas, tempo parado ou
  outros prejuízos decorrentes do uso. Isso não afasta direitos que a lei garanta a você e que não possam ser renunciados
  (por exemplo, os do Código de Defesa do Consumidor, quando se aplicarem).
- Mantenha cópia de segurança dos seus arquivos importantes. É uma boa prática com qualquer programa que mexe no sistema.
- Ganhos de desempenho variam. O NEXUS mostra números medidos no seu PC quando possível e diz quando um ajuste não ajuda;
  ele não promete FPS.
- Não use o NEXUS em computadores que não são seus ou que você não tem autorização para alterar (por exemplo, o computador
  do trabalho sem permissão da empresa).

## 5. Drivers e programas de terceiros

- Drivers vêm **só de fontes oficiais**: o Windows Update (Microsoft) e os servidores da NVIDIA e da AMD. O NEXUS confere a
  assinatura digital antes de instalar e guarda uma cópia do driver anterior para desfazer.
- Programas instalados ou atualizados por gerenciadores de pacote (winget, Chocolatey, Scoop, npm), ferramentas da Microsoft
  (Defender, Windows Update, Microsoft Store) e desinstaladores de outros programas seguem os termos dos respectivos donos.
  O NEXUS só abre essas ferramentas quando você pede.

## 6. Marcas e afiliação

O NEXUS é um projeto independente. **Não é afiliado, patrocinado nem aprovado** pela Microsoft, NVIDIA, AMD, Intel ou
qualquer outro fabricante citado. Windows, DirectX e Microsoft Defender são marcas da Microsoft Corporation; NVIDIA, GeForce
e G-SYNC são marcas da NVIDIA Corporation; AMD, Radeon e Ryzen são marcas da Advanced Micro Devices, Inc.; Intel é marca da
Intel Corporation. Os nomes aparecem só para identificar os produtos com que o NEXUS trabalha.

## 7. Componentes de terceiros

O NEXUS inclui componentes de terceiros, cada um com a sua licença, entre eles o **PresentMon** da Intel (licença MIT),
usado para medir FPS, e o runtime do .NET da Microsoft (licença MIT). A lista completa, com os textos das licenças, está no
arquivo `THIRD-PARTY-NOTICES.txt`, na pasta de instalação.

## 8. Privacidade

O NEXUS não tem conta, não coleta dados e não envia telemetria. Os detalhes (o que fica guardado no seu PC e quando o
NEXUS acessa a internet) estão na **Política de privacidade** (`PRIVACIDADE`), que faz parte destes termos.

## 9. Mudanças nestes termos

Estes termos podem mudar em versões futuras do NEXUS. A versão que vale é a que acompanha a versão instalada (pasta de
instalação e Ajustes → Sobre). Mudanças importantes aparecem nas novidades da versão.

## 10. Lei aplicável

Estes termos seguem as leis da República Federativa do Brasil.
