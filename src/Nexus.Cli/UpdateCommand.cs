using System.Text.Json;
using Nexus.Automation;
using Nexus.Updates;

namespace Nexus.Cli;

/// <summary>
/// <c>nexus update</c>: procura atualização (só leitura; não baixa nem instala) e confere um manifesto assinado com a chave
/// embutida (usado pelo release.yml para provar que o segredo de assinatura corresponde à chave do aplicativo).
/// </summary>
internal static class UpdateCommand
{
    public const string Usage =
        "nexus update --check [--json]\n" +
        "  Procura uma versão nova (não baixa nem instala; quem instala é a interface). Sai com 0 mesmo sem nada publicado.\n" +
        "nexus update --verify-manifest <nexus-update.json> [--sig <arquivo.sig>]\n" +
        "  Confere a assinatura (chave embutida) e o conteúdo do manifesto. 0 = válido; 1 = recusado.";

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return args.Count == 0 ? CliExit.Usage : CliExit.Ok; }
        var version = AppVersionInfo.FromAssembly(typeof(UpdateCommand).Assembly);
        if (args[0] == "--check" && args.Skip(1).All(a => a == "--json")) return await CheckAsync(version, args.Contains("--json")).ConfigureAwait(false);
        if (args[0] == "--verify-manifest" && args.Count is 2 or 4 && (args.Count == 2 || args[2] == "--sig"))
            return Verify(version, args[1], args.Count == 4 ? args[3] : args[1] + ".sig");
        Console.Error.WriteLine("Uso inválido.\n" + Usage);
        return CliExit.Usage;
    }

    private static async Task<int> CheckAsync(AppVersionInfo version, bool json)
    {
        var kind = OperatingSystem.IsWindows() ? UpdateInstallation.Detect(version.Packaged, AppContext.BaseDirectory)
            : version.Packaged ? InstallKind.Portable : InstallKind.Development;
        using var client = new UpdateClient(version.Version);
        var result = await client.CheckAsync().ConfigureAwait(false);
        var mode = result.Manifest is { } m && result.Status == UpdateCheckStatus.Available ? UpdateInstallation.ModeFor(kind, m, version.Version) : (UpdateMode?)null;
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                current = version.Display,
                kind = kind.ToString(),
                feed = client.Feed.ToString(),
                status = result.Status.ToString(),
                message = result.Message,
                available = result.Status == UpdateCheckStatus.Available ? result.Manifest?.Version.ToString() : null,
                mode = mode?.ToString(),
            }));
        }
        else
        {
            Console.WriteLine($"NEXUS {version.Display} ({KindText(kind)})");
            Console.WriteLine($"Fonte: {client.Feed}");
            Console.WriteLine(result.Message);
            if (mode is UpdateMode.NotifyOnly) Console.WriteLine($"Baixe em {UpdatePolicy.ReleasesPage}");
        }
        return CliExit.Ok;
    }

    private static int Verify(AppVersionInfo version, string manifestPath, string signaturePath)
    {
        byte[] manifest, signature;
        try
        {
            manifest = File.ReadAllBytes(manifestPath);
            signature = File.ReadAllBytes(signaturePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Não foi possível ler: {ex.Message}");
            return CliExit.NotFound;
        }
        using var client = new UpdateClient(version.Version);
        var result = client.Evaluate(manifest, signature);
        if (result.Status == UpdateCheckStatus.Failed || result.Manifest is null)
        {
            Console.Error.WriteLine($"RECUSADO: {result.Message}");
            return CliExit.Partial;
        }
        var m = result.Manifest;
        Console.WriteLine($"OK: assinatura válida (chave embutida); NEXUS {m.Version}, {m.Installer.Size} bytes, SHA-256 {m.Installer.Sha256}");
        Console.WriteLine($"    {m.Installer.Url}");
        return CliExit.Ok;
    }

    private static string KindText(InstallKind kind) => kind switch
    {
        InstallKind.Installed => "instalado",
        InstallKind.Portable => "portátil: só avisa",
        _ => "versão de desenvolvimento: sem atualização automática",
    };
}
