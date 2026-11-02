using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Automation;

/// <summary>
/// Preferências da INTERFACE (e da notificação das tarefas agendadas), em <c>%LocalAppData%\NEXUS\settings.json</c>.
/// Nada aqui muda o sistema: agendamentos e "iniciar com o Windows" são ações do catálogo pelo aplicador, com diário e desfazer.
/// Arquivo ausente, corrompido ou com valores estranhos → padrões (e o arquivo ruim é preservado como .bad para diagnóstico).
/// </summary>
public sealed record NexusSettings
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    /// <summary>Notificação do Windows ao terminar uma verificação/limpeza agendada.</summary>
    public bool Notifications { get; init; } = true;
    /// <summary>Notificar também quando a tarefa foi pulada por uma condição (bateria/jogo).</summary>
    public bool NotifySkipped { get; init; }
    /// <summary>Ícone na bandeja do sistema enquanto o NEXUS está aberto.</summary>
    public bool TrayIcon { get; init; } = true;
    /// <summary>Minimizar esconde a janela na bandeja.</summary>
    public bool MinimizeToTray { get; init; }
    /// <summary>Fechar (X) esconde na bandeja em vez de sair. "Sair" no menu da bandeja sai de verdade.</summary>
    public bool CloseToTray { get; init; }
    /// <summary>Atualizar automaticamente (procura, baixa e confere sozinho; instala com um clique ou ao fechar). Padrão: ligado.</summary>
    public bool AutoUpdate { get; init; } = true;
    /// <summary>Última vez que procurou atualização (com resposta do servidor).</summary>
    public DateTimeOffset? LastUpdateCheck { get; init; }
    /// <summary>Versão que já tentou instalar ao fechar (se o aviso do Windows foi recusado, não insiste a cada saída).</summary>
    public string? UpdateExitAttempted { get; init; }
    /// <summary>Idioma da interface: null = igual ao Windows; "pt-BR", "en" ou "es" = fixo. Vale a partir do próximo início.</summary>
    public string? Language { get; init; }

    public static NexusSettings Defaults { get; } = new();

    /// <summary>%LocalAppData%\NEXUS\settings.json.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>Lê as preferências. Nunca lança: problema = padrões, com <paramref name="problem"/> explicando.</summary>
    public static NexusSettings Load(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path)) return Defaults;
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024) { problem = "settings.json grande demais; usando os padrões."; Preserve(path); return Defaults; }
            var text = File.ReadAllText(path, Encoding.UTF8);
            var s = JsonSerializer.Deserialize<NexusSettings>(text, Json);
            if (s is null) { problem = "settings.json vazio; usando os padrões."; return Defaults; }
            if (s.Format is < 1 or > CurrentFormat) { problem = "settings.json de outra versão; usando os padrões."; return Defaults; }
            return s;
        }
        catch (JsonException) { problem = "settings.json corrompido; usando os padrões."; Preserve(path); return Defaults; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            problem = $"Não foi possível ler settings.json ({ex.Message}); usando os padrões.";
            return Defaults;
        }
    }

    public static NexusSettings Load(string path) => Load(path, out _);

    /// <summary>Grava de forma atômica (arquivo temporário + troca). Retorna o problema, ou null.</summary>
    public string? Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this with { Format = CurrentFormat }, Json), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Não foi possível gravar as preferências: {ex.Message}";
        }
    }

    private static void Preserve(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
