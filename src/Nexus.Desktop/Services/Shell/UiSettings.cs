using Nexus.Automation;

namespace Nexus.Desktop.Services.Shell;

/// <summary>
/// Preferências da interface (bandeja, notificações) em %LocalAppData%\NEXUS\settings.json. Só preferências: nada aqui muda o
/// sistema. A página Ajustes altera; a janela e a bandeja escutam <see cref="Changed"/>.
/// </summary>
public static class UiSettings
{
    private static NexusSettings? _current;
    private static string? _problem;

    public static string Path => NexusSettings.DefaultPath;

    /// <summary>Preferências atuais (lidas na primeira vez; arquivo corrompido = padrões).</summary>
    public static NexusSettings Current
    {
        get
        {
            if (_current is null) _current = NexusSettings.Load(Path, out _problem);
            return _current;
        }
    }

    /// <summary>Problema da última leitura/gravação (ex.: arquivo corrompido, usando padrões), ou null.</summary>
    public static string? Problem => _problem;

    public static event EventHandler? Changed;

    /// <summary>Troca e grava as preferências. Retorna o problema da gravação, ou null.</summary>
    public static string? Update(Func<NexusSettings, NexusSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current);
        if (next == _current) return null;
        _current = next;
        _problem = next.Save(Path);
        Changed?.Invoke(null, EventArgs.Empty);
        return _problem;
    }
}
