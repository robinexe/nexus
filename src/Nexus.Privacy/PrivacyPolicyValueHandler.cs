using System.Globalization;
using Nexus.Core;
using Nexus.WindowsActions.Registry;

namespace Nexus.Privacy;

/// <summary>
/// Lista FECHADA de valores de política de privacidade que o NEXUS pode escrever. A política de segurança geral
/// (<see cref="Nexus.WindowsActions.SafetyPolicy"/>) proíbe <c>SOFTWARE\Policies</c> inteiro, de propósito: lá também vivem
/// Defender, SmartScreen, Windows Update e BitLocker. Este módulo abre só os valores abaixo, um a um (chave + nome do valor),
/// todos documentados pela Microsoft como controles de privacidade. O catálogo não consegue ampliar esta lista: um alvo fora
/// dela é bloqueado no Assess e na restauração.
/// </summary>
public static class PrivacyPolicyAllowlist
{
    private static readonly (RegHive Hive, string Path, string Name)[] Entries =
    [
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData"),
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions"),
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"),
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis"),
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot"),
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\office\16.0\common\privacy", "ControllerConnectedServicesEnabled"),
        (RegHive.HKCU, @"SOFTWARE\Policies\Microsoft\office\common\clienttelemetry", "SendTelemetry"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "ConfigureDoNotTrack"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "SearchSuggestEnabled"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "UserFeedbackAllowed"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\FindMyDevice", "AllowFindMyDevice"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsActivateWithVoice"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsActivateWithVoiceAboveLock"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsGetDiagnosticInfo"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Maps", "AllowUntriggeredNetworkTrafficOnSettingsPage"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Maps", "AutoDownloadAndUpdateMapData"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\SettingSync", "DisableSettingSync"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\SettingSync", "DisableSettingSyncUserOverride"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowSearchToUseLocation"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB"),
        // catalog/tuning.v1.json (desempenho e Windows Update): mesmas regras, políticas documentadas pela Microsoft.
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DOPercentageMaxBackgroundBandwidth"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement"),
        (RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo"),
    ];

    private static readonly HashSet<string> Keys = new(Entries.Select(e => Key(e.Hive, e.Path, e.Name)), StringComparer.OrdinalIgnoreCase);

    /// <summary>Entradas permitidas (para testes e documentação).</summary>
    public static IReadOnlyList<(RegHive Hive, string Path, string Name)> All => Entries;

    private static string Key(RegHive hive, string path, string name) => $@"{hive}\{path.Trim('\\')}|{name}";

    /// <summary>Null se o valor está na lista; senão, o motivo da recusa. Comparação exata (sem prefixos, sem WOW6432Node).</summary>
    public static string? Check(RegHive hive, string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name) || path.Contains("\\\\", StringComparison.Ordinal) ||
            path.StartsWith('\\') || path.Contains("..", StringComparison.Ordinal))
            return "Caminho de política inválido.";
        return Keys.Contains(Key(hive, path, name))
            ? null
            : $@"{hive}\{path}\{name} não está na lista de políticas de privacidade permitidas pelo NEXUS.";
    }
}

/// <summary>
/// Handler "PrivacyPolicyValue": escreve UM valor de política de privacidade da <see cref="PrivacyPolicyAllowlist"/>.
/// Alvo: hive (HKLM/HKCU), path, name, type (DWord ou String). Mesmas garantias do RegistryValue: o backup guarda se valor e
/// chave existiam; restaurar "ausente" remove só o valor e, se o NEXUS criou a chave e ela ficou vazia, a chave.
/// HKCU usa o perfil de quem pediu quando o aplicador roda com outra conta (<see cref="UserHive"/>).
/// </summary>
public sealed class PrivacyPolicyValueHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "PrivacyPolicyValue";
    private const string KeyExistedDetail = "keyExisted";
    private static readonly HashSet<string> WritableTypes = new(StringComparer.OrdinalIgnoreCase) { "DWord", "String" };
    private static readonly HashSet<string> RestorableTypes = new(StringComparer.OrdinalIgnoreCase)
        { "DWord", "QWord", "String", "ExpandString", "Binary", "MultiString" };

    private readonly IRegistryBackend _reg;

    public PrivacyPolicyValueHandler(IRegistryBackend backend) => _reg = backend ?? throw new ArgumentNullException(nameof(backend));

    public string HandlerKind => Kind;

    private static RegistryTarget TargetOf(ActionInstance a) =>
        new(Enum.Parse<RegHive>(a.Target["hive"], ignoreCase: true), a.Target["path"], a.Target["name"], RegView.Default, UserHive.Of(a));

    /// <summary>Validação estática do alvo (comum a Assess e restauração).</summary>
    private static string? CheckTarget(ActionInstance action)
    {
        foreach (var k in new[] { "hive", "path", "name", "type" })
            if (!action.Target.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) return $"Alvo incompleto: falta '{k}'.";
        if (!Enum.TryParse<RegHive>(action.Target["hive"], true, out var hive) || !Enum.IsDefined(hive)) return "Hive não permitida.";
        if (UserHive.Check(action, hive) is { } sidProblem) return sidProblem;
        if (action.Target.ContainsKey("view") || action.Target.ContainsKey("policyPath")) return "Campos de alvo não aceitos por este handler.";
        if (!WritableTypes.Contains(action.Target["type"])) return "Tipo de valor não permitido para política (só DWord ou String).";
        return PrivacyPolicyAllowlist.Check(hive, action.Target["path"], action.Target["name"]);
    }

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckTarget(action) is { } problem) return Applicability.Blocked(problem);
        var type = action.Target["type"];
        if (action.Desired.Exists)
        {
            if (!string.Equals(action.Desired.Type, type, StringComparison.OrdinalIgnoreCase))
                return Applicability.Blocked("Tipo do valor desejado difere do tipo declarado no catálogo.");
            if (type.Equals("DWord", StringComparison.OrdinalIgnoreCase) &&
                (!uint.TryParse(action.Desired.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ||
                 n.ToString(CultureInfo.InvariantCulture) != action.Desired.Value))
                return Applicability.Blocked("Valor numérico não canônico ou fora da faixa.");
            if (action.Desired.Value is null || action.Desired.Value.Length > 260) return Applicability.Blocked("Valor desejado inválido.");
        }
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var r = _reg.Read(TargetOf(action));
        var detail = new Dictionary<string, string> { [KeyExistedDetail] = r.KeyExists ? "true" : "false" };
        // A política é o próprio alvo: o valor lido é "explícito" (quem o escreveu pode ter sido o NEXUS, o usuário ou uma GPO).
        return r.ValueExists ? StateSnapshot.Of(r.Kind!, r.Value!, ValueOrigin.Explicit, detail) : StateSnapshot.Absent(detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckTarget(action) is { } problem) throw new InvalidOperationException(problem);
        var t = TargetOf(action);
        if (action.Desired.Exists) _reg.Write(t, action.Desired.Type!, action.Desired.Value!);
        else _reg.DeleteValue(t);
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        var t = TargetOf(action);
        if (original.Exists)
        {
            _reg.Write(t, original.Type!, original.Value!);
            return;
        }
        _reg.DeleteValue(t);
        if (original.Detail is not null && original.Detail.TryGetValue(KeyExistedDetail, out var existed) && existed == "false")
            _reg.DeleteKeyIfEmpty(t.Hive, t.Path, t.View, t.UserSid);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckTarget(action) is { } problem) return problem;
        if (original.Exists && (original.Type is null || !RestorableTypes.Contains(original.Type) || original.Value is null))
            return $"Tipo original '{original.Type}' não restaurável.";
        return null;
    }
}
