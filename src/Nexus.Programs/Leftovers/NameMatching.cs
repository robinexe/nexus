using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nexus.Programs.Inventory;

namespace Nexus.Programs.Leftovers;

/// <summary>Normalização de nomes de programa, editor e pasta para comparação (sem versão, arquitetura, sufixo jurídico).</summary>
public static partial class NameMatching
{
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\bv?\d+(?:[._]\d+)+[a-z]?\b", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();

    [GeneratedRegex(@"\b(?:x64|x86|amd64|arm64|win64|win32|64[- ]?bits?|32[- ]?bits?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Architecture();

    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.Ordinal)
    {
        "inc", "llc", "ltd", "limited", "corporation", "corp", "co", "company", "gmbh", "sa", "s a", "srl", "ag", "bv", "oy", "ab", "pty",
        "plc", "kg", "spa", "sas", "ltda", "me", "eireli", "the", "software", "technologies", "technology", "games", "studios", "studio", "entertainment",
    };

    /// <summary>Minúsculas, sem acentos, sem parênteses, versão e arquitetura; só letras, dígitos, '+' e '#'.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = RemoveDiacritics(text.ToLowerInvariant());
        s = Bracketed().Replace(s, " ");
        s = VersionNumber().Replace(s, " ");
        s = Architecture().Replace(s, " ");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '+' or '#' ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Editor sem "Inc.", "Corporation", "GmbH"...: "Valve Corporation" → "valve".</summary>
    public static string NormalizePublisher(string? publisher)
    {
        var words = Normalize(publisher).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && (CompanySuffixes.Contains(words[^1]) || words[^1].Length == 1)) words.RemoveAt(words.Count - 1); // "S.A." vira "s a"
        return string.Join(' ', words);
    }

    public static string Compact(string normalized) => normalized.Replace(" ", "", StringComparison.Ordinal);

    /// <summary>Formas aceitas do nome do programa: inteiro, sem espaços e sem o nome do editor na frente ("Mozilla Firefox" → "firefox").</summary>
    public static IReadOnlyList<string> ProgramForms(string name, string? publisher)
    {
        var n = Normalize(name);
        var forms = new List<string>();
        void Add(string f) { if (f.Length >= 3 && !forms.Contains(f)) forms.Add(f); }
        Add(n);
        Add(Compact(n));
        var pub = NormalizePublisher(publisher);
        if (pub.Length > 0 && n.StartsWith(pub + " ", StringComparison.Ordinal))
        {
            var rest = n[(pub.Length + 1)..];
            Add(rest);
            Add(Compact(rest));
        }
        return forms;
    }

    public static bool MatchesProgram(string folderOrKey, IReadOnlyList<string> programForms)
    {
        var n = Normalize(folderOrKey);
        if (n.Length < 3) return false;
        var c = Compact(n);
        return programForms.Any(f => f == n || f == c);
    }

    public static bool MatchesPublisher(string folderOrKey, string? publisher)
    {
        var pub = NormalizePublisher(publisher);
        if (pub.Length < 3) return false;
        var n = NormalizePublisher(folderOrKey);
        return n == pub || Compact(n) == Compact(pub);
    }

    /// <summary>
    /// O nome é de algum programa AINDA instalado (nome do programa ou do editor): a pasta/chave pode ser compartilhada e nunca
    /// é oferecida nem removida.
    /// </summary>
    public static bool BelongsToInstalled(string folderOrKey, IEnumerable<InstalledProgram> installed) =>
        installed.Any(p => MatchesProgram(folderOrKey, ProgramForms(p.Name, p.Publisher)) || MatchesPublisher(folderOrKey, p.Publisher));

    /// <summary>O nome da pasta/chave CONTÉM o nome do programa (≥ 5 letras), ex.: "FooEditorUpdater". Só para achados de baixa confiança.</summary>
    public static bool ContainsProgram(string folderOrKey, IReadOnlyList<string> programForms)
    {
        var c = Compact(Normalize(folderOrKey));
        return programForms.Any(f => !f.Contains(' ', StringComparison.Ordinal) && f.Length >= 5 && c.Contains(f, StringComparison.Ordinal));
    }

    private static string RemoveDiacritics(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
