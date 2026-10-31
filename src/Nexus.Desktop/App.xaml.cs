using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Nexus.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>Erros inesperados não fecham o aplicativo em silêncio: são mostrados e registrados no perfil do usuário.</summary>
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
        MessageBox.Show(e.Exception.Message, "NEXUS — erro inesperado (nada foi alterado no sistema)", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
