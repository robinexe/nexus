using System.Globalization;
using System.Text.Json;

namespace Nexus.Updates;

/// <summary>Instalador descrito no manifesto: endereço (https, GitHub), SHA-256 (64 hex, minúsculo) e tamanho exato em bytes.</summary>
public sealed record UpdateInstaller(Uri Url, string Sha256, long Size)
{
    /// <summary>Nome do arquivo para gravar (do fim do endereço, se for seguro; senão um nome fixo).</summary>
    public string FileName(SemVersion version)
    {
        var last = Uri.UnescapeDataString(Url.Segments.LastOrDefault() ?? "");
        return UpdatePolicy.IsSafeFileName(last) ? last : $"NEXUS-{version}-instalador.exe";
    }
}

/// <summary>
/// Manifesto <c>nexus-update.json</c> (schema <c>nexus.update/1</c>), publicado como asset do último Release de
/// robinexe/nexus-releases junto com <c>nexus-update.json.sig</c>. Só é lido DEPOIS de a assinatura conferir.
/// </summary>
public sealed record UpdateManifest(
    SemVersion Version,
    string Channel,
    DateTimeOffset PublishedAt,
    string Notes,
    UpdateInstaller Installer,
    SemVersion? MinimumVersion)
{
    public const string Schema = "nexus.update/1";
    public const string StableChannel = "stable";
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxNotesLength = 2000;

    /// <summary>
    /// Interpreta e valida os bytes EXATOS do manifesto (os mesmos que foram assinados). Recusa schema desconhecido, canal
    /// diferente de stable, versão inválida, endereço fora da lista de hosts, SHA-256 malformado e tamanho fora do limite.
    /// </summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxManifestBytes) throw new UpdateException("Manifesto vazio ou grande demais.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException ex) { throw new UpdateException($"Manifesto não é JSON válido: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new UpdateException("Manifesto não é um objeto JSON.");
            var schema = Text(root, "schema");
            if (schema != Schema) throw new UpdateException($"Formato de manifesto desconhecido: '{schema}'. Atualize o NEXUS à mão.");
            if (!SemVersion.TryParse(Text(root, "version"), out var version)) throw new UpdateException("Versão inválida no manifesto.");
            var channel = Text(root, "channel");
            if (channel != StableChannel) throw new UpdateException($"Canal desconhecido: '{channel}'.");
            if (!DateTimeOffset.TryParse(Text(root, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var published))
                throw new UpdateException("Data de publicação inválida no manifesto.");
            var notes = OptionalText(root, "notes") ?? "";
            if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];
            SemVersion? minimum = null;
            if (OptionalText(root, "minimumVersion") is { } min && !SemVersion.TryParse(min, out minimum))
                throw new UpdateException("minimumVersion inválida no manifesto.");

            if (!root.TryGetProperty("installer", out var inst) || inst.ValueKind != JsonValueKind.Object)
                throw new UpdateException("Manifesto sem 'installer'.");
            if (!Uri.TryCreate(Text(inst, "url"), UriKind.Absolute, out var url) || !UpdatePolicy.IsAllowedDownload(url))
                throw new UpdateException("Endereço do instalador fora dos permitidos (só https no GitHub).");
            var sha = Text(inst, "sha256").ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new UpdateException("SHA-256 do instalador malformado.");
            if (!inst.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.Number || !sizeEl.TryGetInt64(out var size)
                || size <= 0 || size > UpdatePolicy.MaxInstallerBytes)
                throw new UpdateException($"Tamanho do instalador inválido (máximo {UpdatePolicy.MaxInstallerBytes / (1024 * 1024)} MB).");

            return new UpdateManifest(version, channel, published, notes.Trim(), new UpdateInstaller(url, sha, size), minimum);
        }
    }

    private static string Text(JsonElement obj, string name) =>
        OptionalText(obj, name) ?? throw new UpdateException($"Manifesto sem '{name}'.");

    private static string? OptionalText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

/// <summary>Falha de atualização com mensagem para o usuário (pt-BR). Nada que falhou uma conferência é executado.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException() { }
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception inner) : base(message, inner) { }
}
