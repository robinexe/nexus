using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Broker;

/// <summary>Enum fechado. Não existe operação genérica ("executar comando", "escrever chave").</summary>
public enum BrokerOperation { Ping, Status, ReadState, Plan, Execute, EndSession, RevertRun, Recover }

/// <summary>
/// Pedido de ação: só o id versionado do catálogo do broker ("id@versão"); quando o catálogo define uma lista de valores
/// permitidos, a escolha dentro dela; e, quando o catálogo declara parâmetros do usuário (ex.: qual driver antigo remover),
/// os argumentos, cada um validado pela regra do catálogo. Não há campo de alvo livre: o cliente não nomeia chave, serviço ou
/// arquivo fora do que o catálogo permite.
/// </summary>
public sealed record ActionRequest([property: JsonRequired] string ActionId, string? Value = null, IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record BrokerRequest(
    [property: JsonRequired] int V,
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] BrokerOperation Op,
    IReadOnlyList<ActionRequest>? Actions = null,
    Guid? RunId = null,
    bool Force = false,
    string? Profile = null);

public sealed record BrokerError(string Code, string Detail);

public sealed record ActionResultDto(string ActionKey, string Outcome, string? Detail = null, string? Observed = null, string? Desired = null, bool Inferred = false);

public sealed record BrokerStatus(
    string BrokerVersion,
    string CatalogVersion,
    bool ExecutionAllowed,
    string ExecutionGateReason,
    string Coordination,
    Guid? ActiveSessionRunId = null);

public sealed record BrokerResponse(
    Guid Id,
    bool Ok,
    BrokerError? Error = null,
    BrokerStatus? Status = null,
    IReadOnlyList<ActionResultDto>? Results = null,
    Guid? RunId = null,
    bool? SessionActive = null,
    bool? FullyRestored = null,
    string? Message = null)
{
    public static BrokerResponse Fail(Guid id, string code, string detail) => new(id, false, new BrokerError(code, detail));
}

public sealed class BrokerProtocolException : Exception
{
    public BrokerProtocolException() { }
    public BrokerProtocolException(string message) : base(message) { }
    public BrokerProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Quadros: comprimento (uint32 little-endian) + JSON UTF-8. Máximo 64 KiB; maior é recusado sem ler o corpo.</summary>
public static class BrokerWire
{
    public const int ProtocolVersion = 1;
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>JSON estrito: enums só por nome, campos desconhecidos recusados, profundidade limitada.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new StrictEnumConverterFactory() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<T>(T message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxFrameBytes) throw new BrokerProtocolException($"Mensagem de {body.Length} bytes excede {MaxFrameBytes}.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static void Write<T>(Stream stream, T message)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = Encode(message);
        stream.Write(frame);
        stream.Flush();
    }

    /// <summary>Lê um quadro. Null = fim do fluxo antes de um novo quadro.</summary>
    public static byte[]? ReadFrame(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[4];
        var got = ReadExactly(stream, header);
        if (got == 0) return null;
        if (got < 4) throw new BrokerProtocolException("Cabeçalho de quadro incompleto.");
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len == 0 || len > MaxFrameBytes) throw new BrokerProtocolException($"Tamanho de quadro inválido: {len} (máximo {MaxFrameBytes}).");
        var body = new byte[len];
        if (ReadExactly(stream, body) != len) throw new BrokerProtocolException("Quadro truncado.");
        return body;
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = Encode(message);
        await stream.WriteAsync(frame, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Lê um quadro de forma assíncrona. Null = fim do fluxo antes de um novo quadro.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var got = await ReadExactlyAsync(stream, header, cancel).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new BrokerProtocolException("Cabeçalho de quadro incompleto.");
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len == 0 || len > MaxFrameBytes) throw new BrokerProtocolException($"Tamanho de quadro inválido: {len} (máximo {MaxFrameBytes}).");
        var body = new byte[len];
        if (await ReadExactlyAsync(stream, body, cancel).ConfigureAwait(false) != len) throw new BrokerProtocolException("Quadro truncado.");
        return body;
    }

    private static async Task<int> ReadExactlyAsync(Stream s, Memory<byte> buffer, CancellationToken cancel)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer[total..], cancel).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public static T Decode<T>(byte[] body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json) ?? throw new BrokerProtocolException("Mensagem vazia.");
        }
        catch (JsonException ex)
        {
            throw new BrokerProtocolException($"JSON inválido ou com campos não permitidos: {ex.Message}", ex);
        }
    }

    private static int ReadExactly(Stream s, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>
/// Enums só pelo nome exato (diferencia maiúsculas), um único valor, nada de números nem combinações "A, B".
/// O JsonStringEnumConverter padrão aceita "execute" e "Ping, Execute".
/// </summary>
public sealed class StrictEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StrictEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class StrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly Dictionary<string, T> ByName = Enum.GetValues<T>().ToDictionary(v => v.ToString(), StringComparer.Ordinal);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException($"{typeof(T).Name} deve ser texto.");
            var text = reader.GetString();
            return text is not null && ByName.TryGetValue(text, out var v) ? v : throw new JsonException($"Valor '{text}' não existe em {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteStringValue(value.ToString());
        }
    }
}
