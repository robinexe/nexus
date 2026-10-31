using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.SelfTest;

/// <summary>Uma página da interface no autoteste (<c>Nexus.Desktop.exe --selftest-ui</c>).</summary>
public sealed record UiPageResult
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string Group { get; init; } = "";
    public string Type { get; init; } = "";
    public CheckStatus Status { get; init; }
    public string StatusText => SelfTestReport.StatusText(Status);
    /// <summary>Do clique até ficar ociosa (OnShownAsync terminou, sem "ocupado", fila da interface vazia).</summary>
    public double LoadSeconds { get; init; }
    public bool ReachedIdle { get; init; }
    /// <summary>Maior intervalo em que a thread da interface não respondeu enquanto a página carregava.</summary>
    public double LongestFreezeSeconds { get; init; }
    public bool HasTemplate { get; init; } = true;
    public IReadOnlyList<string> Exceptions { get; init; } = [];
    public int BindingErrors { get; init; }
    public IReadOnlyList<string> BindingSamples { get; init; } = [];
    /// <summary>Mensagem da página (ModulePage.Message) ao terminar, sanitizada.</summary>
    public string? Message { get; init; }
    /// <summary>Barra de status da janela ao terminar, sanitizada.</summary>
    public string? AppStatus { get; init; }
    /// <summary>PNG relativo à pasta do JSON (ex.: "telas/03-processador.png").</summary>
    public string? Screenshot { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record UiSelfTestReport
{
    public DateTimeOffset StartedAt { get; init; }
    public double Seconds { get; init; }
    public SelfTestEnvironment? Environment { get; init; }
    /// <summary>Leitura do PC antes da navegação (a mesma que a janela faz ao abrir).</summary>
    public double InitialLoadSeconds { get; init; }
    public bool InitialLoadCompleted { get; init; }
    public string? InitialStatus { get; init; }
    public IReadOnlyList<string> StartupExceptions { get; init; } = [];
    public int StartupBindingErrors { get; init; }
    /// <summary>Pedidos de aplicar/desfazer/simular que o modo autoteste recusou (nenhum chegou ao aplicador).</summary>
    public int RefusedWrites { get; init; }
    public bool Aborted { get; init; }
    public string? AbortReason { get; init; }
    public IReadOnlyList<UiPageResult> Pages { get; init; } = [];

    public int Failures => Pages.Count(p => p.Status == CheckStatus.Failure) + (StartupExceptions.Count > 0 || Aborted || !InitialLoadCompleted ? 1 : 0);
    public int Warnings => Pages.Count(p => p.Status == CheckStatus.Warning);
    public int ExitCode => Failures > 0 ? 1 : 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static UiSelfTestReport? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<UiSelfTestReport>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Classificação de uma página: FALHA = exceção, sem DataTemplate ou interface parada ≥ 10 s; AVISO = erro de binding, parada ≥ 2 s ou não ficou ociosa no tempo limite.</summary>
    public static CheckStatus Classify(int exceptions, bool hasTemplate, double longestFreeze, int bindingErrors, bool reachedIdle)
    {
        if (exceptions > 0 || !hasTemplate || longestFreeze >= 10) return CheckStatus.Failure;
        if (bindingErrors > 0 || longestFreeze >= 2 || !reachedIdle) return CheckStatus.Warning;
        return CheckStatus.Ok;
    }
}
