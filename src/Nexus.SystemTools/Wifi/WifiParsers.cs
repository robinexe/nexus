namespace Nexus.SystemTools.Wifi;

public enum WifiProfileScope { AllUsers, CurrentUser, GroupPolicy }

public sealed record WifiProfileInfo(string Name, string Interface, WifiProfileScope Scope)
{
    /// <summary>Perfis de política de grupo são gerenciados pela organização: o NEXUS nunca os remove.</summary>
    public bool Removable => Scope != WifiProfileScope.GroupPolicy;
}

public sealed record WifiListing(IReadOnlyList<WifiProfileInfo> Profiles, string? Note);

/// <summary>Leitura de <c>netsh wlan show profiles</c> em português e inglês (rótulos sem depender de acentos).</summary>
public static class WifiParsers
{
    public static WifiListing ParseNetshProfiles(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var list = new List<WifiProfileInfo>();
        string iface = "";
        var groupPolicy = false;
        string? note = null;
        foreach (var raw in Text.Lines(output))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0 || line.Trim().All(c => c == '-')) continue;
            var folded = Text.Fold(line);
            var indented = raw.Length > 0 && char.IsWhiteSpace(raw[0]);

            if (!indented)
            {
                if (folded.Contains("interface", StringComparison.Ordinal) && line.EndsWith(':'))
                {
                    var at = folded.LastIndexOf("interface", StringComparison.Ordinal) + "interface".Length;
                    iface = line[at..^1].Trim();
                    groupPolicy = false;
                }
                else if (folded.Contains("group policy", StringComparison.Ordinal) || folded.Contains("politica de grupo", StringComparison.Ordinal))
                    groupPolicy = true;
                else if (folded.Contains("user profiles", StringComparison.Ordinal) || folded.Contains("perfis de usuario", StringComparison.Ordinal))
                    groupPolicy = false;
                else if (folded.Contains("wlansvc", StringComparison.Ordinal) || folded.Contains("no wireless interface", StringComparison.Ordinal) ||
                         folded.Contains("nenhuma interface", StringComparison.Ordinal) || folded.Contains("sem fio", StringComparison.Ordinal))
                    note = line.Trim();
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                // Perfis de política de grupo aparecem só com o nome; "<None>"/"<Nenhum>" = lista vazia.
                var bare = line.Trim();
                if (groupPolicy && !bare.StartsWith('<')) list.Add(new WifiProfileInfo(bare, iface, WifiProfileScope.GroupPolicy));
                continue;
            }
            var label = Text.Fold(line[..colon]);
            var name = line[(colon + 1)..].Trim();
            if (name.Length == 0) continue;
            WifiProfileScope scope;
            if (groupPolicy) scope = WifiProfileScope.GroupPolicy;
            else if (label.Contains("all user", StringComparison.Ordinal) || label.Contains("todos", StringComparison.Ordinal)) scope = WifiProfileScope.AllUsers;
            else if (label.Contains("current user", StringComparison.Ordinal) || label.Contains("atual", StringComparison.Ordinal)) scope = WifiProfileScope.CurrentUser;
            else continue;
            list.Add(new WifiProfileInfo(name, iface, scope));
        }
        return new WifiListing(list, list.Count == 0 ? note : null);
    }

    /// <summary>SYSTEMTIME de 16 bytes (valor DateLastConnected do NetworkList), ou null se inválido.</summary>
    public static DateTime? SystemTime(byte[]? data)
    {
        if (data is not { Length: >= 16 }) return null;
        int U(int i) => data[i] | (data[i + 1] << 8);
        try { return new DateTime(U(0), U(2), U(6), U(8), U(10), U(12), DateTimeKind.Local); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
