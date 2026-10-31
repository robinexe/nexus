using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Core;

namespace Nexus.SystemTools.Restore;

public sealed record RestorePointInfo(long Sequence, string Description, DateTime? Created, int Type);

/// <summary>Lista de pontos (null + motivo quando o Windows não deixou ler, ex.: sem administrador).</summary>
public sealed record RestorePointList(IReadOnlyList<RestorePointInfo>? Items, string? Error);

public interface IRestorePointBackend
{
    /// <summary>SRSetRestorePointW. Retorna o código de status do Windows (0 = criado).</summary>
    int Create(string description);

    RestorePointList List();
}

/// <summary>
/// Cria um ponto de restauração manual (SRSetRestorePointW). Efêmero: não há o que "desfazer" num ponto criado (ele só é
/// apagado pelo próprio Windows quando o espaço reservado acaba). Confirma pela lista que um ponto novo existe; o Windows
/// pula a criação se já houve um ponto nas últimas 24 h (SystemRestorePointCreationFrequency) — então aceita o ponto
/// criado há poucos minutos (ex.: o que o aplicador do NEXUS cria antes de mudanças de sistema) e diz quando não há nenhum.
/// </summary>
public sealed class RestorePointHandler(IRestorePointBackend backend, Func<DateTime>? clock = null, EphemeralMarker? marker = null) : EphemeralHandler(marker)
{
    public const string Kind = "SystemRestorePoint";
    public const string Description = "NEXUS: ponto manual";
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    public override string HandlerKind => Kind;

    protected override void Run(ActionInstance action)
    {
        var before = backend.List().Items;
        var status = backend.Create(Description);
        if (status != 0) throw new InvalidOperationException(Explain(status));
        var after = backend.List().Items;
        if (after is null) return; // não deu para listar; o Windows informou sucesso
        var newest = after.Where(p => p.Created is not null).MaxBy(p => p.Created);
        var grew = before is not null && after.Count > before.Count;
        var recent = newest?.Created is { } c && _clock() - c <= RecentWindow;
        if (!grew && !recent)
            throw new InvalidOperationException("O Windows não criou um ponto novo: por padrão ele cria no máximo um a cada 24 horas. O ponto mais recente continua valendo.");
    }

    public static string Explain(int status) => status switch
    {
        1058 => "A Restauração do Sistema (Proteção do Sistema) está desligada neste PC. Ative em Proteção do Sistema para criar pontos.",
        1460 => "O Windows demorou demais para criar o ponto (tempo esgotado).",
        112 => "Pouco espaço em disco para o ponto de restauração.",
        _ => $"O Windows não criou o ponto de restauração (código {status}).",
    };
}

/// <summary>Leitura dos pontos (CIM <c>root/default:SystemRestore</c>) e do estado da Proteção do Sistema.</summary>
public static partial class RestoreParsers
{
    [GeneratedRegex(@"^(?<d>\d{14})\.\d{6}(?<s>[+-])(?<o>\d{3})$")]
    private static partial Regex Dmtf();

    [GeneratedRegex(@"^/Date\((?<ms>-?\d+)\)/$")]
    private static partial Regex JsonDate();

    [GeneratedRegex(@"\(([A-Za-z])%3A\)")]
    private static partial Regex ProtectedDrive();

    /// <summary>Data DMTF do WMI (yyyyMMddHHmmss.ffffff±UUU, deslocamento em minutos), /Date(ms)/ ou ISO 8601.</summary>
    public static DateTime? ParseWmiDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (Dmtf().Match(s) is { Success: true } m &&
            DateTime.TryParseExact(m.Groups["d"].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            var offset = int.Parse(m.Groups["o"].Value, CultureInfo.InvariantCulture) * (m.Groups["s"].Value == "-" ? -1 : 1);
            return new DateTimeOffset(local, TimeSpan.FromMinutes(offset)).LocalDateTime;
        }
        if (JsonDate().Match(s) is { Success: true } j)
            return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(j.Groups["ms"].Value, CultureInfo.InvariantCulture)).LocalDateTime;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var iso) ? iso : null;
    }

    public static RestorePointList ParseList(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json = json.Trim();
        if (json.Length == 0) return new RestorePointList([], null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            return new RestorePointList(null, err.GetString());
        var items = new List<RestorePointInfo>();
        IEnumerable<JsonElement> arr = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [root];
        foreach (var e in arr)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            long seq = e.TryGetProperty("SequenceNumber", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            var desc = e.TryGetProperty("Description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            var created = e.TryGetProperty("CreationTime", out var c) && c.ValueKind == JsonValueKind.String ? ParseWmiDate(c.GetString()) : null;
            var type = e.TryGetProperty("RestorePointType", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
            items.Add(new RestorePointInfo(seq, desc, created, type));
        }
        return new RestorePointList(items.OrderByDescending(i => i.Created).ToList(), null);
    }

    /// <summary>Unidades com Proteção do Sistema ligada, a partir do valor do SPP (ex.: <c>\\?\Volume{…}\:(C%3A)</c>).</summary>
    public static IReadOnlyList<char> ProtectedDrives(IEnumerable<string>? sppClients) =>
        (sppClients ?? []).SelectMany(v => ProtectedDrive().Matches(v).Select(m => char.ToUpperInvariant(m.Groups[1].Value[0])))
            .Distinct().Order().ToList();

    public static string TypeName(int type) => type switch
    {
        0 => "instalação de programa",
        1 => "desinstalação de programa",
        10 => "instalação de driver",
        12 => "alteração de configuração",
        13 => "operação cancelada",
        _ => "sistema",
    };
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsRestorePointBackend(IProcessRunner runner) : IRestorePointBackend
{
    private const int BeginSystemChange = 100, EndSystemChange = 101, ModifySettings = 12;
    private const string SppKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string SppValue = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    public int Create(string description)
    {
        var info = new RestorePointInfoNative { EventType = BeginSystemChange, RestorePtType = ModifySettings, SequenceNumber = 0, Description = description };
        StateMgrStatus status;
        try
        {
            if (!SRSetRestorePointW(ref info, out status)) return status.Status == 0 ? -1 : status.Status;
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException("Esta edição do Windows não tem Restauração do Sistema (ex.: Windows Server).");
        }
        var end = new RestorePointInfoNative { EventType = EndSystemChange, RestorePtType = ModifySettings, SequenceNumber = status.SequenceNumber, Description = description };
        SRSetRestorePointW(ref end, out _);
        return 0;
    }

    public RestorePointList List()
    {
        const string script = "try{$p=Get-CimInstance -Namespace root/default -ClassName SystemRestore -ErrorAction Stop|" +
                              "Select-Object SequenceNumber,Description,CreationTime,RestorePointType;" +
                              "ConvertTo-Json -Compress -Depth 2 -InputObject @($p)}" +
                              "catch{ConvertTo-Json -Compress -InputObject @{error=$_.Exception.Message};exit 5}";
        try
        {
            var r = PowerShellRunner.Run(runner, script, TimeSpan.FromSeconds(60));
            var parsed = RestoreParsers.ParseList(r.Output);
            if (parsed.Error is { } e)
                return new RestorePointList(null, e.Contains("denied", StringComparison.OrdinalIgnoreCase) || e.Contains("negado", StringComparison.OrdinalIgnoreCase)
                    ? "Listar pontos de restauração exige administrador." : e);
            return parsed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
        {
            return new RestorePointList(null, ex.Message);
        }
    }

    /// <summary>Unidades com Proteção do Sistema ligada (registro do SPP), ou null se não deu para ler.</summary>
    public static IReadOnlyList<char>? ProtectedDrives()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SppKey);
            return RestoreParsers.ProtectedDrives(k?.GetValue(SppValue) as string[]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>A política de grupo desliga a Restauração do Sistema (DisableSR/DisableConfig).</summary>
    public static bool DisabledByPolicy()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
            return k?.GetValue("DisableSR") is int sr && sr == 1 || k?.GetValue("DisableConfig") is int cfg && cfg == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestorePointInfoNative
    {
        public int EventType;
        public int RestorePtType;
        public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateMgrStatus
    {
        public int Status;
        public long SequenceNumber;
    }

#pragma warning disable SYSLIB1054 // struct com ByValTStr: DllImport clássico (o mesmo do RestorePoint do Broker.Host)
    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfoNative info, out StateMgrStatus status);
#pragma warning restore SYSLIB1054
}
