# Guia: suas próprias regras de limpeza

Além das regras que vêm com o NEXUS, você pode escrever as suas, como no Kudu ("cleaner rules"): arquivos `.json` na pasta

```
%AppData%\NEXUS\rules\
```

(no Explorador: `C:\Users\<você>\AppData\Roaming\NEXUS\rules`). Na página **Limpeza › Regras**, o botão
**Abrir pasta de regras** cria a pasta e, na primeira vez, copia um exemplo comentado e **desativado**
(`exemplo-desativado.json`). Depois de editar, clique em **Recarregar**: cada arquivo aparece com as regras que valem ou com o
erro que impede de usá-lo. As regras válidas aparecem na aba **Limpar**, no grupo **Suas regras**, com tamanho medido.

## Garantias (o que o NEXUS faz com a sua regra)

- **Nada é apagado:** tudo vai para a quarentena do NEXUS e pode voltar (Desfazer / Restaurar). Só "Esvaziar quarentena"
  apaga de vez, e isso pede duas confirmações.
- **Só o seu perfil.** Regras do usuário só podem usar pastas do perfil e **nunca** valem no aplicador de administrador nem no
  serviço. Assim, uma regra escrita por você (ou por um programa que escreveu na sua pasta) nunca vira um jeito de mover
  arquivos com privilégio.
- **Mesmas proteções das regras embutidas:** links simbólicos e junções nunca são seguidos, arquivos em uso são pulados, data no
  futuro nunca conta como "velha", e as pastas proibidas (abaixo) são recusadas na carga.
- **Relida a cada limpeza:** o aplicador relê e revalida a pasta a cada pedido. O manifesto do lote guarda uma cópia da regra,
  então o desfazer funciona mesmo que você apague ou mude o arquivo depois.
- Arquivo com erro não vale **inteiro** (nem as regras corretas dele) até ser corrigido.

## Formato

O mesmo das regras embutidas (`src/Nexus.Cleaning/Rules/cleaning-rules.json`). Comentários `//` e vírgula no fim são aceitos.

```jsonc
{
  "enabled": true,              // opcional; false desativa o arquivo inteiro
  "rules": [
    {
      "id": "meu-app-cache",    // minúsculas, números e hífens; até 48; único (não pode repetir uma regra do NEXUS)
      "title": "Meu App: cache",
      "category": "Apps",       // Sistema | Navegadores | Apps | Jogos | Desenvolvedor (só organiza a lista)
      "scope": "User",          // obrigatório: User
      "mode": "Quarantine",     // obrigatório: Quarantine
      "roots": [ "%LocalAppData%\\MeuApp\\Cache" ],
      "patterns": [ "*" ],      // nomes de arquivo (curingas * e ?)
      "recursive": true,        // entra nas subpastas
      "minAgeHours": 24,        // só arquivos sem modificação há mais de 24 h (0 = qualquer idade)
      "exclude": [ "*.config" ],          // arquivos que ficam
      "excludeDirs": [ "Importante" ],    // subpastas que ficam
      "closeApps": [ "MeuApp" ],          // nome do processo, sem .exe: com ele aberto, a regra é bloqueada
      "defaultSelected": false,           // vem marcada na lista?
      "sensitive": false,                 // true = dados pessoais: exige "warning" e nunca vem marcada
      "warning": null,
      "explanation": "Arquivos temporários que o Meu App recria sozinho.",
      "expectedGain": "Espaço em disco.",
      "evidence": "Documentação do Meu App: a pasta Cache pode ser apagada com o programa fechado."
    }
  ]
}
```

Lembre: no JSON a barra invertida é escrita duas vezes (`\\`).

### Raízes permitidas

Toda raiz começa com um destes e **sempre** tem uma pasta com nome (sem curinga) logo depois:

| Placeholder | Pasta |
|---|---|
| `%LocalAppData%` | `C:\Users\<você>\AppData\Local` |
| `%AppData%` | `C:\Users\<você>\AppData\Roaming` |
| `%LocalAppDataLow%` | `C:\Users\<você>\AppData\LocalLow` |
| `%UserTemp%` | `C:\Users\<você>\AppData\Local\Temp` |
| `%UserProfile%` | `C:\Users\<você>` (ex.: `%UserProfile%\.gradle\caches`) |

Curingas nas pastas seguintes são aceitos (até dois por raiz), ex.: `%LocalAppData%\MeuApp\Perfis\*\Cache`.

### Recusado (a página mostra o motivo)

- Placeholders de máquina: `%WinDir%`, `%ProgramData%`, `%SteamPath%`, `%SystemDrive%`, caminhos absolutos (`C:\...`), `..`.
- `"scope": "Machine"`, `"admin": true`, `"mode": "MeasureOnly"`, `"special"`, `"nativeTool"`.
- Raiz igual à pasta-base (`%LocalAppData%` sozinho) ou com curinga logo depois (`%LocalAppData%\*\Cache`).
- Pastas protegidas, inclusive por curinga ou por uma pasta acima delas com `recursive`:
  `%AppData%\Microsoft\{Protect, Credentials, Crypto, SystemCertificates}`, `%AppData%\Microsoft\Windows\{Start Menu, Recent}`,
  `%LocalAppData%\Microsoft\{Credentials, Vault}`, `%AppData%\NEXUS`, `%LocalAppData%\NEXUS`, `%LocalAppData%\Programs`,
  `%UserProfile%\{AppData, .ssh, .gnupg, Documents, Desktop, Pictures, Videos, Music, Downloads, OneDrive*, Favorites,
  Contacts, Saved Games, Links, Searches}`.
- Na hora de limpar, de novo: a pasta do perfil inteira, Documentos/Área de Trabalho/Imagens/Vídeos/Músicas/Downloads/OneDrive,
  Arquivos de Programas, a quarentena do NEXUS e qualquer pasta (ex.: `%LocalAppData%`) redirecionada para fora do perfil.
- Arquivo maior que 256 KB, mais de 40 regras por arquivo ou no total, mais de 32 arquivos, id repetido entre arquivos.

## Exemplos

**Cache de um launcher (Electron/Chromium):**

```json
{ "rules": [ {
  "id": "meu-launcher-cache", "title": "Meu Launcher: cache", "category": "Jogos", "scope": "User", "mode": "Quarantine",
  "roots": [ "%AppData%\\MeuLauncher\\Cache", "%AppData%\\MeuLauncher\\Code Cache", "%AppData%\\MeuLauncher\\GPUCache" ],
  "patterns": [ "*" ], "recursive": true, "minAgeHours": 0, "closeApps": [ "MeuLauncher" ], "defaultSelected": true,
  "explanation": "Cache do navegador embutido do launcher. Ele recria ao abrir.", "expectedGain": "Espaço em disco.",
  "evidence": "Electron: Cache, Code Cache e GPUCache são caches do Chromium."
} ] }
```

**Logs antigos de um jogo (só `.log`, mais de 7 dias, sem entrar em subpastas):**

```json
{ "rules": [ {
  "id": "meu-jogo-logs", "title": "Meu Jogo: registros antigos", "category": "Jogos", "scope": "User", "mode": "Quarantine",
  "roots": [ "%LocalAppData%\\MeuJogo\\Saved\\Logs" ], "patterns": [ "*.log" ], "recursive": false, "minAgeHours": 168,
  "closeApps": [ "MeuJogo-Win64-Shipping" ],
  "explanation": "Registros de sessões anteriores.", "expectedGain": "Pouco espaço.", "evidence": "Pasta de logs do jogo."
} ] }
```

**Cache de ferramenta de desenvolvimento em `%UserProfile%`:**

```json
{ "rules": [ {
  "id": "cargo-registry-cache", "title": "Rust: cache do registro (desenvolvedor)", "category": "Desenvolvedor",
  "scope": "User", "mode": "Quarantine",
  "roots": [ "%UserProfile%\\.cargo\\registry\\cache" ], "patterns": [ "*.crate" ], "recursive": true, "minAgeHours": 168,
  "closeApps": [ "cargo", "rustc" ],
  "explanation": "Pacotes .crate baixados; o cargo baixa de novo se precisar.", "expectedGain": "Centenas de MB.",
  "evidence": "The Cargo Book: Cargo Home > registry/cache."
} ] }
```

**Arquivo desativado** (fica validado, mas não vale): `{ "enabled": false, "rules": [ ... ] }`.

## Erros comuns

| Mensagem | O que fazer |
|---|---|
| JSON inválido (linha N) | Vírgula sobrando ou faltando, aspas, chaves. Barras invertidas duplas. |
| precisa começar com um placeholder permitido | Use um dos cinco placeholders de perfil. |
| precisa nomear uma pasta logo depois | `%AppData%\NomeDoApp\...`, sem `*` logo depois do placeholder. |
| toca uma pasta protegida | A pasta (ou uma pasta acima dela com `recursive`) está na lista proibida. |
| já existe uma regra embutida com esse id | Troque o `id`. |
| id já usado em outro arquivo | Ids são únicos entre todos os seus arquivos. |
| não está carregada (ao limpar) | O arquivo foi apagado, desativado ou ficou com erro depois da medição: Recarregar. |

## Como a limpeza chega ao aplicador

A página manda a ação genérica `cleaning.user-rule` com dois parâmetros validados por regex: `rule` (o id) e `batch` (o lote).
O aplicador da sua conta (handler `CleaningUserRule`) acha a pasta `%AppData%\NEXUS\rules` pelo **SID do próprio processo e
pela pasta do perfil em `ProfileList`** (HKLM), não pelas variáveis de ambiente; relê e revalida os arquivos; só aceita ids
de regras carregadas e válidas; e recusa tudo se estiver elevado ou rodando como serviço/outra conta.
