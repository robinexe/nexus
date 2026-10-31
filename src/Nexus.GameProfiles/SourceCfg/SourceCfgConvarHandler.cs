using Nexus.Core;

namespace Nexus.GameProfiles.SourceCfg;

/// <summary>
/// Conjunto de convars válidos da versão instalada, obtido de um dump do comando `cvarlist`
/// (ex.: console com -condebug grava console.log). Sem dump, nada é escrito: lista de
/// comandos copiada da internet não é aceita como validação.
/// </summary>
public sealed class CvarCatalog
{
    private readonly HashSet<string> _names;
    public string SourceDescription { get; }

    private CvarCatalog(HashSet<string> names, string source)
    {
        _names = names;
        SourceDescription = source;
    }

    public bool Contains(string name) => _names.Contains(name);
    public int Count => _names.Count;

    /// <summary>
    /// Formato tolerante: cada linha relevante começa com o nome seguido de espaço e ':' (ex.: "fps_max : 400 : ...").
    /// Linhas sem esse padrão são ignoradas. O formato exato deve ser confirmado com um dump real do CS2.
    /// </summary>
    public static CvarCatalog FromCvarListDump(string text, string sourceDescription)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(" :", StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            if (name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')) names.Add(name);
        }
        return new CvarCatalog(names, sourceDescription);
    }
}

/// <summary>
/// Handler "SourceCfgConvar". Alvo: root (diretório cfg do jogo detectado), file (relativo à raiz), convar, tag.
/// Desejado: Type "Convar", Value texto. Ausente = convar não atribuído no arquivo.
/// </summary>
public sealed class SourceCfgConvarHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "SourceCfgConvar";
    private readonly Func<CvarCatalog?> _cvars;

    public SourceCfgConvarHandler(Func<CvarCatalog?> cvarCatalogProvider) => _cvars = cvarCatalogProvider;

    public string HandlerKind => Kind;

    private static string PathOf(ActionInstance a) =>
        SafeFiles.EnsureUnderRoot(a.Target["root"], Path.Combine(a.Target["root"], a.Target["file"]));

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        foreach (var k in new[] { "root", "file", "convar" })
            if (!action.Target.ContainsKey(k)) return Applicability.Blocked($"Alvo incompleto: falta '{k}'.");
        if (action.Desired.Exists && (action.Desired.Type != "Convar" || string.IsNullOrEmpty(action.Desired.Value)))
            return Applicability.Blocked("Desejado deve ser Type 'Convar' com valor não vazio.");
        if (!action.Target["file"].EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            return Applicability.Blocked("Somente arquivos .cfg.");

        var cvars = _cvars();
        if (cvars is null) return Applicability.Blocked("Sem dump de cvarlist da versão instalada; validação impossível.");
        if (!cvars.Contains(action.Target["convar"]))
            return Applicability.NotApplicable($"'{action.Target["convar"]}' não existe na versão instalada ({cvars.SourceDescription}).");

        string path;
        try { path = PathOf(action); }
        catch (UnauthorizedAccessException ex) { return Applicability.Blocked(ex.Message); }

        SourceCfgDocument doc;
        try { doc = SourceCfgDocument.Parse(SafeFiles.Read(path).Text); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException)
        {
            return Applicability.Blocked($"Codificação do arquivo não suportada; edição recusada para não corromper o conteúdo ({ex.Message}).");
        }
        if (doc.AppearsInOpaqueLine(action.Target["convar"]))
            return Applicability.Blocked("Convar aparece em linha composta/alias; edição manual necessária para não alterar outras preferências.");
        return Applicability.Ok;
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        foreach (var k in new[] { "root", "file", "convar" })
            if (!action.Target.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return $"Alvo incompleto: falta '{k}'.";
        if (!action.Target["file"].EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) return "Somente arquivos .cfg.";
        try { _ = PathOf(action); }
        catch (UnauthorizedAccessException ex) { return ex.Message; }
        if (original.Exists && (original.Type != "Convar" || string.IsNullOrEmpty(original.Value))) return "Valor original inválido.";
        return null;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        var read = SafeFiles.Read(PathOf(action));
        var doc = SourceCfgDocument.Parse(read.Text);
        var detail = new Dictionary<string, string> { ["sha256"] = read.Sha256, ["fileExisted"] = read.Exists ? "true" : "false" };
        var v = doc.GetEffective(action.Target["convar"]);
        return v is null ? StateSnapshot.Absent(detail) : StateSnapshot.Of("Convar", v, ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action) => Edit(action, doc =>
    {
        if (action.Desired.Exists) doc.Set(action.Target["convar"], action.Desired.Value!, action.Definition.Id);
        else doc.RemoveTagged(action.Target["convar"]);
    });

    public void Restore(ActionInstance action, StateSnapshot original) => Edit(action, doc =>
    {
        if (original.Exists) doc.Set(action.Target["convar"], original.Value!, action.Definition.Id);
        else doc.RemoveTagged(action.Target["convar"]);
    });

    private static void Edit(ActionInstance action, Action<SourceCfgDocument> change)
    {
        var path = PathOf(action);
        var read = SafeFiles.Read(path);
        var doc = SourceCfgDocument.Parse(read.Text);
        change(doc);
        SafeFiles.WriteAtomic(path, doc.ToString(), read.Encoding, read.Sha256);
    }
}
