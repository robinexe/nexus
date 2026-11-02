# 12 — Idiomas da interface (pt-BR, English, Español)

A interface do NEXUS existe em **português do Brasil** (padrão), **inglês** e **espanhol**. O idioma é escolhido em
**Ajustes → Idioma**: "Igual ao Windows" (padrão), Português, English ou Español. Trocar pede reinício (botão
"Reiniciar agora"): os textos são carregados uma vez, ao abrir, o que mantém tudo simples e sem telas pela metade.

## Como funciona

| Peça | Onde |
|---|---|
| Textos pt-BR (neutro, usado quando falta tradução) | `src/Nexus.Desktop/Localization/Strings.resx` |
| Textos em inglês e espanhol | `Strings.en.resx`, `Strings.es.resx` (viram `en\Nexus.Desktop.resources.dll` e `es\...`) |
| Acesso no código | `Loc.T("Chave")` e `Loc.F("Chave", args)` (`string.Format`, números no formato do idioma) |
| Acesso no XAML | `xmlns:l="clr-namespace:Nexus.Desktop.Localization"` e `Text="{l:T Chave}"`; em Binding: `StringFormat={l:T Chave}` |
| Escolha do idioma | `LanguageOption.Resolve`: `--lang xx` na linha de comando > Ajustes (`settings.json`, campo `language`) > idioma do Windows > pt-BR |
| Aplicado | no construtor de `App`, antes de qualquer XAML |
| Catálogo | campo opcional `"i18n": {"en": {"title": …, "mechanism": …, "risks": …, "revert": …}, "es": {…}}` por ação; a interface usa `CatalogDocument.Localized(Loc.Language)`. Só texto muda; o aplicador lê o catálogo original |
| Publicação | `tools/packaging/publish.py` publica com `SatelliteResourceLanguages=pt-BR;en;es`; o instalador copia a pasta inteira (`recursesubdirs`) |
| Autoteste em outro idioma | `Nexus.Desktop.exe --selftest-ui ui-en.json --lang en` (docs/09-autoteste.md) |

Windows em idioma sem tradução (francês, alemão…) abre em pt-BR. pt-PT abre em pt-BR; en-GB em inglês; es-MX em espanhol.

## Testes (`tests/Nexus.Tests/LocalizationTests.cs`)

- toda chave existe nos três arquivos, sem sobras e sem texto vazio;
- os marcadores `{0}`, `{1:0.0}`… são os mesmos nos três idiomas e o texto formata sem erro;
- toda chave usada em XAML (`{l:T …}`) e código (`Loc.T/F("…")`) existe, e nenhuma chave fica sem uso;
- XAML traduzido não tem texto fixo em atributos de texto (`Text`, `Content`, `Title`, `ToolTip`, `Header`…, `StringFormat='…'`);
  páginas ainda não traduzidas ficam na lista `XamlNotYetTranslated` (o teste avisa quando uma delas já pode sair);
- o ResourceManager acha en/es (satélites) e cai no pt-BR para idioma desconhecido; ordem de escolha do idioma;
- `publish.py` publica os três idiomas; títulos do catálogo traduzidos não mudam chave, handler, alvo, valor ou privilégio.

## O que está traduzido (02/11/2026)

Toda a interface do Desktop: casca (navegação, barra de status, tela de progresso, bandeja, atualização, erros) e as 30
páginas — XAML e textos montados nos ViewModels, inclusive confirmações e mensagens. `XamlNotYetTranslated` está vazia.
Nomes e explicações das 13 etapas do Assistente. Títulos de todas as ações de todos os catálogos (`catalog/*.v1.json`).
Números e datas da tela usam a cultura do idioma escolhido (`Loc.Culture`: 3.4 GB em inglês, 3,4 GB em pt-BR/es).

## O que continua em pt-BR

- Textos que vêm das bibliotecas dos módulos (`Nexus.*`): achados do diagnóstico, recomendações, fatos e propostas do
  Assistente, nomes e avisos de regras de limpeza, itens de privacidade (a página Privacidade mostra os títulos da
  biblioteca), veredictos de serviços/segurança/programas, motivos de recusa do aplicador, relatório HTML do Assistente,
  linha de comando `nexus`. A interface mostra esses textos como recebe.
- Mecanismo, riscos e desfazer das ações do catálogo (só os títulos estão traduzidos).
- Formatos de data escritos à mão (`dd/MM/yyyy`) continuam nessa ordem em todos os idiomas; só os separadores e números
  seguem o idioma. `AssistantPage` lê em pt-BR os números que as bibliotecas escrevem (de propósito).
- Aplicador, diário, catálogo e exportações (CSV, relatório da limpeza) não dependem do idioma.

## Como acrescentar um idioma

1. Copie `Strings.en.resx` para `Strings.<código>.resx` (ex.: `Strings.fr.resx`) e traduza **todos** os `<value>`,
   mantendo os marcadores `{0}`, `{1:0.0}` e as quebras de linha.
2. Inclua o código em `LanguageOption.Supported`, `LanguageOption.Choices` (nome no próprio idioma) e em
   `LanguageOption.FromCulture`/`Normalize`.
3. Inclua o código em `SatelliteResourceLanguages` em `tools/packaging/publish.py`.
4. Opcional: `"i18n": {"<código>": {"title": …}}` nas ações do catálogo.
5. `dotnet test` — `LocalizationTests` aponta chave faltando, marcador diferente ou idioma não publicado.

Para traduzir mais uma página: troque cada texto fixo por `{l:T Pagina.Chave}` / `Loc.T`/`Loc.F`, acrescente a chave nos três
`.resx` e tire a página de `XamlNotYetTranslated`. Cuidado com textos que também são comparados (ex.: título de cartão
procurado por nome): compare com a mesma chave, nunca com o texto pt-BR.
