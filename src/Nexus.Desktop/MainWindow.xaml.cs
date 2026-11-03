using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nexus.Desktop.Services.Shell;
using Nexus.Desktop.ViewModels;
using Nexus.Desktop.Views.Shell;

namespace Nexus.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Window não é IDisposable; o ícone da bandeja é liberado em Closed.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _toldAboutTray;

    /// <summary>Aberto por "iniciar com o Windows" (--minimized): vai direto para a bandeja quando ela está ligada.</summary>
    public bool StartInTray { get; init; }

    public MainWindow()
    {
        using (UiWatchdog.Phase("janela: InitializeComponent", logAboveMs: 30)) InitializeComponent();
        using (UiWatchdog.Phase("janela: MainViewModel (27 páginas)")) _vm = new MainViewModel();

        // Navegação: as 6 telas simples. A lista vai antes do DataContext para o SelectedItem já achar a página.
        BuildNavigation();
        Nav.SelectionChanged += KeepSelection;

        DataContext = _vm;
        _vm.PropertyChanged += OnViewModelChanged;
        // Aviso de notebook na bateria ao abrir um jogo (checagens do PC): notificação da bandeja, se o ícone estiver ligado.
        _vm.GameNotice += (_, n) => _tray?.Notify(n.Title, n.Text, null, warning: true);
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        ContentRendered += (_, _) => UiWatchdog.Step("janela desenhada pela primeira vez");
        Loaded += async (_, _) =>
        {
            UiWatchdog.Step("janela carregada");
            using (UiWatchdog.Phase("ícone da bandeja", logAboveMs: 30)) ApplyTray();
            if (StartInTray && _tray?.Visible == true) Hide();
            ShellMotion.SweepOnce(SweepBeam, SweepLayer.ActualWidth);
            await _vm.LoadAsync();
        };
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            UiSettings.Changed -= OnSettingsChanged;
            _tray?.Dispose();
            _tray = null;
        };
        UiSettings.Changed += OnSettingsChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPage)) ShellMotion.EnterPage(PageHost);
        if (e.PropertyName == nameof(MainViewModel.NavPages)) BuildNavigation();
    }

    /// <summary>Monta a lista lateral: as 6 telas simples, sem grupos (as outras ficam em Ferramentas avançadas).</summary>
    private void BuildNavigation()
    {
        var pages = _vm.NavPages;
        Nav.ItemsSource = pages.ToList();
        // SetCurrentValue (e não atribuição): atribuir apagaria a ligação SelectedItem ← SelectedPage do XAML.
        Nav.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, pages.Contains(_vm.SelectedPage) ? _vm.SelectedPage : null);
    }

    // ------------------------------------------------------------------ navegação pelas páginas (ShellCommands.Navigate)

    /// <summary>Abre a página cujo tipo do ViewModel tem o nome dado em CommandParameter (ex.: "CleaningPage").</summary>
    private void OnNavigate(object sender, ExecutedRoutedEventArgs e)
    {
        if (FindPage(e.Parameter) is { } page) _vm.SelectedPage = page;
        e.Handled = true;
    }

    private void OnCanNavigate(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = FindPage(e.Parameter) is not null;
        e.Handled = true;
    }

    private PageViewModel? FindPage(object? parameter) => parameter switch
    {
        PageViewModel p => p,
        // _vm ainda é null enquanto o InitializeComponent monta os botões (o WPF já pergunta CanExecute): nada a navegar ainda.
        string name when _vm is not null => _vm.Pages.FirstOrDefault(p => string.Equals(p.GetType().Name, name, StringComparison.Ordinal)),
        _ => null,
    };

    /// <summary>
    /// Ctrl+clique desmarcaria o item ativo e deixaria a área de conteúdo vazia: volta para a página anterior. Abrir uma página que
    /// não está na lista do modo (ex.: do modo simples para "Atualizar drivers") também tira a seleção — essa fica.
    /// </summary>
    private void KeepSelection(object sender, SelectionChangedEventArgs e)
    {
        // A seleção vai da janela para a lista (OneWay); o clique do usuário volta para a janela aqui.
        if (Nav.SelectedItem is PageViewModel page)
        {
            if (!ReferenceEquals(page, _vm.SelectedPage)) _vm.SelectedPage = page;
            return;
        }
        if (e.RemovedItems.Count > 0 && _vm.NavPages.Contains(_vm.SelectedPage))
            Nav.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, _vm.SelectedPage);
    }

    // ------------------------------------------------------------------ bandeja

    /// <summary>Mostra a janela de novo (bandeja, segunda instância, notificação).</summary>
    public void RestoreFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowActivated = true;
        Activate();
        Topmost = true;  // traz para frente mesmo se outra janela tiver o foco
        Topmost = false;
        Focus();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(ApplyTray));

    /// <summary>Cria ou remove o ícone conforme a preferência. Sem ícone, a janela nunca fica escondida.</summary>
    private void ApplyTray()
    {
        var want = UiSettings.Current.TrayIcon;
        if (want && _tray is null)
        {
            try
            {
                _tray = new TrayIcon();
                _tray.SetTip(Loc.T("Shell.NexusOtimizacaoSeguranca"));
                _tray.OpenRequested += (_, _) => RestoreFromTray();
                _tray.OptimizeRequested += (_, _) => OptimizeFromTray();
                _tray.ScanRequested += async (_, _) => await ScanFromTrayAsync();
                _tray.ExitRequested += (_, _) => ExitApp();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                _tray = null;
                _vm.Notify(Loc.F("Shell.NaoFoiPossivelCriar", ex.Message));
            }
        }
        else if (!want && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
            if (!IsVisible) RestoreFromTray();
        }
    }

    private bool TrayActive => _tray?.Visible == true;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && TrayActive && UiSettings.Current.MinimizeToTray) Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || App.RelaunchRequested || !TrayActive || !UiSettings.Current.CloseToTray) return;
        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray!.Notify(Loc.T("Shell.NexusContinuaBandeja"), Loc.T("Shell.SairVezCliqueBotao"));
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
        Application.Current?.Shutdown();
    }

    private void OptimizeFromTray()
    {
        RestoreFromTray();
        _vm.SelectedPage = _vm.Automation;
        if (_vm.Automation.OptimizeCommand.CanExecute(null)) _vm.Automation.OptimizeCommand.Execute(null);
        else _vm.Notify(Loc.T("Shell.AguardeLeituraPcTerminar"));
    }

    /// <summary>"Verificar agora": relê o PC (somente leitura), grava o relatório e avisa com "abrir relatório".</summary>
    private async Task ScanFromTrayAsync()
    {
        try
        {
            _tray?.Notify(Loc.T("Shell.VerificandoPc"), Loc.T("Shell.SoLeituraAvisoQuando"));
            await _vm.LoadAsync();
            var (report, summary) = await _vm.Automation.SaveReportAsync();
            if (report is null) _tray?.Notify(Loc.T("Shell.VerificacaoNaoConcluida"), summary, warning: true);
            else _tray?.Notify(Loc.T("Shell.VerificacaoNexusConcluida"), summary + Loc.T("Shell.CliqueAbrirRelatorio"), report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _tray?.Notify(Loc.T("Shell.VerificacaoNaoConcluida"), ex.Message, warning: true);
        }
    }
}
