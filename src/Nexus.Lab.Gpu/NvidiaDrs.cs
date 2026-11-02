using System.Globalization;
using System.Text.RegularExpressions;
using Nexus.Core;

namespace Nexus.Lab.Gpu;

/// <summary>Em qual perfil do driver NVIDIA (DRS) o ajuste vive.</summary>
public enum DrsProfileKind
{
    /// <summary>Perfil base ("_GLOBAL_DRIVER_PROFILE"): vale para todo programa sem valor próprio.</summary>
    Base,
    /// <summary>Perfil do executável (o do próprio driver, se existir; senão um "NEXUS - jogo.exe" criado pelo NEXUS).</summary>
    App,
}

/// <summary>Perfil alvo: base ou o do executável (só o nome do arquivo, validado por <see cref="NvidiaDrsSettings.IsValidExe"/>).</summary>
public sealed record DrsTarget(DrsProfileKind Kind, string? Exe)
{
    public static readonly DrsTarget Base = new(DrsProfileKind.Base, null);
    public static DrsTarget App(string exe) => new(DrsProfileKind.App, exe);
    public string Label => Kind == DrsProfileKind.Base ? "perfil global" : $"perfil de {Exe}";
}

/// <summary>
/// Leitura de um ajuste num perfil. <see cref="InProfile"/> = o perfil tem valor próprio para o ajuste (do usuário ou
/// predefinido pela NVIDIA); falso = herdado do global/padrão do driver (<see cref="Effective"/> traz o que vale).
/// </summary>
/// <param name="ProfileFound">Falso quando o executável não está em perfil nenhum (o driver usa o global).</param>
/// <param name="ProfileName">Nome do perfil (ex.: "Counter-Strike 2" ou "NEXUS - cs2.exe").</param>
/// <param name="InProfile">O perfil tem o ajuste.</param>
/// <param name="Value">Valor no perfil (quando <see cref="InProfile"/>).</param>
/// <param name="IsPredefined">O valor é o predefinido pela NVIDIA, não um do usuário.</param>
/// <param name="Effective">Valor que vale para o perfil, de onde vier (null = o driver não informou).</param>
public sealed record DrsReading(bool ProfileFound, string? ProfileName, bool InProfile, uint? Value, bool IsPredefined, uint? Effective)
{
    public static readonly DrsReading NoProfile = new(false, null, false, null, false, null);
}

/// <summary>Falha de NVAPI com o código (NvAPI_Status) e uma frase em português.</summary>
public sealed class NvidiaDrsException : InvalidOperationException
{
    public NvidiaDrsException() { }
    public NvidiaDrsException(string message) : base(message) { }
    public NvidiaDrsException(string message, Exception inner) : base(message, inner) { }
    public NvidiaDrsException(string operation, int status) : base($"{operation}: {NvApiStatus.Describe(status)}") => Status = status;

    public int Status { get; }
}

/// <summary>Códigos de NvAPI_Status usados (nvapi_lite_common.h) e o texto para o usuário.</summary>
public static class NvApiStatus
{
    public const int Ok = 0;
    public const int Error = -1;
    public const int LibraryNotFound = -2;
    public const int NoImplementation = -3;
    public const int ApiNotInitialized = -4;
    public const int InvalidArgument = -5;
    public const int NvidiaDeviceNotFound = -6;
    public const int IncompatibleStructVersion = -9;
    public const int InvalidUserPrivilege = -137;
    public const int SettingNotFound = -160;
    public const int ProfileNotFound = -163;
    public const int ProfileNameInUse = -164;
    public const int ExecutableNotFound = -166;
    public const int ExecutableAlreadyInUse = -167;
    public const int ProfileRemoved = -169;
    public const int ExecutablePathIsAmbiguous = -182;

    public static string Describe(int status) => status switch
    {
        Ok => "ok",
        LibraryNotFound => "biblioteca NVAPI do driver não encontrada (-2)",
        NoImplementation => "este driver não implementa a função (-3)",
        ApiNotInitialized => "NVAPI não inicializada (-4)",
        InvalidArgument => "argumento recusado pelo driver (-5)",
        NvidiaDeviceNotFound => "nenhuma placa NVIDIA com driver ativo (-6)",
        IncompatibleStructVersion => "versão de estrutura não suportada por este driver (-9)",
        InvalidUserPrivilege => "o driver exige administrador para gravar (-137)",
        SettingNotFound => "ajuste não existe no perfil (-160)",
        ProfileNotFound => "perfil não encontrado (-163)",
        ProfileNameInUse => "já existe um perfil com esse nome (-164)",
        ExecutableNotFound => "executável não está em nenhum perfil (-166)",
        ExecutableAlreadyInUse => "o executável já pertence a outro perfil (-167)",
        ProfileRemoved => "o perfil foi removido (-169)",
        ExecutablePathIsAmbiguous => "mais de um perfil do driver usa esse nome de executável; ajuste pelo Painel de Controle NVIDIA (-182)",
        _ => $"erro NVAPI {status.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>Leitura do banco de perfis do driver NVIDIA (DRS). Pode ser usada pela interface: não grava nada.</summary>
public interface INvidiaDrsReader
{
    /// <summary>null = NVAPI disponível; senão o motivo ("Sem placa/driver NVIDIA: ...").</summary>
    string? Unavailable();

    /// <summary>Lê um ajuste num perfil. Lança <see cref="NvidiaDrsException"/> (ex.: executável ambíguo).</summary>
    DrsReading Read(DrsTarget target, uint settingId);
}

/// <summary>
/// Leitura e escrita do DRS. SÓ o handler <see cref="NvidiaDrsHandler"/> (no aplicador) chama as escritas; a interface usa
/// apenas <see cref="INvidiaDrsReader"/> (há teste que proíbe as escritas na dll do Desktop).
/// </summary>
public interface INvidiaDrs : INvidiaDrsReader
{
    /// <summary>
    /// Grava um valor do usuário (DWORD) no perfil. Perfil de app inexistente: cria "NEXUS - exe" com o executável.
    /// Salva o banco (NvAPI_DRS_SaveSettings) antes de voltar.
    /// </summary>
    void SetUserValue(DrsTarget target, uint settingId, uint value);

    /// <summary>
    /// Tira o valor do usuário (NvAPI_DRS_DeleteProfileSetting: some do perfil ou volta ao predefinido da NVIDIA). Se o perfil
    /// for um "NEXUS - exe" e ficar sem ajuste nenhum, remove o perfil criado. Sem perfil/sem ajuste: nada a fazer.
    /// </summary>
    void ClearUserValue(DrsTarget target, uint settingId);
}

/// <summary>Instâncias por plataforma. A interface (Desktop) recebe SÓ o leitor; o aplicador cria o completo em GpuLabHandlers.</summary>
public static class NvidiaDrsFactory
{
    public static INvidiaDrsReader Reader() => OperatingSystem.IsWindows() ? new Windows.WindowsNvidiaDrs() : new NoNvidiaDrs();
}

/// <summary>Plataforma sem NVAPI (Linux nos testes, x86): sempre indisponível.</summary>
public sealed class NoNvidiaDrs : INvidiaDrs
{
    private readonly string _reason;
    public NoNvidiaDrs(string reason = "Sem placa/driver NVIDIA (NVAPI só existe no Windows 64 bits com driver NVIDIA).") => _reason = reason;
    public string? Unavailable() => _reason;
    public DrsReading Read(DrsTarget target, uint settingId) => throw new NvidiaDrsException(_reason);
    public void SetUserValue(DrsTarget target, uint settingId, uint value) => throw new NvidiaDrsException(_reason);
    public void ClearUserValue(DrsTarget target, uint settingId) => throw new NvidiaDrsException(_reason);
}

/// <summary>Um ajuste da lista fechada: id, nome, perfis aceitos, valores que o NEXUS aplica e valores válidos para restaurar.</summary>
/// <param name="ApplyValue">Valores que uma ação pode gravar (o que o catálogo oferece).</param>
/// <param name="RestoreValue">Valores válidos segundo NvApiDriverSettings.h (para devolver o que o usuário tinha).</param>
public sealed record NvidiaDrsSettingSpec(uint Id, string Name, DrsProfileKind Scope, Func<uint, bool> ApplyValue, Func<uint, bool> RestoreValue,
    Func<uint, string> Describe);

/// <summary>
/// Lista FECHADA de ajustes do driver que o NEXUS pode tocar. Ids e valores conferidos em NvApiDriverSettings.h (NVIDIA/nvapi,
/// licença MIT). Nenhum outro id passa: o catálogo não consegue pedir ajuste arbitrário.
/// </summary>
public static partial class NvidiaDrsSettings
{
    public const uint PreferredPstate = 0x1057EB71;      // PREFERRED_PSTATE_ID "Power management mode"
    public const uint PrerenderLimit = 0x007BA09E;       // PRERENDERLIMIT_ID "Maximum pre-rendered frames" (Modo de baixa latência)
    public const uint FrameRateLimit = 0x10835002;       // FRL_FPS_ID "Frame Rate Limiter" (Taxa máxima de quadros)
    public const uint VsyncMode = 0x00A879CF;            // VSYNCMODE_ID "Vertical Sync"
    public const uint ShaderCacheMaxSize = 0x00AC8497;   // PS_SHADERDISKCACHE_MAX_SIZE_ID "Shader disk cache maximum size" (MB)

    public const uint PstatePreferMax = 1;               // PREFERRED_PSTATE_PREFER_MAX
    public const uint PstateOptimalPower = 5;            // PREFERRED_PSTATE_OPTIMAL_POWER (padrão)
    public const uint LowLatencyOn = 1;                  // 1 quadro pré-renderizado = "Modo de baixa latência: Ligado"
    public const uint VsyncForceOn = 0x47814940;         // VSYNCMODE_FORCEON
    public const uint VsyncPassive = 0x60925292;         // VSYNCMODE_PASSIVE (padrão: o app decide)
    public const uint ShaderCache100Gb = 102400;         // MB
    public const uint ShaderCacheUnlimited = 0xFFFFFFFF; // PS_SHADERDISKCACHE_MAX_SIZE_MAX
    public const uint ShaderCacheDefaultMb = 0x4000;     // PS_SHADERDISKCACHE_MAX_SIZE_DEFAULT (cabeçalho atual)
    public const uint FpsMin = 20, FpsMax = 1023;         // FRL_FPS_MAX = 0x3ff; abaixo de 20 não serve para jogo

    public const string ValueType = "UInt32";

    private static readonly uint[] VsyncValues = [0x60925292, 0x08416747, 0x47814940, 0x32610244, 0x71271021, 0x13245256, 0x18888888];

    public static readonly IReadOnlyDictionary<uint, NvidiaDrsSettingSpec> All = new Dictionary<uint, NvidiaDrsSettingSpec>
    {
        [PreferredPstate] = new(PreferredPstate, "Modo de gerenciamento de energia", DrsProfileKind.App,
            v => v == PstatePreferMax, v => v <= 5, v => v switch
            {
                0 => "adaptável", 1 => "preferir desempenho máximo", 2 => "controlado pelo driver", 3 => "desempenho consistente",
                4 => "preferir mínimo", 5 => "ideal (padrão)", _ => Hex(v),
            }),
        [PrerenderLimit] = new(PrerenderLimit, "Modo de baixa latência (quadros pré-renderizados)", DrsProfileKind.App,
            v => v == LowLatencyOn, v => v <= 0xFF, v => v switch { 0 => "o jogo decide (padrão)", 1 => "ligado (1 quadro)", _ => $"{v} quadros" }),
        [FrameRateLimit] = new(FrameRateLimit, "Taxa máxima de quadros", DrsProfileKind.App,
            v => v is >= FpsMin and <= FpsMax, v => v <= FpsMax, v => v == 0 ? "desligada" : $"{v} FPS"),
        [VsyncMode] = new(VsyncMode, "Sincronização vertical", DrsProfileKind.App,
            v => v == VsyncForceOn, v => VsyncValues.Contains(v), v => v switch
            {
                0x60925292 => "o jogo decide (padrão)", 0x08416747 => "desligada", 0x47814940 => "ligada", 0x18888888 => "rápida (Fast Sync)",
                0x32610244 => "1/2 da taxa", 0x71271021 => "1/3 da taxa", 0x13245256 => "1/4 da taxa", _ => Hex(v),
            }),
        [ShaderCacheMaxSize] = new(ShaderCacheMaxSize, "Tamanho do cache de sombreador", DrsProfileKind.Base,
            v => v is ShaderCache100Gb or ShaderCacheUnlimited, _ => true, v => v switch
            {
                0 => "desligado", ShaderCacheUnlimited => "ilimitado", >= 1024 when v % 1024 == 0 => $"{v / 1024} GB", _ => $"{v} MB",
            }),
    };

    public static string Hex(uint v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\A[A-Za-z0-9 ._()+\-]{1,64}\.exe\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExePattern();

    [GeneratedRegex(@"\A0x[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Só o nome do arquivo ("cs2.exe"): sem pasta, sem "..", até 64 caracteres antes de ".exe".</summary>
    public static bool IsValidExe(string? exe) => exe is not null && ExePattern().IsMatch(exe) && !exe.Contains("..", StringComparison.Ordinal) &&
                                                  exe.Trim() == exe;

    public static NvidiaDrsSettingSpec? ParseSetting(string? raw) =>
        raw is not null && IdPattern().IsMatch(raw) && uint.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) &&
        All.TryGetValue(id, out var spec) ? spec : null;

    public static uint? ParseValue(StateSnapshot s) =>
        s.Exists && string.Equals(s.Type, ValueType, StringComparison.Ordinal) &&
        uint.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Alvo do catálogo/diário → perfil e ajuste. Null + motivo quando algo não confere.</summary>
    public static (DrsTarget Target, NvidiaDrsSettingSpec Spec)? ParseTarget(IReadOnlyDictionary<string, string> t, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(t);
        reason = null;
        if (ParseSetting(t.TryGetValue("setting", out var s) ? s : null) is not { } spec)
        {
            reason = "Ajuste do driver fora da lista permitida pelo NEXUS.";
            return null;
        }
        var profile = t.TryGetValue("profile", out var p) ? p : null;
        DrsTarget target;
        if (profile == "base")
        {
            if (t.ContainsKey("exe")) { reason = "Perfil global não leva executável."; return null; }
            target = DrsTarget.Base;
        }
        else if (profile == "app")
        {
            var exe = t.TryGetValue("exe", out var e) ? e : null;
            if (!IsValidExe(exe)) { reason = "Executável inválido (esperado só o nome do arquivo, como cs2.exe)."; return null; }
            target = DrsTarget.App(exe!);
        }
        else
        {
            reason = "Perfil inválido (esperado base ou app).";
            return null;
        }
        if (target.Kind != spec.Scope)
        {
            reason = spec.Scope == DrsProfileKind.App ? $"{spec.Name} só é aplicado por jogo, nunca no perfil global." : $"{spec.Name} só é aplicado no perfil global.";
            return null;
        }
        return (target, spec);
    }

    /// <summary>Taxa máxima sugerida para G-SYNC/FreeSync: 3 abaixo da taxa do monitor (Blur Busters), no mínimo 20.</summary>
    public static int SuggestedFpsCap(int refreshHz) => Math.Clamp(refreshHz - 3, (int)FpsMin, (int)FpsMax);

    /// <summary>Maior valor da lista do catálogo que não passa da sugestão (null = lista vazia/nenhum cabe).</summary>
    public static string? NearestAllowedCap(int refreshHz, IEnumerable<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        var target = SuggestedFpsCap(refreshHz);
        return allowed.Select(a => int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : -1)
            .Where(v => v > 0 && v <= target).OrderByDescending(v => v).Select(v => v.ToString(CultureInfo.InvariantCulture)).FirstOrDefault();
    }
}

/// <summary>
/// Handler "NvidiaDrsSetting": um ajuste da lista fechada (<see cref="NvidiaDrsSettings"/>) num perfil do driver NVIDIA.
/// Alvo: profile ("base"|"app"), exe (só com app, nome do arquivo) e setting ("0x1057EB71").
/// <para>Detect: "UInt32:valor" quando o perfil tem valor próprio (do usuário ou predefinido da NVIDIA, marcado em
/// Detail["predefined"]="1"); ausente quando o perfil não tem o ajuste ou o executável não está em perfil nenhum.</para>
/// <para>Apply: grava o valor do usuário (cria o perfil "NEXUS - exe" se o executável não tiver perfil).</para>
/// <para>Restore: original ausente ou predefinido → tira o valor do usuário (DeleteProfileSetting; perfil criado pelo NEXUS e
/// vazio é removido); original do usuário → grava de volta, desde que seja válido segundo o cabeçalho da NVIDIA.</para>
/// </summary>
public sealed class NvidiaDrsHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "NvidiaDrsSetting";
    public const string PredefinedDetail = "predefined";

    private readonly INvidiaDrs _drs;

    public NvidiaDrsHandler(INvidiaDrs drs) => _drs = drs ?? throw new ArgumentNullException(nameof(drs));

    public string HandlerKind => Kind;

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (NvidiaDrsSettings.ParseTarget(action.Target, out var why) is not { } parsed) return Applicability.Blocked(why!);
        if (action.Desired.Exists)
        {
            if (NvidiaDrsSettings.ParseValue(action.Desired) is not { } v || !parsed.Spec.ApplyValue(v))
                return Applicability.Blocked($"Valor {action.Desired.Describe()} não é aceito para {parsed.Spec.Name}.");
        }
        if (_drs.Unavailable() is { } reason) return Applicability.NotApplicable(reason);
        try { _ = _drs.Read(parsed.Target, parsed.Spec.Id); }
        catch (NvidiaDrsException ex) { return Applicability.Blocked($"O driver NVIDIA recusou a leitura do {parsed.Target.Label}: {ex.Message}"); }
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (NvidiaDrsSettings.ParseTarget(action.Target, out _) is not { } parsed) return StateSnapshot.Absent();
        if (_drs.Unavailable() is not null) return StateSnapshot.Absent(new Dictionary<string, string> { ["nvapi"] = "indisponível" });
        var r = _drs.Read(parsed.Target, parsed.Spec.Id);
        var detail = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.ProfileName is not null) detail["profileName"] = r.ProfileName;
        if (r.Effective is { } eff) detail["effective"] = eff.ToString(CultureInfo.InvariantCulture);
        if (!r.InProfile || r.Value is not { } value) return StateSnapshot.Absent(detail);
        detail[PredefinedDetail] = r.IsPredefined ? "1" : "0";
        return StateSnapshot.Of(NvidiaDrsSettings.ValueType, value.ToString(CultureInfo.InvariantCulture),
            r.IsPredefined ? ValueOrigin.Inherited : ValueOrigin.Explicit, detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (NvidiaDrsSettings.ParseTarget(action.Target, out var why) is not { } parsed) throw new InvalidOperationException(why);
        if (NvidiaDrsSettings.ParseValue(action.Desired) is not { } v || !parsed.Spec.ApplyValue(v))
            throw new InvalidOperationException($"Valor {action.Desired.Describe()} não é aceito para {parsed.Spec.Name}.");
        _drs.SetUserValue(parsed.Target, parsed.Spec.Id, v);
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (NvidiaDrsSettings.ParseTarget(action.Target, out var why) is not { } parsed) return why;
        if (original.Exists && !IsPredefined(original))
        {
            if (NvidiaDrsSettings.ParseValue(original) is not { } v) return "Valor original inválido no diário.";
            if (!parsed.Spec.RestoreValue(v)) return $"Valor original {v} fora da faixa válida de {parsed.Spec.Name}.";
        }
        return _drs.Unavailable() is { } reason ? reason + " Reinstale o driver NVIDIA e desfaça de novo." : null;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (CheckRestore(action, original) is { } problem) throw new InvalidOperationException(problem);
        var parsed = NvidiaDrsSettings.ParseTarget(action.Target, out _)!.Value;
        if (!original.Exists || IsPredefined(original))
            _drs.ClearUserValue(parsed.Target, parsed.Spec.Id);
        else
            _drs.SetUserValue(parsed.Target, parsed.Spec.Id, NvidiaDrsSettings.ParseValue(original)!.Value);
    }

    private static bool IsPredefined(StateSnapshot s) => s.Detail is not null && s.Detail.TryGetValue(PredefinedDetail, out var p) && p == "1";
}

/// <summary>Chaves das ações de catalog/nvidia.v1.json e as listas de valores que a página oferece (iguais ao catálogo; há teste).</summary>
public static class NvidiaDrsActions
{
    public const string ShaderCache = "gpu.nv.shadercache.max@1";
    public const string LowLatency = "gpu.nv.lowlatency.on.per-app@1";
    public const string MaxPower = "gpu.nv.power.max.per-app@1";
    public const string FpsCap = "gpu.nv.fps-cap.per-app@1";
    public const string VsyncOn = "gpu.nv.vsync.force-on.per-app@1";

    /// <summary>allowedValues de gpu.nv.fps-cap.per-app: valores redondos e "taxa − 3" dos monitores comuns (60…240 Hz).</summary>
    public static readonly IReadOnlyList<string> FpsCaps =
        ["30", "40", "45", "50", "57", "60", "72", "87", "90", "97", "117", "120", "141", "144", "162", "165", "177", "197", "237", "240"];

    /// <summary>allowedValues de gpu.nv.shadercache.max (MB): 100 GB e ilimitado.</summary>
    public static readonly IReadOnlyList<string> ShaderCacheValues = ["102400", "4294967295"];
}
