namespace Nexus.Security;

/// <summary>Estado do motor de varredura de um produto registrado na Central de Segurança.</summary>
public enum ScannerState { Off, On, Snoozed, Expired, Unknown }

/// <summary>
/// Decodificação do campo <c>productState</c> de <c>root\SecurityCenter2</c> (<c>AntiVirusProduct</c>, <c>FirewallProduct</c>).
/// A Microsoft não documenta o formato publicamente; o layout abaixo é o usado pela comunidade e confere com os valores
/// conhecidos (Defender ligado = 0x061100/397568; desligado por outro antivírus = 0x060100/393472; terceiro ligado e
/// atualizado = 0x041000/266240). Por isso a interface mostra também o valor bruto e diz "não documentado".
/// <list type="bullet">
/// <item>bits 16–23: tipos de proteção do produto (1 firewall, 2 atualização automática, 4 antivírus, 8 antispyware,
/// 16 configurações de internet, 32 UAC, 64 serviço).</item>
/// <item>bits 12–15: estado do motor (0 desligado, 1 ligado, 2 adiado, 3 expirado).</item>
/// <item>bits 8–11: 0 = produto de terceiros, 1 = do próprio Windows (heurística).</item>
/// <item>bits 0–7: 0x00 assinaturas em dia, 0x10 desatualizadas.</item>
/// </list>
/// </summary>
public sealed record ProductState(uint Raw, ScannerState Scanner, bool SignaturesUpToDate, bool MicrosoftOwned, int ProviderFlags)
{
    public static ProductState Decode(uint raw)
    {
        var scanner = ((raw >> 12) & 0xF) switch
        {
            0 => ScannerState.Off,
            1 => ScannerState.On,
            2 => ScannerState.Snoozed,
            3 => ScannerState.Expired,
            _ => ScannerState.Unknown,
        };
        var sig = raw & 0xFF;
        return new ProductState(raw, scanner, sig == 0x00, ((raw >> 8) & 0xF) == 1, (int)((raw >> 16) & 0xFF));
    }

    public bool IsAntivirus => (ProviderFlags & 4) != 0;
    public bool IsFirewall => (ProviderFlags & 1) != 0;

    public string Describe() =>
        $"{Scanner switch { ScannerState.On => "ligado", ScannerState.Off => "desligado", ScannerState.Snoozed => "adiado", ScannerState.Expired => "expirado", _ => "desconhecido" }}" +
        $", assinaturas {(SignaturesUpToDate ? "em dia" : "desatualizadas")} (0x{Raw:X6})";
}
