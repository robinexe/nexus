using Nexus.Programs.Inventory;

namespace Nexus.Programs.Uninstall;

/// <summary>Como o desinstalador de um item roda.</summary>
public enum UninstallMode
{
    /// <summary>Desinstalador do fornecedor com a janela dele: você conclui os passos.</summary>
    Interactive,
    /// <summary>Comando silencioso que o próprio fornecedor declarou (<c>QuietUninstallString</c>).</summary>
    VendorQuiet,
    /// <summary>Windows Installer com interface básica: <c>msiexec /x {GUID} /qb- /norestart</c> (só barra de progresso).</summary>
    MsiBasic,
}

/// <summary>Um item da fila, já com o comando validado.</summary>
public sealed record BulkQueueItem(InstalledProgram Program, UninstallCommand Command, UninstallMode Mode, BulkEligibility Eligibility, string? Note = null)
{
    public bool Critical => Eligibility.Critical;

    public string ModeText => Mode switch
    {
        UninstallMode.VendorQuiet => "Silencioso (declarado pelo fornecedor)",
        UninstallMode.MsiBasic => "Windows Installer, só barra de progresso",
        _ => "Janela do fornecedor (você conclui)",
    };

    public string KindText => Program.IsMsi || Command.IsMsi ? "Windows Installer (MSI)" : "Instalador próprio do fornecedor";
}

/// <summary>Fila montada + o que ficou de fora (com motivo).</summary>
public sealed record BulkQueuePlan(IReadOnlyList<BulkQueueItem> Items, IReadOnlyList<(InstalledProgram Program, string Reason)> Refused)
{
    public long KnownBytes => Items.Sum(i => i.Program.SizeBytes ?? 0);
    public int UnknownSize => Items.Count(i => i.Program.SizeBytes is null);
    public IReadOnlyList<BulkQueueItem> CriticalItems => Items.Where(i => i.Critical).ToList();
}

/// <summary>
/// Monta a fila de desinstalação. Ordem: primeiro os que abrem a janela do fornecedor (enquanto você está olhando), depois os
/// silenciosos (rodam sozinhos), e por último os marcados como críticos (bibliotecas e drivers dos quais outros programas da
/// fila podem depender); dentro de cada grupo, por nome. Nunca em paralelo (o executor roda um por vez).
/// <para>Modo silencioso só quando é confiável: MSI vira <c>msiexec /x {GUID} /qb- /norestart</c> (opções documentadas do
/// Windows Installer: interface básica sem caixas modais e sem reiniciar sozinho); senão, o <c>QuietUninstallString</c> que o
/// fornecedor gravou, passado pelo MESMO validador do desinstalador normal e só se apontar para o mesmo executável (ou a mesma
/// pasta). Nada de adivinhar opções (<c>/S</c>, <c>/VERYSILENT</c>): cada instalador entende uma coisa.</para>
/// </summary>
public static class BulkQueueBuilder
{
    public const string MsiQuietArguments = "/qb- /norestart";

    public static BulkQueuePlan Build(IEnumerable<InstalledProgram> selected, UninstallEnvironment env, bool preferQuiet)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(env);
        var items = new List<BulkQueueItem>();
        var refused = new List<(InstalledProgram, string)>();
        foreach (var p in selected.DistinctBy(p => p.Id))
        {
            var eligibility = BulkUninstallPolicy.Evaluate(p, env);
            if (!eligibility.Selectable) { refused.Add((p, eligibility.Reason ?? "Não pode ser desinstalado em lote.")); continue; }
            var interactive = env.Parse(p.UninstallString, out var error);
            if (interactive is null) { refused.Add((p, error ?? "Desinstalador inválido.")); continue; }
            var (command, mode, note) = preferQuiet ? ChooseQuiet(p, interactive, env) : (interactive, UninstallMode.Interactive, null);
            items.Add(new BulkQueueItem(p, command, mode, eligibility, note));
        }
        var ordered = items
            .OrderBy(i => i.Critical ? 2 : i.Mode == UninstallMode.Interactive ? 0 : 1)
            .ThenBy(i => i.Program.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new BulkQueuePlan(ordered, refused);
    }

    /// <summary>Escolhe o comando silencioso confiável, ou mantém o interativo com uma nota do porquê.</summary>
    public static (UninstallCommand Command, UninstallMode Mode, string? Note) ChooseQuiet(InstalledProgram program, UninstallCommand interactive, UninstallEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(interactive);
        ArgumentNullException.ThrowIfNull(env);
        if (interactive is { IsMsi: true, ProductCode: { } code })
            return (MsiBasic(interactive.FileName, code), UninstallMode.MsiBasic, null);
        if (string.IsNullOrWhiteSpace(program.QuietUninstallString))
            return (interactive, UninstallMode.Interactive, null);

        var quiet = env.Parse(program.QuietUninstallString, out var error);
        if (quiet is null)
            return (interactive, UninstallMode.Interactive, $"Modo silencioso declarado, mas recusado ({error}). Abre a janela do fornecedor.");
        if (quiet is { IsMsi: true, ProductCode: { } qcode })
            return (MsiBasic(quiet.FileName, qcode), UninstallMode.MsiBasic, null);
        if (!SameProgram(interactive.FileName, quiet.FileName))
            return (interactive, UninstallMode.Interactive, "O modo silencioso declarado aponta para outro executável. Abre a janela do fornecedor.");
        return (quiet, UninstallMode.VendorQuiet, null);
    }

    public static UninstallCommand MsiBasic(string msiexecPath, string productCode) =>
        new(msiexecPath, $"/x {productCode} {MsiQuietArguments}", true, productCode);

    private static bool SameProgram(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        Dir(a).Equals(Dir(b), StringComparison.OrdinalIgnoreCase);

    private static string Dir(string p) => p[..Math.Max(0, p.LastIndexOf('\\'))];
}
