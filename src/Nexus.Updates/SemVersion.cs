using System.Globalization;

namespace Nexus.Updates;

/// <summary>
/// Versão SemVer 2.0 (MAIOR.MENOR.CORREÇÃO[-pré][+build]). A comparação segue a especificação: pré-lançamento vem antes do
/// lançamento (0.9.0-ci.7 &lt; 0.9.0 &lt; 0.9.1), identificadores numéricos comparam como número e vêm antes dos alfanuméricos,
/// e o "+build" (hash do commit) não conta.
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, IReadOnlyList<string> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Identificadores do pré-lançamento ("ci", "7"); vazio = lançamento.</summary>
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Versão inválida: '{text}'.");

    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            var build = s[(plus + 1)..];
            if (!ValidIdentifiers(build, numericNoLeadingZero: false)) return false;
            s = s[..plus];
        }
        IReadOnlyList<string> pre = [];
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            var p = s[(dash + 1)..];
            if (!ValidIdentifiers(p, numericNoLeadingZero: true)) return false;
            pre = p.Split('.');
            s = s[..dash];
        }
        var core = s.Split('.');
        if (core.Length != 3) return false;
        if (!Number(core[0], out var major) || !Number(core[1], out var minor) || !Number(core[2], out var patch)) return false;
        version = new SemVersion(major, minor, patch, pre);
        return true;
    }

    private static bool Number(string s, out int value)
    {
        value = 0;
        if (s.Length == 0 || s.Length > 9 || (s.Length > 1 && s[0] == '0') || !s.All(char.IsAsciiDigit)) return false;
        value = int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool ValidIdentifiers(string s, bool numericNoLeadingZero)
    {
        if (s.Length == 0) return false;
        foreach (var id in s.Split('.'))
        {
            if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            if (numericNoLeadingZero && id.Length > 1 && id[0] == '0' && id.All(char.IsAsciiDigit)) return false;
        }
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            c = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (c != 0) return c;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    private static int CompareIdentifier(string a, string b)
    {
        var an = a.All(char.IsAsciiDigit);
        var bn = b.All(char.IsAsciiDigit);
        if (an && bn)
        {
            var len = a.Length.CompareTo(b.Length); // sem zeros à esquerda: mais dígitos = maior (sem limite de tamanho)
            return len != 0 ? len : string.CompareOrdinal(a, b);
        }
        if (an) return -1;
        if (bn) return 1;
        return string.CompareOrdinal(a, b);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (IsPrerelease ? "-" + string.Join('.', Prerelease) : "");

    public static bool operator <(SemVersion? a, SemVersion? b) => Compare(a, b) < 0;
    public static bool operator >(SemVersion? a, SemVersion? b) => Compare(a, b) > 0;
    public static bool operator <=(SemVersion? a, SemVersion? b) => Compare(a, b) <= 0;
    public static bool operator >=(SemVersion? a, SemVersion? b) => Compare(a, b) >= 0;
    public static bool operator ==(SemVersion? a, SemVersion? b) => Compare(a, b) == 0;
    public static bool operator !=(SemVersion? a, SemVersion? b) => Compare(a, b) != 0;

    private static int Compare(SemVersion? a, SemVersion? b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b);
}
