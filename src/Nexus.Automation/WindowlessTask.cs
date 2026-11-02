namespace Nexus.Automation;

/// <summary>
/// Regras do <c>nexus-task.exe</c>: a linha de comando do NEXUS compilada como aplicativo de janela (WinExe), sem console, para
/// as tarefas agendadas não abrirem uma janela preta. Como não há console para perguntar nem para mostrar erro, ele só aceita
/// EXATAMENTE as linhas de comando que o NEXUS grava nas tarefas (lista fechada: verificação/limpeza com as condições
/// possíveis, e a verificação semanal antiga). Qualquer outra coisa sai com código de uso inválido sem fazer nada.
/// </summary>
public static class WindowlessTask
{
    /// <summary>Todas as linhas de comando aceitas (as mesmas que <see cref="NexusTaskXml"/> e <see cref="ScheduledScanTask"/> geram).</summary>
    public static IReadOnlyList<string> AllowedCommandLines { get; } =
        NexusTaskKind.All.SelectMany(k => TaskConditions.All.Select(k.ArgumentsFor))
            .Append(ScheduledScanTask.Arguments)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Os argumentos recebidos são uma das linhas de comando das tarefas? O Agendador divide a linha nos espaços (não há aspas
    /// nem texto livre nelas), então juntar com um espaço reproduz a linha gravada.
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args.Any(a => a.Length == 0 || a.Any(char.IsWhiteSpace))) return false;
        var line = string.Join(' ', args);
        return AllowedCommandLines.Contains(line, StringComparer.Ordinal);
    }
}
