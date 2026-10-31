using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nexus.Desktop.ViewModels;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop;

public partial class MainWindow : Window
{
    /// <summary>Ordem dos grupos da navegação lateral (por tarefa do jogador).</summary>
    private static readonly string[] GroupOrder = ["Meu PC", "Automático", "Otimizar", "Limpar", "Proteger", "Sistema", "Medir", "Registro"];

    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();

        // Navegação agrupada por PageViewModel.Group. A lista vai antes do DataContext para o SelectedItem já achar a página.
        var ordered = _vm.Pages
            .OrderBy(p => Array.IndexOf(GroupOrder, p.Group) is var i && i >= 0 ? i : GroupOrder.Length)
            .ToList();
        var view = new ListCollectionView(ordered);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PageViewModel.Group)));
        Nav.ItemsSource = view;
        Nav.SelectionChanged += KeepSelection;

        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += async (_, _) =>
        {
            ShellMotion.SweepOnce(SweepBeam, SweepLayer.ActualWidth);
            await _vm.LoadAsync();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPage)) ShellMotion.EnterPage(PageHost);
    }

    /// <summary>Ctrl+clique desmarcaria o item ativo e deixaria a área de conteúdo vazia: volta para a página anterior.</summary>
    private void KeepSelection(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is null && e.RemovedItems.Count > 0) Nav.SelectedItem = e.RemovedItems[0];
    }
}
