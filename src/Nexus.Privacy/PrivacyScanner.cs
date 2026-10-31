using System.Diagnostics;
using System.Text;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Privacy;

public enum PrivacyItemStatus { Protected, Partial, Exposed, NotApplicable }

/// <summary>Estado lido de uma ação do item.</summary>
public sealed record PrivacyActionState(CatalogEntry Entry, bool Applicable, bool Protected, string Current, string Desired, string? Error)
{
    public string Key => Entry.Definition.Key;
}

/// <summary>Estado lido de um item da página.</summary>
public sealed record PrivacyItemState(PrivacyItem Item, PrivacyItemStatus Status, IReadOnlyList<PrivacyActionState> Actions)
{
    /// <summary>Ações que faltam aplicar (aplicáveis a este Windows e ainda não protegidas).</summary>
    public IEnumerable<PrivacyActionState> Pending => Actions.Where(a => a.Applicable && !a.Protected);
}

/// <summary>Placar: itens protegidos sobre itens que se aplicam a este Windows.</summary>
public sealed record PrivacyScore(int Protected, int Applicable, int Partial)
{
    public double Fraction => Applicable == 0 ? 0 : (double)Protected / Applicable;
    public override string ToString() => $"{Protected}/{Applicable}";
}

/// <summary>
/// Leitura do estado de cada item (somente leitura: o backend é embrulhado em <see cref="ReadOnlyRegistryBackend"/>).
/// Usa os mesmos handlers do aplicador para detectar, então "protegido" aqui significa exatamente o que o aplicador veria.
/// </summary>
public sealed class PrivacyScanner
{
    private readonly RegistryValueHandler _registry;
    private readonly PrivacyPolicyValueHandler _policy;
    private readonly ReadOnlyRegistryBackend _backend;

    public PrivacyScanner(IRegistryBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = new ReadOnlyRegistryBackend(backend);
        _registry = new RegistryValueHandler(_backend);
        _policy = new PrivacyPolicyValueHandler(_backend);
    }

    /// <param name="windowsBuild">Build do Windows (0 = desconhecido: tudo é considerado aplicável).</param>
    public IReadOnlyList<PrivacyItemState> Scan(PrivacyCatalog catalog, int windowsBuild)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Items.Select(i => ScanItem(i, windowsBuild)).ToList();
    }

    public PrivacyItemState ScanItem(PrivacyItem item, int windowsBuild)
    {
        ArgumentNullException.ThrowIfNull(item);
        var states = item.Actions.Select(a => ScanAction(item, a, windowsBuild)).ToList();
        var applicable = states.Where(s => s.Applicable).ToList();
        var status = applicable.Count == 0 ? PrivacyItemStatus.NotApplicable
            : applicable.All(s => s.Protected) ? PrivacyItemStatus.Protected
            : applicable.Any(s => s.Protected) ? PrivacyItemStatus.Partial
            : PrivacyItemStatus.Exposed;
        return new PrivacyItemState(item, status, states);
    }

    public static bool IsApplicable(ActionDefinition d, int windowsBuild)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (windowsBuild <= 0) return true;
        return (d.MinBuild is not { } min || windowsBuild >= min) && (d.MaxBuild is not { } max || windowsBuild <= max);
    }

    private PrivacyActionState ScanAction(PrivacyItem item, CatalogEntry entry, int windowsBuild)
    {
        var applicable = IsApplicable(entry.Definition, windowsBuild);
        var instance = entry.TryInstantiate(new Dictionary<string, string>(), out var reason);
        if (instance is null) return new PrivacyActionState(entry, applicable, false, "?", "?", reason);
        try
        {
            var current = entry.Definition.HandlerKind switch
            {
                RegistryValueHandler.Kind => _registry.Detect(instance),
                PrivacyPolicyValueHandler.Kind => _policy.Detect(instance),
                _ => throw new NotSupportedException($"Handler '{entry.Definition.HandlerKind}' não é lido por esta página."),
            };
            var isProtected = current.SameValueAs(instance.Desired) ||
                              (current.Exists && item.ProtectedValues.TryGetValue(entry.Definition.Id, out var also) &&
                               also.Contains(current.Value ?? "", StringComparer.Ordinal) &&
                               string.Equals(current.Type, instance.Desired.Type, StringComparison.OrdinalIgnoreCase));
            return new PrivacyActionState(entry, applicable, isProtected, Describe(current), Describe(instance.Desired), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PrivacyActionState(entry, applicable, false, "não lido", Describe(instance.Desired), ex.Message);
        }
    }

    private static string Describe(StateSnapshot s) => s.Exists ? $"{s.Value} ({s.Type})" : "ausente (padrão do Windows)";

    /// <summary>Estado geral de uma permissão de app (ConsentStore): "Allow", "Deny" ou null se não lido.</summary>
    public string? ReadConsent(string store)
    {
        if (string.IsNullOrWhiteSpace(store) || store.Any(c => !char.IsAsciiLetterOrDigit(c))) return null;
        try
        {
            var r = _backend.Read(new RegistryTarget(RegHive.HKCU, $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{store}", "Value", RegView.Default));
            return r.ValueExists ? r.Value : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}

/// <summary>Planejamento puro: placar, perfis e pedidos ao aplicador.</summary>
public static class PrivacyPlanner
{
    /// <summary>Limite do aplicador por pedido (<c>BrokerService.MaxActionsPerRequest</c>).</summary>
    public const int MaxActionsPerRequest = 64;

    public static PrivacyScore Score(IEnumerable<PrivacyItemState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var list = states.Where(s => s.Status != PrivacyItemStatus.NotApplicable).ToList();
        return new PrivacyScore(list.Count(s => s.Status == PrivacyItemStatus.Protected), list.Count, list.Count(s => s.Status == PrivacyItemStatus.Partial));
    }

    /// <summary>Itens do perfil que ainda não estão protegidos (e se aplicam a este Windows).</summary>
    public static IReadOnlyList<PrivacyItemState> MissingFor(IEnumerable<PrivacyItemState> states, PrivacyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(states);
        return states.Where(s => s.Item.InProfile(profile) && s.Status is PrivacyItemStatus.Exposed or PrivacyItemStatus.Partial).ToList();
    }

    /// <summary>Chaves id@versão a pedir, sem repetição, na ordem do catálogo.</summary>
    public static IReadOnlyList<string> ActionKeys(IEnumerable<PrivacyItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.SelectMany(i => i.Pending).Select(a => a.Key).Distinct(StringComparer.Ordinal).ToList();
    }

    public static bool NeedsAdmin(IEnumerable<PrivacyItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.SelectMany(i => i.Pending).Any(a => a.Entry.Definition.Privileges == PrivilegeLevel.Admin);
    }

    /// <summary>Divide em lotes que o aplicador aceita, sem separar as ações de um mesmo item.</summary>
    public static IReadOnlyList<IReadOnlyList<PrivacyItemState>> Batches(IEnumerable<PrivacyItemState> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var batches = new List<IReadOnlyList<PrivacyItemState>>();
        var current = new List<PrivacyItemState>();
        var count = 0;
        foreach (var item in items)
        {
            var n = item.Pending.Count();
            if (n == 0) continue;
            if (n > MaxActionsPerRequest) throw new InvalidOperationException($"{item.Item.Id} tem ações demais para um pedido.");
            if (count + n > MaxActionsPerRequest)
            {
                batches.Add(current);
                current = [];
                count = 0;
            }
            current.Add(item);
            count += n;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>Texto da confirmação: o que muda, o que você perde, e como desfazer.</summary>
    public static string ConfirmationText(IReadOnlyList<PrivacyItemState> items, int part = 1, int parts = 1)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder();
        if (parts > 1) sb.Append("Parte ").Append(part).Append(" de ").Append(parts).AppendLine(" (o aplicador aceita até 32 ajustes por vez).").AppendLine();
        sb.Append("O NEXUS vai aplicar ").Append(items.Count).Append(items.Count == 1 ? " proteção" : " proteções").AppendLine(":");
        foreach (var i in items)
        {
            sb.Append("• ").AppendLine(i.Item.Title);
            sb.Append("   Você perde: ").AppendLine(i.Item.Loses);
            if (i.Item.Warning is { } w) sb.Append("   Atenção: ").AppendLine(w);
        }
        sb.AppendLine();
        if (NeedsAdmin(items))
            sb.AppendLine("Algumas mudanças valem para o PC todo: o Windows pede permissão de administrador e o NEXUS cria um ponto de restauração antes.");
        if (items.SelectMany(i => i.Pending).Any(a => a.Entry.Definition.HandlerKind == PrivacyPolicyValueHandler.Kind))
            sb.AppendLine("Ajustes feitos por política fazem as Configurações mostrarem \"gerenciado pela sua organização\". É só um aviso.");
        sb.AppendLine("Tudo fica no diário e pode ser desfeito pelo botão Desfazer desta página ou pela página Aplicar.");
        sb.Append("Nenhum destes ajustes aumenta o FPS: o ganho é de privacidade e de menos propaganda.");
        return sb.ToString();
    }
}

/// <summary>Embrulho somente leitura: qualquer tentativa de escrita lança exceção (a página nunca escreve).</summary>
public sealed class ReadOnlyRegistryBackend : IRegistryBackend
{
    private readonly IRegistryBackend _inner;
    public ReadOnlyRegistryBackend(IRegistryBackend inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public RegistryRead Read(RegistryTarget target) => _inner.Read(target);
    public void Write(RegistryTarget target, string kind, string value) => throw new InvalidOperationException("Leitura apenas: a página de Privacidade não escreve no registro.");
    public void DeleteValue(RegistryTarget target) => throw new InvalidOperationException("Leitura apenas: a página de Privacidade não escreve no registro.");
    public bool DeleteKeyIfEmpty(RegHive hive, string path, RegView view, string? userSid = null) => throw new InvalidOperationException("Leitura apenas: a página de Privacidade não escreve no registro.");
}

/// <summary>Abre telas do app Configurações do Windows (o usuário vê e decide lá). Só endereços de privacidade conhecidos.</summary>
public static class SettingsLauncher
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "ms-settings:privacy", "ms-settings:privacy-webcam", "ms-settings:privacy-microphone", "ms-settings:privacy-location",
        "ms-settings:privacy-general", "ms-settings:privacy-feedback", "ms-settings:privacy-speech",
    };

    public static bool IsAllowed(string uri) => uri is not null && Allowed.Contains(uri);

    /// <summary>Abre a tela pedida. Retorna false (sem lançar) se o endereço não é permitido ou se o Windows recusou.</summary>
    public static bool Open(string uri)
    {
        if (!IsAllowed(uri) || !OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
