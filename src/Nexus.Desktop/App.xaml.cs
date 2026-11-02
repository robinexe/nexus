using System.IO;
using System.Windows;
using System.Windows.Threading;
using Nexus.Desktop.Services.Shell;

namespace Nexus.Desktop;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application não é IDisposable; os handles da instância única são liberados em OnExit.")]
public partial class App : Application
{
    // Uma instância por sessão do usuário: abrir de novo (atalho, iniciar com o Windows) só traz a janela que já existe.
    private const string InstanceName = @"Local\NEXUS.Desktop.Instance";
    private const string ShowEventName = @"Local\NEXUS.Desktop.Show";
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    /// <summary>
    /// Idioma antes de qualquer XAML (InitializeComponent carrega os dicionários e os {l:T} logo depois deste construtor):
    /// --lang na linha de comando, senão a preferência de Ajustes, senão o idioma do Windows (ver LanguageOption).
    /// </summary>
    public App() =>
        Loc.Use(LanguageOption.Resolve(LanguageOption.FromArgs(Environment.GetCommandLineArgs()), UiSettings.Current.Language,
            System.Globalization.CultureInfo.CurrentUICulture));

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        Services.Shell.UiWatchdog.Start(Dispatcher);
        Services.Shell.UiWatchdog.Step("interface iniciada");
        base.OnStartup(e);

        // Autoteste da interface: sem instância única (não atrapalha nem é atrapalhado por um NEXUS aberto), sem bandeja
        // minimizada, sem aplicador e sem janelas de erro. Ver SelfTest/UiSelfTestRunner.cs e docs/09-autoteste.md.
        if (SelfTest.UiSelfTestRunner.Parse(e.Args) is { } selfTest)
        {
            ViewModels.MainViewModel.SelfTestMode = true;
            var testWindow = new MainWindow();
            testWindow.Show();
            SelfTest.UiSelfTestRunner.Start(testWindow, selfTest.Path, selfTest.Screenshots);
            ViewModels.AppUpdates.Instance.Start(e.Args, selfTest: true); // autoteste: atualização desligada
            return;
        }

        _instance = new Mutex(initiallyOwned: true, InstanceName, out var first);
        if (!first && (e.Args.Contains("--admin-relaunch") || e.Args.Contains(RelaunchArgument)))
        {
            // Reaberto pelo próprio NEXUS (como administrador, ou para trocar o idioma): a cópia anterior está fechando;
            // espera ela soltar a trava.
            try { first = _instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(ShowEventName); signal.Set(); }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { }
            _instance.Dispose();
            _instance = null;
            Shutdown();
            return;
        }

        MainWindow window;
        using (UiWatchdog.Phase("criando a janela (XAML + páginas)")) window = new MainWindow { StartInTray = StartupArguments.Minimized(e.Args) };
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(window.RestoreFromTray)), null, Timeout.Infinite, executeOnlyOnce: false);
        if (window.StartInTray)
        {
            // Iniciar com o Windows: abre minimizado e, com o ícone da bandeja ligado, esconde ao terminar de carregar.
            window.WindowState = WindowState.Minimized;
            window.ShowActivated = false;
        }
        using (UiWatchdog.Phase("mostrando a janela (estilos, layout)")) window.Show();
        // Atualização do programa: procura 30 s depois de abrir e a cada 6 h (se ligada em Ajustes); docs/11-atualizacao.md.
        using (UiWatchdog.Phase("atualização: agendar procura", logAboveMs: 30)) ViewModels.AppUpdates.Instance.Start(e.Args, selfTest: false);
    }

    /// <summary>Argumento do reinício pedido em Ajustes (troca de idioma): espera a cópia atual fechar.</summary>
    public const string RelaunchArgument = "--relaunch";

    /// <summary>Verdadeiro depois de <see cref="Relaunch"/>: a janela fecha de verdade.</summary>
    public static bool RelaunchRequested { get; private set; }

    /// <summary>Reabre o NEXUS (mesma elevação) e fecha esta cópia. Usado ao trocar o idioma em Ajustes.</summary>
    public static bool Relaunch()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, RelaunchArgument) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        RelaunchRequested = true; // "fechar para a bandeja" não segura a janela neste caso
        Current.Shutdown();
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Versão nova já baixada e conferida, e o usuário não clicou "Reiniciar e atualizar": instala agora, ao fechar.
        if (_instance is not null) ViewModels.AppUpdates.Instance.OnAppExit();
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { }
            _instance.Dispose();
        }
        base.OnExit(e);
    }

    /// <summary>Erros inesperados não fecham o aplicativo em silêncio: são mostrados e registrados no perfil do usuário.</summary>
    private readonly HashSet<string> _shownErrors = new(StringComparer.Ordinal);
    private bool _showingError;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var text = $"{DateTimeOffset.Now:o} {e.Exception}";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "desktop-erros.log"), text + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        e.Handled = true;
        if (SelfTest.UiSelfTestRunner.Current is { } selfTest) { selfTest.RecordException(e.Exception); return; } // autoteste: registra, sem janela
        // Um erro que se repete (ex.: binding a cada atualização) vira UMA janela, não uma pilha: o mesmo texto só aparece uma vez,
        // e nunca duas janelas ao mesmo tempo. O registro em desktop-erros.log continua completo.
        var key = e.Exception.GetType().FullName + ":" + e.Exception.Message;
        if (_showingError || !_shownErrors.Add(key)) return;
        _showingError = true;
        try
        {
            MessageBox.Show(e.Exception.Message + "\n\nSe repetir, não aparece de novo nesta sessão. Detalhes em %LocalAppData%\\NEXUS\\desktop-erros.log.",
                Loc.T("Shell.NexusErroInesperadoNada"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _showingError = false; }
    }
}
