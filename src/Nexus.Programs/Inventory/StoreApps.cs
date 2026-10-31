using System.Text;
using System.Text.Json;

namespace Nexus.Programs.Inventory;

/// <summary>App da Microsoft Store (pacote Appx/MSIX) do usuário atual. Só leitura.</summary>
public sealed record StoreApp(string Name, string PackageFullName, string? Publisher, string? Version, string? InstallLocation, bool NonRemovable)
{
    /// <summary>Nome amigável: "Microsoft.WindowsCalculator" vira "WindowsCalculator" (o nome de exibição exige ler o manifesto).</summary>
    public string DisplayName => Name.Contains('.', StringComparison.Ordinal) ? Name[(Name.IndexOf('.', StringComparison.Ordinal) + 1)..] : Name;
}

/// <summary>
/// Lista os apps da Store do usuário com <c>Get-AppxPackage</c> (cmdlet de LEITURA do Windows) em PowerShell oculto, sem
/// frameworks e sem pacotes de sistema. O texto do script é fixo (nenhum dado do usuário entra nele).
/// </summary>
public static class StoreApps
{
    public const string Script =
        "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "Get-AppxPackage | Where-Object { -not $_.IsFramework -and [string]$_.SignatureKind -ne 'System' } | " +
        "Select-Object Name,PackageFullName,Publisher,@{n='Version';e={[string]$_.Version}},InstallLocation,@{n='NonRemovable';e={[bool]$_.NonRemovable}} | " +
        "ConvertTo-Json -Depth 2 -Compress";

    public static async Task<IReadOnlyList<StoreApp>> ReadAsync(IProcessRunner runner, string powershellPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        var r = await runner.RunAsync(powershellPath, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (r.TimedOut) throw new TimeoutException("Get-AppxPackage não respondeu em 60 s.");
        return Parse(r.StdOut);
    }

    /// <summary>JSON do ConvertTo-Json: um objeto (um app só) ou uma lista. Saída vazia = nenhum app.</summary>
    public static IReadOnlyList<StoreApp> Parse(string json)
    {
        var text = (json ?? "").Trim().TrimStart('﻿');
        if (text.Length == 0) return [];
        using var doc = JsonDocument.Parse(text);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var list = new List<StoreApp>();
        foreach (var e in items)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var name = Str(e, "Name");
            var full = Str(e, "PackageFullName");
            if (name is null || full is null) continue;
            list.Add(new StoreApp(name, full, Str(e, "Publisher"), Str(e, "Version"), Str(e, "InstallLocation"),
                e.TryGetProperty("NonRemovable", out var nr) && nr.ValueKind == JsonValueKind.True));
        }
        return list.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;
}
