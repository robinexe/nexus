using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Programs.Updates;

/// <summary>Gerenciador encontrado (ou não) neste PC, com a versão lida e uma explicação curta da fonte.</summary>
public sealed record PackageManagerStatus(PackageManagerKind Kind, bool Installed, string? Version, string? ExecutablePath, string? Problem = null)
{
    public string DisplayName => PackageManagerInfo.DisplayName(Kind);
    public string Explanation => PackageManagerInfo.Explanation(Kind);
    public string StatusText => Installed ? (Version is null ? "Instalado" : $"Instalado · {Version}") : "Não encontrado";
}

public sealed record UpdateListResult(PackageManagerKind Kind, IReadOnlyList<PackageUpdate> Updates, string? Problem);

public static class PackageManagerInfo
{
    public static string DisplayName(PackageManagerKind kind) => kind switch
    {
        PackageManagerKind.Winget => "winget",
        PackageManagerKind.Chocolatey => "Chocolatey",
        PackageManagerKind.Scoop => "Scoop",
        _ => "npm (pacotes globais)",
    };

    public static string Explanation(PackageManagerKind kind) => kind switch
    {
        PackageManagerKind.Winget => "Gerenciador de pacotes da Microsoft, já vem no Windows 10/11 (App Installer). Compara o que está instalado com o repositório oficial do winget e da Store.",
        PackageManagerKind.Chocolatey => "Gerenciador da comunidade. Atualizar exige administrador: o NEXUS abre a janela do próprio Chocolatey com o aviso do Windows, e você acompanha tudo.",
        PackageManagerKind.Scoop => "Gerenciador por usuário (sem administrador). Só conhece os programas que você instalou por ele.",
        _ => "Ferramentas de linha de comando instaladas com 'npm install -g'. Só aparece se o Node.js estiver instalado.",
    };

    /// <summary>
    /// Script (texto fixo + IDs já validados por <see cref="PackageIdRules"/>) que a janela visível do PowerShell executa.
    /// Cada ID vai entre aspas simples; nenhum outro texto do usuário entra.
    /// </summary>
    public static string BuildUpgradeScript(PackageManagerKind kind, string executablePath, IReadOnlyList<PackageUpdate> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("Nada para atualizar.", nameof(items));
        foreach (var i in items)
        {
            if (i.Manager != kind) throw new ArgumentException("Pacote de outro gerenciador.", nameof(items));
            if (!PackageIdRules.IsValid(kind, i.Id)) throw new ArgumentException($"ID fora do formato permitido: {i.Id}", nameof(items));
            if (kind == PackageManagerKind.Winget && !PackageIdRules.IsValidWingetSource(i.Source)) throw new ArgumentException($"Origem inválida para {i.Id}.", nameof(items));
        }
        if (executablePath.Contains('\'', StringComparison.Ordinal) || executablePath.IndexOfAny(['\r', '\n', '`', '$']) >= 0)
            throw new ArgumentException("Caminho do gerenciador com caracteres não permitidos.", nameof(executablePath));

        var sb = new StringBuilder();
        sb.Append("$Host.UI.RawUI.WindowTitle = 'NEXUS - atualizando com ").Append(DisplayName(kind)).Append("'\n");
        sb.Append("Write-Host 'NEXUS: esta janela é do próprio ").Append(DisplayName(kind))
          .Append(". Acompanhe as mensagens; o NEXUS não altera nada por conta própria.' -ForegroundColor Cyan\n");
        var exe = "& '" + executablePath + "'";
        switch (kind)
        {
            case PackageManagerKind.Winget:
                foreach (var i in items)
                    sb.Append(exe).Append(" upgrade --id '").Append(i.Id).Append("' --exact --source '").Append(i.Source).Append("'\n");
                break;
            case PackageManagerKind.Chocolatey:
                sb.Append(exe).Append(" upgrade ").Append(string.Join(' ', items.Select(i => "'" + i.Id + "'"))).Append('\n');
                break;
            case PackageManagerKind.Scoop:
                sb.Append(exe).Append(" update ").Append(string.Join(' ', items.Select(i => "'" + i.Id + "'"))).Append('\n');
                break;
            case PackageManagerKind.Npm:
                sb.Append(exe).Append(" install -g ").Append(string.Join(' ', items.Select(i => "'" + i.Id + "@latest'"))).Append('\n');
                break;
        }
        sb.Append("Write-Host ''\nWrite-Host 'NEXUS: terminou. Confira as mensagens acima e feche esta janela.' -ForegroundColor Cyan\n");
        return sb.ToString();
    }
}

/// <summary>Detecta e lê os gerenciadores (processos ocultos, só leitura) e abre a atualização numa janela VISÍVEL.</summary>
[SupportedOSPlatform("windows")]
public static class PackageManagers
{
    public static readonly PackageManagerKind[] All = [PackageManagerKind.Winget, PackageManagerKind.Chocolatey, PackageManagerKind.Scoop, PackageManagerKind.Npm];

    public static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string? Locate(PackageManagerKind kind)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string?> candidates = kind switch
        {
            PackageManagerKind.Winget => [Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"), FromPath("winget.exe")],
            PackageManagerKind.Chocolatey => [Env("ChocolateyInstall", "bin", "choco.exe"), Path.Combine(programData, "chocolatey", "bin", "choco.exe"), FromPath("choco.exe")],
            PackageManagerKind.Scoop => [Env("SCOOP", "shims", "scoop.ps1"), Path.Combine(profile, "scoop", "shims", "scoop.ps1"), FromPath("scoop.ps1"), FromPath("scoop.cmd")],
            _ => [FromPath("npm.cmd"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd")],
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    private static string? Env(string variable, params string[] parts)
    {
        var v = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(v) || !Path.IsPathFullyQualified(v) ? null : Path.Combine([v, .. parts]);
    }

    private static string? FromPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(dir)) continue; // nunca pastas relativas do PATH
            var p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>npm: roda <c>node.exe npm-cli.js</c> direto quando existem (evita passar por cmd.exe); senão o npm.cmd.</summary>
    private static (string File, List<string> Prefix) NpmInvocation(string npmCmd)
    {
        var dir = Path.GetDirectoryName(npmCmd)!;
        var node = Path.Combine(dir, "node.exe");
        var cli = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
        return File.Exists(node) && File.Exists(cli) ? (node, [cli]) : (npmCmd, []);
    }

    private static (string File, List<string> Args) PowerShell(string script) =>
        (PowerShellPath, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);

    public static async Task<PackageManagerStatus> DetectAsync(PackageManagerKind kind, IProcessRunner runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var path = Locate(kind);
        if (path is null) return new PackageManagerStatus(kind, false, null, null);
        try
        {
            ProcessOutput r;
            switch (kind)
            {
                case PackageManagerKind.Scoop:
                    return new PackageManagerStatus(kind, true, null, path);
                case PackageManagerKind.Npm:
                    var (file, prefix) = NpmInvocation(path);
                    r = await runner.RunAsync(file, [.. prefix, "--version"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    break;
                default:
                    r = await runner.RunAsync(path, ["--version"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    break;
            }
            var version = FixedWidthTable.CleanLines(r.StdOut).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l.Length < 40);
            return new PackageManagerStatus(kind, true, r.ExitCode == 0 ? version?.TrimStart('v') : null, path, r.ExitCode == 0 ? null : "Não respondeu à consulta de versão.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new PackageManagerStatus(kind, true, null, path, ex.Message);
        }
    }

    public static async Task<UpdateListResult> ListUpdatesAsync(PackageManagerStatus status, IProcessRunner runner, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(runner);
        if (!status.Installed || status.ExecutablePath is null) return new(status.Kind, [], null);
        var exe = status.ExecutablePath;
        try
        {
            switch (status.Kind)
            {
                case PackageManagerKind.Winget:
                {
                    var r = await runner.RunAsync(exe, ["upgrade", "--include-unknown", "--disable-interactivity"], TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
                    if (r.TimedOut) return new(status.Kind, [], "O winget não respondeu em 3 minutos.");
                    var list = WingetParser.ParseUpgradeTable(r.StdOut);
                    if (list.Count == 0 && WingetParser.NeedsSourceAgreement(r.StdOut + r.StdErr))
                        return new(status.Kind, [], "O winget pede que você aceite os termos da origem uma vez: abra um terminal e rode 'winget upgrade'.");
                    if (list.Count == 0 && r.ExitCode != 0 && !LooksLikeNoUpdates(r.StdOut))
                    {
                        // Tabela ilegível (idioma/versão diferente): tenta o módulo oficial Microsoft.WinGet.Client, se instalado.
                        var viaModule = await TryWingetModuleAsync(runner, ct).ConfigureAwait(false);
                        if (viaModule is not null) return new(status.Kind, viaModule, null);
                        return new(status.Kind, [], "Não foi possível ler a resposta do winget: " + LastLine(r.StdOut + "\n" + r.StdErr));
                    }
                    return new(status.Kind, list, null);
                }
                case PackageManagerKind.Chocolatey:
                {
                    var r = await runner.RunAsync(exe, ["outdated", "-r"], TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
                    if (r.TimedOut) return new(status.Kind, [], "O Chocolatey não respondeu em 3 minutos.");
                    return new(status.Kind, ChocolateyParser.ParseOutdated(r.StdOut), null);
                }
                case PackageManagerKind.Scoop:
                {
                    var (file, args) = PowerShell("& '" + exe.Replace("'", "''", StringComparison.Ordinal) + "' status *>&1 | Out-String -Width 400");
                    var r = await runner.RunAsync(file, args, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
                    if (r.TimedOut) return new(status.Kind, [], "O Scoop não respondeu em 3 minutos.");
                    return new(status.Kind, ScoopParser.ParseStatus(r.StdOut),
                        ScoopParser.ScoopOutOfDate(r.StdOut) ? "O próprio Scoop está desatualizado; a lista pode estar incompleta (rode 'scoop update')." : null);
                }
                default:
                {
                    var (file, prefix) = NpmInvocation(exe);
                    var r = await runner.RunAsync(file, [.. prefix, "outdated", "-g", "--json"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                    if (r.TimedOut) return new(status.Kind, [], "O npm não respondeu em 2 minutos.");
                    return new(status.Kind, NpmParser.ParseOutdated(r.StdOut), null); // código 1 = há desatualizados (normal)
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new(status.Kind, [], ex.Message);
        }
    }

    private static async Task<IReadOnlyList<PackageUpdate>?> TryWingetModuleAsync(IProcessRunner runner, CancellationToken ct)
    {
        const string script =
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; if (Get-Module -ListAvailable -Name Microsoft.WinGet.Client) { Import-Module Microsoft.WinGet.Client; " +
            "Get-WinGetPackage | Where-Object { $_.IsUpdateAvailable } | Select-Object Name,Id,InstalledVersion,@{n='AvailableVersion';e={@($_.AvailableVersions)[0]}},Source | " +
            "ConvertTo-Json -Depth 3 -Compress } else { 'NO-MODULE' }";
        var (file, args) = PowerShell(script);
        var r = await runner.RunAsync(file, args, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (r.TimedOut || r.ExitCode != 0 || r.StdOut.Contains("NO-MODULE", StringComparison.Ordinal)) return null;
        try { return WingetParser.ParseClientJson(r.StdOut); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static bool LooksLikeNoUpdates(string output) =>
        output.Contains("No installed package", StringComparison.OrdinalIgnoreCase) || output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Nenhum", StringComparison.OrdinalIgnoreCase);

    private static string LastLine(string text) =>
        FixedWidthTable.CleanLines(text).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "sem mensagem";

    /// <summary>
    /// Abre a atualização numa janela VISÍVEL do PowerShell rodando o próprio gerenciador (exceção controlada, depois de
    /// <c>Host.Confirm</c>). Chocolatey pede administrador: o Windows mostra o aviso e a janela aparece. Scoop e npm, nunca elevados.
    /// </summary>
    public static Process? StartUpgradeWindow(PackageManagerStatus status, IReadOnlyList<PackageUpdate> items)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.Installed || status.ExecutablePath is null) throw new InvalidOperationException($"{status.DisplayName} não foi encontrado.");
        var script = PackageManagerInfo.BuildUpgradeScript(status.Kind, status.ExecutablePath, items);
        var psi = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = true, // janela própria, visível
            Verb = status.Kind == PackageManagerKind.Chocolatey ? "runas" : "",
        };
        foreach (var a in new[] { "-NoExit", "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
