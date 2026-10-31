using System.Globalization;

namespace Nexus.Lab.Gpu;

/// <summary>Versão do driver como o Windows registra (ex.: "32.0.15.6636") e como o fabricante divulga (ex.: "566.36").</summary>
public sealed record DriverVersionInfo(string WindowsVersion, string? VendorVersion, string Explanation);

public static class DriverInfo
{
    /// <summary>Páginas OFICIAIS de download (o NEXUS só abre o link; nunca baixa nada sozinho).</summary>
    public static string? OfficialDownloadPage(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "https://www.nvidia.com/pt-br/drivers/",
        GpuVendor.Amd => "https://www.amd.com/pt/support/download/drivers.html",
        GpuVendor.Intel => "https://www.intel.com.br/content/www/br/pt/support/detect.html",
        _ => null,
    };

    /// <summary>
    /// Decodifica a versão do Windows (a.b.c.d) para a versão do fabricante.
    /// <list type="bullet">
    /// <item>NVIDIA: os 5 últimos dígitos de c+d viram "xxx.yy" (32.0.15.6636 → 566.36), regra documentada pela NVIDIA nas notas de versão.</item>
    /// <item>Intel: a versão pública é "c.d" (31.0.101.5590 → 101.5590).</item>
    /// <item>AMD: o número do Windows não mapeia para o nome do Adrenalin (ex.: 24.12.1) sem tabela da AMD; mostramos como está.</item>
    /// </list>
    /// </summary>
    public static DriverVersionInfo Decode(GpuVendor vendor, string? windowsVersion)
    {
        var raw = (windowsVersion ?? "").Trim();
        var parts = raw.Split('.');
        var numeric = parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
        if (!numeric) return new(raw, null, raw.Length == 0 ? "Versão do driver não informada." : "Formato de versão inesperado; mostrado como o Windows registra.");
        switch (vendor)
        {
            case GpuVendor.Nvidia:
            {
                var digits = parts[2] + parts[3].PadLeft(4, '0');
                if (digits.Length < 5) break;
                var last5 = digits[^5..];
                var v = $"{int.Parse(last5[..3], CultureInfo.InvariantCulture)}.{last5[3..]}";
                return new(raw, v, $"NVIDIA {v}: os 5 últimos dígitos de {parts[2]}.{parts[3]} (regra de numeração da NVIDIA).");
            }
            case GpuVendor.Intel:
                return new(raw, $"{parts[2]}.{parts[3]}", $"Intel {parts[2]}.{parts[3]}: a Intel divulga os dois últimos campos.");
            case GpuVendor.Amd:
                return new(raw, null, "A AMD não divulga a correspondência entre este número e o nome do Adrenalin (ex.: 24.12.1); confira no AMD Software.");
        }
        return new(raw, null, "Versão mostrada como o Windows registra.");
    }

    /// <summary>
    /// Data do driver como o registro guarda ("12-4-2024", "M-D-AAAA") ou ISO. Retorna null se não reconhecer.
    /// </summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        string[] formats = ["M-d-yyyy", "MM-dd-yyyy", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd"];
        if (DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) return DateOnly.FromDateTime(dt);
        return null;
    }

    /// <summary>Idade do driver em dias (nunca negativa).</summary>
    public static int AgeDays(DateOnly date, DateOnly today) => Math.Max(0, today.DayNumber - date.DayNumber);

    /// <summary>
    /// Classificação honesta da idade. Driver novo não "acelera" por si só; o ganho real vem de correções e perfis de jogos
    /// lançados depois da data. Em notebook, o driver da iGPU muitas vezes vem do fabricante do notebook e fica mais velho.
    /// </summary>
    public static (string Level, string Text) AgeVerdict(int days, bool integrated) => days switch
    {
        > 730 => ("bad", $"Driver de {days / 365} anos atrás: jogos lançados depois disso não têm perfil nem correções nele."),
        > 365 => (integrated ? "info" : "warn", integrated
            ? "Driver da integrada com mais de 1 ano. Em notebook é comum (vem do fabricante); atualize se tiver falhas."
            : "Driver com mais de 1 ano. Vale atualizar pelo site oficial: jogos recentes trazem perfis e correções."),
        > 180 => ("info", "Driver com mais de 6 meses. Atualize se for jogar algo lançado depois disso."),
        _ => ("good", "Driver recente."),
    };
}
