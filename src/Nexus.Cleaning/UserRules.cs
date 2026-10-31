using System.Text;

namespace Nexus.Cleaning;

/// <summary>Resultado da leitura de UM arquivo de regras do usuário (para a página mostrar erro por arquivo).</summary>
/// <param name="Error">Null = arquivo válido. Arquivo com erro não contribui com nenhuma regra (nem as válidas dele).</param>
public sealed record UserRuleFile(string Path, string Name, bool Enabled, IReadOnlyList<string> RuleIds, string? Error);

/// <summary>Regras do usuário carregadas de uma pasta, mais o relatório por arquivo.</summary>
public sealed record UserRuleLoad(string Folder, CleaningRuleSet Rules, IReadOnlyList<UserRuleFile> Files, string? FolderProblem)
{
    public static UserRuleLoad None(string folder, string? problem = null) => new(folder, CleaningRuleSet.Empty, [], problem);
}

/// <summary>
/// Regras de limpeza escritas pelo usuário, como no Kudu: <c>%AppData%\NEXUS\rules\*.json</c>, mesmo formato das regras
/// embutidas (<see cref="CleaningRuleSet.ParseUserRules"/>). Só leitura: quem cria a pasta e o exemplo é a interface
/// (<see cref="ExampleJson"/>), e quem aplica é o aplicador da própria conta (nunca o elevado nem o serviço).
/// </summary>
public static class UserCleaningRules
{
    public const string ExampleFileName = "exemplo-desativado.json";
    public const int MaxFiles = 32;

    /// <summary><c>%AppData%\NEXUS\rules</c> das pastas dadas (null se %AppData% não estiver disponível).</summary>
    public static string? FolderFor(CleaningFolders folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return string.IsNullOrWhiteSpace(folders.AppData) || !Path.IsPathFullyQualified(folders.AppData) ? null : Path.Combine(folders.AppData, "NEXUS", "rules");
    }

    /// <summary>
    /// Lê e valida cada <c>*.json</c> da pasta (não recursivo). Pasta ausente = nenhuma regra. Recusados: pasta ou arquivo que é
    /// link/junção, arquivo grande demais, JSON quebrado, regra inválida e id repetido entre arquivos (o segundo arquivo leva o erro).
    /// </summary>
    public static UserRuleLoad Load(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return UserRuleLoad.None("", "Pasta de regras indisponível.");
        DirectoryInfo dir;
        try
        {
            dir = new DirectoryInfo(folder);
            if (!dir.Exists) return UserRuleLoad.None(folder);
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return UserRuleLoad.None(folder, "A pasta de regras é um link ou junção; por segurança, nenhuma regra foi lida.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return UserRuleLoad.None(folder, ex.Message);
        }

        List<FileInfo> files;
        try
        {
            files = dir.EnumerateFiles("*.json", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return UserRuleLoad.None(folder, ex.Message);
        }

        var report = new List<UserRuleFile>();
        var rules = new List<CleaningRule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (report.Count >= MaxFiles)
            {
                report.Add(new UserRuleFile(f.FullName, f.Name, false, [], $"Arquivos demais na pasta (máximo {MaxFiles}); este foi ignorado."));
                continue;
            }
            try
            {
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("O arquivo é um link; ignorado.");
                if (f.Length > CleaningRuleSet.MaxUserRuleFileBytes) throw new InvalidDataException("Arquivo grande demais (máximo 256 KB).");
                string text;
                using (var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(s, new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true))
                {
                    var buffer = new char[CleaningRuleSet.MaxUserRuleFileBytes + 1];
                    var n = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (n > CleaningRuleSet.MaxUserRuleFileBytes) throw new InvalidDataException("Arquivo grande demais (máximo 256 KB).");
                    text = new string(buffer, 0, n);
                }
                var set = CleaningRuleSet.ParseUserRules(text, out var enabled);
                var dup = set.Rules.FirstOrDefault(r => ids.Contains(r.Id));
                if (dup is not null) throw new InvalidDataException($"{dup.Id}: id já usado em outro arquivo de regras.");
                if (rules.Count + set.Rules.Count > CleaningRuleSet.MaxUserRules)
                    throw new InvalidDataException($"Regras do usuário demais no total (máximo {CleaningRuleSet.MaxUserRules}).");
                foreach (var r in set.Rules) ids.Add(r.Id);
                rules.AddRange(set.Rules);
                report.Add(new UserRuleFile(f.FullName, f.Name, enabled, set.Rules.Select(r => r.Id).ToList(), null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                                           or DecoderFallbackException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                report.Add(new UserRuleFile(f.FullName, f.Name, false, [], Describe(ex)));
            }
        }
        return new UserRuleLoad(folder, CleaningRuleSet.From(rules), report, null);
    }

    private static string Describe(Exception ex) => ex switch
    {
        System.Text.Json.JsonException j => $"JSON inválido{(j.LineNumber is { } l ? $" (linha {l + 1})" : "")}: confira vírgulas, aspas e chaves.",
        DecoderFallbackException => "O arquivo não está em UTF-8.",
        KeyNotFoundException => "Falta um campo obrigatório (\"rules\").",
        InvalidOperationException => "Um campo tem o tipo errado (ex.: texto onde deveria ser número ou lista).",
        FormatException => "Um número está num formato inválido.",
        _ => ex.Message.Length <= 300 ? ex.Message : ex.Message[..300],
    };

    /// <summary>
    /// Exemplo comentado que a interface copia para a pasta na primeira vez. Vem DESATIVADO (<c>"enabled": false</c>): para usar,
    /// troque para true e ajuste a pasta. Ver docs/modules/cleaning-rules-guide.md.
    /// </summary>
    public const string ExampleJson = """
        // Exemplo de regra de limpeza do NEXUS (desativado).
        // Guia completo: docs/modules/cleaning-rules-guide.md (vem junto com o NEXUS).
        //
        // Como usar:
        //   1. Copie este arquivo com outro nome terminado em .json (ex.: meu-app.json).
        //   2. Troque "enabled" para true e ajuste a regra para a pasta do seu programa.
        //   3. Na página Limpeza > Regras, clique em "Recarregar". Erros aparecem por arquivo.
        //
        // Regras do usuário: só pastas do seu perfil (%LocalAppData%, %AppData%, %LocalAppDataLow%, %UserTemp%, %UserProfile%),
        // sempre com uma pasta com nome logo depois do placeholder. Tudo vai para a quarentena e pode voltar.
        {
          "enabled": false,
          "rules": [
            {
              "id": "meu-app-cache",
              "title": "Meu App: cache",
              "category": "Apps",
              "scope": "User",
              "mode": "Quarantine",
              "roots": [ "%LocalAppData%\\MeuApp\\Cache" ],
              "patterns": [ "*" ],
              "recursive": true,
              "minAgeHours": 24,
              "exclude": [ "*.config" ],
              "closeApps": [ "MeuApp" ],
              "explanation": "Arquivos temporários que o Meu App recria sozinho.",
              "expectedGain": "Espaço em disco.",
              "evidence": "Documentação do Meu App: a pasta Cache pode ser apagada com o programa fechado."
            }
          ]
        }
        """;
}
