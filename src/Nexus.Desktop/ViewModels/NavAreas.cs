using Nexus.Desktop.ViewModels.Simple;

namespace Nexus.Desktop.ViewModels;

/// <summary>Um item da lista lateral: uma das 5 áreas (Início, Otimizar, Desempenho, Diagnóstico, Configurações).</summary>
public sealed class NavAreaItem(int index, NavArea area, PageViewModel entry)
{
    public int Index { get; } = index;
    public NavArea Area { get; } = area;
    public PageViewModel Entry { get; } = entry;
    public string Title => Area.Key switch
    {
        "home" => Loc.T("Nav.Area.Home"),
        "optimize" => Loc.T("Nav.Area.Optimize"),
        "performance" => Loc.T("Nav.Area.Performance"),
        "diagnostics" => Loc.T("Nav.Area.Diagnostics"),
        _ => Loc.T("Nav.Area.Settings"),
    };
    public string IconKey => Area.IconKey;
    /// <summary>Dica: as abas da área ("Otimizar · Limpar").</summary>
    public string Subtitle { get; init; } = "";
}

/// <summary>Uma aba no topo do conteúdo (as páginas do dia a dia da área). Clicar abre a página.</summary>
public sealed class AreaTabItem(PageViewModel page, bool isSelected, Action<PageViewModel> open)
{
    public PageViewModel Page { get; } = page;
    public string Title => Page.Title;
    public string Subtitle => Page.Subtitle;
    public bool IsSelected { get; } = isSelected;
    public RelayCommand OpenCommand { get; } = new(() => open(page));
}

/// <summary>Uma ferramenta recolhida em "Avançado" (ícone, nome, uma linha). Clicar abre a página.</summary>
public sealed class AreaLinkItem(PageViewModel page, bool isSelected, Action<PageViewModel> open)
{
    public PageViewModel Page { get; } = page;
    public string Title => Page.Title;
    public string Subtitle => Page.Subtitle;
    public bool IsSelected { get; } = isSelected;
    public RelayCommand OpenCommand { get; } = new(() => open(page));
}
