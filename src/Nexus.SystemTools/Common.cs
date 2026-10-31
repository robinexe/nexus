using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nexus.Core;

namespace Nexus.SystemTools;

/// <summary>Saída de uma ferramenta do Windows (netsh, pnputil, ipconfig, PowerShell).</summary>
public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : Output + Environment.NewLine + Error;
}

/// <summary>Executa ferramentas do próprio Windows sem janela. Os argumentos vão por ArgumentList (nunca por texto montado).</summary>
public interface IProcessRunner
{
    /// <param name="utf8">A ferramenta escreve UTF-8 (PowerShell com OutputEncoding ajustado); senão, página OEM do console.</param>
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, bool utf8 = false);
}

/// <summary>Caminhos das ferramentas do sistema (sempre absolutos, nunca resolvidos pelo PATH).</summary>
public static class SystemPaths
{
    /// <summary>System32 real (Sysnative quando o processo é de 32 bits num Windows de 64 bits).</summary>
    public static string System32
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!Environment.Is64BitProcess && Environment.Is64BitOperatingSystem)
            {
                var sysnative = Path.Combine(windows, "Sysnative");
                if (Directory.Exists(sysnative)) return sysnative;
            }
            return Environment.SystemDirectory;
        }
    }

    public static string Tool(string exe) => Path.Combine(System32, exe);

    public static string PowerShell => Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");
}

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, bool utf8 = false)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            // Bytes crus: várias ferramentas (netsh no Windows 11) escrevem UTF-8 mesmo com o console em 850; decidimos depois.
            StandardOutputEncoding = Latin1,
            StandardErrorEncoding = Latin1,
            WorkingDirectory = SystemPaths.System32,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(fileName)}.");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            throw new TimeoutException($"{Path.GetFileName(fileName)} não terminou em {timeout.TotalSeconds:0} s.");
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, Decode(stdout.GetAwaiter().GetResult(), utf8), Decode(stderr.GetAwaiter().GetResult(), utf8));
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>UTF-8 quando os bytes são UTF-8 válido (ou pedido); senão a página OEM do console.</summary>
    internal static string Decode(string latin1, bool utf8)
    {
        var bytes = Latin1.GetBytes(latin1);
        if (utf8 || ProcessText.IsValidUtf8(bytes)) return new UTF8Encoding(false).GetString(bytes);
        return OemEncoding().GetString(bytes);
    }

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (ArgumentException) { return Encoding.UTF8; }
        catch (NotSupportedException) { return Encoding.UTF8; }
    }
}

/// <summary>Decisão de codificação da saída de ferramentas (pura, testável).</summary>
public static class ProcessText
{
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>Verdadeiro se os bytes formam UTF-8 válido E contêm algo além de ASCII (ASCII puro é igual nas duas páginas).</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var ascii = true;
        foreach (var b in bytes) if (b >= 0x80) { ascii = false; break; }
        if (ascii) return false;
        try { Strict.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}

/// <summary>PowerShell do Windows (5.1) em processo oculto, com o script codificado (sem montar linha de comando com texto).</summary>
public static class PowerShellRunner
{
    public static ProcessResult Run(IProcessRunner runner, string script, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return runner.Run(SystemPaths.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout, utf8: true);
    }

    /// <summary>Literal de texto do PowerShell entre aspas simples (aspas internas dobradas).</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}

/// <summary>
/// Ações "efêmeras" (limpar cache DNS/ARP, criar ponto de restauração, exportar drivers): não há estado anterior a restaurar.
/// O núcleo só conhece <see cref="PersistenceKind.Session"/> e <see cref="PersistenceKind.Persistent"/>; estas ações usam
/// Persistent (Session prenderia a trava do aplicador até EndSession) e o estado lido é um marcador:
/// <list type="bullet">
/// <item>Detect = "pronto", ou "executado" por <see cref="Window"/> logo depois de executar (para a releitura confirmar);</item>
/// <item>Restore não muda nada no sistema (só limpa o marcador): desfazer é declarado "nada a restaurar", nunca simulado.</item>
/// </list>
/// </summary>
public sealed class EphemeralMarker
{
    public const string Type = "Ephemeral";
    public const string Ready = "pronto";
    public const string Done = "executado";
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, DateTime> _marks = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;

    public EphemeralMarker(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public static string KeyOf(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Definition.Id + "|" + action.TargetDescription;
    }

    public StateSnapshot Read(string key) =>
        StateSnapshot.Of(Type, _marks.TryGetValue(key, out var at) && _clock() - at <= Window ? Done : Ready);

    public void Mark(string key) => _marks[key] = _clock();

    public void Clear(string key) => _marks.TryRemove(key, out _);
}

/// <summary>Base dos handlers efêmeros (ver <see cref="EphemeralMarker"/>).</summary>
public abstract class EphemeralHandler : IActionHandler, IRestoreValidator
{
    private readonly EphemeralMarker _marker;

    protected EphemeralHandler(EphemeralMarker? marker) => _marker = marker ?? new EphemeralMarker();

    public abstract string HandlerKind { get; }

    /// <summary>Executa a operação. Exceção = não feito (o executor registra a falha; nada a compensar).</summary>
    protected abstract void Run(ActionInstance action);

    protected virtual Applicability AssessCore(ActionInstance action, SystemContext context) => Applicability.Ok;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Desired.Type != EphemeralMarker.Type || action.Desired.Value != EphemeralMarker.Done)
            return Applicability.Blocked("Valor desejado inválido para uma ação efêmera.");
        return AssessCore(action, context);
    }

    public StateSnapshot Detect(ActionInstance action) => _marker.Read(EphemeralMarker.KeyOf(action));

    public void Apply(ActionInstance action)
    {
        Run(action);
        _marker.Mark(EphemeralMarker.KeyOf(action));
    }

    /// <summary>Nada a restaurar no sistema: a operação não guarda estado anterior (documentado em docs/modules/system.md).</summary>
    public void Restore(ActionInstance action, StateSnapshot original) => _marker.Clear(EphemeralMarker.KeyOf(action));

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original.Type == EphemeralMarker.Type && original.Value == EphemeralMarker.Ready ? null : "Original inválido para ação efêmera.";
    }
}

/// <summary>Estados das ações que removem algo com backup (perfil Wi-Fi, pacote de driver, app do Windows).</summary>
public static class PresenceState
{
    public const string Present = "presente";
    public const string Removed = "removido";

    public static StateSnapshot Of(string type, bool present) => StateSnapshot.Of(type, present ? Present : Removed);
}

internal static partial class Text
{
    [GeneratedRegex(@"[\x00-\x1F\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlChars();

    public static bool HasControl(string s) => ControlChars().IsMatch(s);

    public static string ShortHash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>Grava atomicamente (arquivo temporário na mesma pasta + troca).</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Remove acentos e baixa a caixa (para casar rótulos de saídas em pt/en).</summary>
    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
