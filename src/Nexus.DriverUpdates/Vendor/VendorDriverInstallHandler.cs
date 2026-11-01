using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Core;
using Nexus.Lab.Gpu;

namespace Nexus.DriverUpdates.Vendor;

/// <summary>Resultado da conferência Authenticode (WinVerifyTrust + nome do assinante).</summary>
public sealed record SignatureCheck(bool Valid, string? Signer, string Reason);

/// <summary>Conferência da assinatura digital do instalador (no Windows: WinVerifyTrust; nos testes: falso).</summary>
public interface ISignatureVerifier
{
    SignatureCheck Verify(string path);
}

/// <summary>Resultado de rodar o instalador: código de saída (null se não terminou) e se estourou o tempo.</summary>
public sealed record InstallerRunResult(int? ExitCode, bool TimedOut);

/// <summary>Executa o instalador com argumentos fixos e espera (no Windows: processo; nos testes: falso).</summary>
public interface IInstallerRunner
{
    InstallerRunResult Run(string executable, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>Pasta do perfil de quem pediu (pelo SID em ProfileList, como serviço; ou a do próprio processo).</summary>
public interface IUserProfileLocator
{
    string? UserProfile(string? userSid);
}

/// <summary>Manifesto gravado ANTES de rodar o instalador: driver anterior, backup e resultado.</summary>
public sealed record VendorDriverManifest(
    string Vendor,
    string Version,
    string Sha256,
    IReadOnlyList<string> DeviceInstanceIds,
    RollbackSource Source,
    string? PreviousInf,
    string? PreviousOriginalInf,
    string? PreviousMatchingId,
    string? PreviousVersion,
    string? PreviousVendorVersion,
    DateTime CreatedUtc,
    DateTime? InstalledUtc = null,
    int? ExitCode = null,
    bool RebootRequired = false);

/// <summary>
/// Instala o driver de vídeo baixado do site oficial (NVIDIA/AMD). Alvo: fabricante, versão e SHA-256 do instalador que a
/// interface baixou e conferiu (nunca um caminho: o arquivo fica em <c>&lt;perfil&gt;\AppData\Local\NEXUS\drivers\&lt;fab&gt;\&lt;versão&gt;\</c>,
/// nome fixo). No aplicador elevado:
/// <list type="number">
/// <item>copia o instalador para a pasta de dados do aplicador (só administradores alteram) calculando o SHA-256 da cópia — tem
/// que ser o mesmo que o usuário confirmou (contra troca do arquivo entre a conferência e a execução, TOCTOU);</item>
/// <item>confere a assinatura Authenticode DA CÓPIA: cadeia válida e assinante "NVIDIA Corporation" / "Advanced Micro Devices".
/// Inválida = apaga e nunca executa;</item>
/// <item>exporta o driver atual (pnputil /export-driver, o mesmo backup das atualizações do Windows Update) e grava o manifesto;</item>
/// <item>roda a cópia com as opções silenciosas do fabricante e espera (tempo limite); lê o código de saída.</item>
/// </list>
/// Detect: versão instalada do driver da placa (NVIDIA pela numeração do Windows; AMD pela "RadeonSoftwareVersion" ou pela troca do
/// driver depois da instalação). Restore: reinstala o pacote anterior do backup (instalação forçada, como o Desfazer do Windows Update).
/// </summary>
public sealed class VendorDriverInstallHandler : IActionHandler, IRestoreValidator
{
    public const string Kind = "DriverUpdatesVendorInstall";
    public const string StateType = "GpuDriver";
    public const string Installed = "instalada";
    public const string Pending = "pendente";
    public static readonly TimeSpan NvidiaTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AmdTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// NVIDIA: <c>-s</c> (silencioso; documentado para o setup.exe do pacote e aceito pelo pacote baixado, que repassa as opções ao
    /// setup.exe), <c>-noreboot</c> (não reinicia sozinho), <c>-noeula</c> (não mostra o contrato). Sem <c>-clean</c>: mantém as
    /// configurações do Painel de Controle da NVIDIA. Código 0 = sucesso; 1 = sucesso, reinício necessário.
    /// AMD: <c>-INSTALL</c> (instalação sem janelas do instalador da AMD, "Command Line Installation"). Código 0 = sucesso.
    /// </summary>
    public static IReadOnlyList<string> SilentArguments(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => ["-s", "-noreboot", "-noeula"],
        GpuVendor.Amd => ["-INSTALL"],
        _ => throw new ArgumentException("Fabricante sem instalação pelo NEXUS.", nameof(vendor)),
    };

    private readonly IDeviceInventory _devices;
    private readonly IDriverPackageTool _tool;
    private readonly IBootClock _clock;
    private readonly ISignatureVerifier _verifier;
    private readonly IInstallerRunner _runner;
    private readonly IUserProfileLocator _profiles;
    private readonly string _dataDirectory;
    private readonly string _backupRoot;
    private readonly Func<DateTime> _utcNow;

    public VendorDriverInstallHandler(IDeviceInventory devices, IDriverPackageTool tool, IBootClock clock, ISignatureVerifier verifier,
        IInstallerRunner runner, IUserProfileLocator profiles, string dataDirectory, Func<DateTime>? utcNow = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _backupRoot = DriverUpdateInstallHandler.BackupRoot(dataDirectory);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string HandlerKind => Kind;

    public static uint PciVendor(GpuVendor vendor) => vendor == GpuVendor.Nvidia ? GpuIdentity.VendorNvidia : GpuIdentity.VendorAmd;

    public string BackupDirectory(GpuVendor vendor, string version) => Path.Combine(_backupRoot, $"gpu-{VendorDriverPolicy.VendorToken(vendor)}-{version}");
    private string ManifestPath(GpuVendor vendor, string version) => Path.Combine(BackupDirectory(vendor, version), "nexus-manifest.json");

    private sealed record Target(GpuVendor Vendor, string Version, string Sha256, string? UserSid);

    private static Target? Parse(ActionInstance a)
    {
        var t = a.Target;
        var vendor = VendorDriverPolicy.VendorFromToken(t.TryGetValue("vendor", out var v) ? v : null);
        var version = t.TryGetValue("version", out var ver) ? ver : null;
        var sha = t.TryGetValue("sha256", out var s) ? s : null;
        var sid = t.TryGetValue(UserProfileScope.TargetKey, out var u) ? u : null;
        if (vendor is not { } vd || !VendorDriverPolicy.IsValidVersion(vd, version) || !VendorDriverPolicy.IsValidSha256(sha)) return null;
        if (sid is not null && !UserProfileScope.IsValidSid(sid)) return null;
        return new Target(vd, version!, sha!, sid);
    }

    private static Target Require(ActionInstance a) =>
        Parse(a) ?? throw new InvalidOperationException("Pedido inválido: fabricante, versão ou SHA-256 fora do formato.");

    private List<DeviceDriver> Adapters(GpuVendor vendor) => _devices.List().Where(d => d.IsDisplayOf(PciVendor(vendor))).ToList();

    /// <summary>Versão do fabricante em uso no dispositivo (NVIDIA pela numeração do Windows; AMD pela RadeonSoftwareVersion).</summary>
    public static string? VendorVersionOf(GpuVendor vendor, DeviceDriver d) =>
        vendor == GpuVendor.Nvidia ? VendorVersions.NvidiaFromWindows(d.DriverVersion) : d.VendorPackageVersion;

    // ------------------------------------------------------------------ contrato

    public Applicability Assess(ActionInstance action, SystemContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        var t = Parse(action);
        if (t is null) return Applicability.Blocked("Pedido inválido: fabricante, versão ou SHA-256 fora do formato.");
        if (action.Desired.Type != StateType || action.Desired.Value != Installed) return Applicability.Blocked("Valor desejado inválido.");
        if (Adapters(t.Vendor).Count == 0)
            return Applicability.NotApplicable($"Nenhuma placa de vídeo {GpuIdentity.VendorName(t.Vendor)} presente neste PC.");
        return Applicability.Ok;
    }

    public StateSnapshot Detect(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var t = Require(action);
        var adapters = Adapters(t.Vendor);
        if (adapters.Count == 0) throw new InvalidOperationException($"Nenhuma placa de vídeo {GpuIdentity.VendorName(t.Vendor)} presente.");
        var current = adapters.Select(d => VendorVersionOf(t.Vendor, d) ?? d.DriverVersion ?? "sem driver").Distinct(StringComparer.Ordinal).ToList();
        var detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["alvo"] = t.Version, ["atual"] = string.Join(", ", current) };
        if (adapters.Any(d => VendorVersions.Compare(t.Vendor, VendorVersionOf(t.Vendor, d), t.Version) >= 0))
            return StateSnapshot.Of(StateType, Installed, detail: detail);
        var m = ReadManifest(t.Vendor, t.Version);
        if (m is { InstalledUtc: { } at } && m.Sha256 == t.Sha256)
        {
            if (m.RebootRequired && _clock.LastBootUtc < at)
            {
                detail["reinicio"] = "pendente";
                return StateSnapshot.Of(StateType, Installed, detail: detail);
            }
            // AMD sem RadeonSoftwareVersion: vale a troca do driver (versão do Windows mais nova que a de antes da instalação).
            if (t.Vendor == GpuVendor.Amd && m.PreviousVersion is not null &&
                adapters.Any(d => m.DeviceInstanceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase) && d.VendorPackageVersion is null &&
                                  DeviceMatching.CompareVersions(d.DriverVersion, m.PreviousVersion) > 0))
            {
                detail["conferido"] = "driver trocado pela instalação";
                return StateSnapshot.Of(StateType, Installed, detail: detail);
            }
        }
        return StateSnapshot.Of(StateType, Pending, detail: detail);
    }

    public void Apply(ActionInstance action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var t = Require(action);
        var adapters = Adapters(t.Vendor);
        if (adapters.Count == 0) throw new InvalidOperationException($"Nenhuma placa de vídeo {GpuIdentity.VendorName(t.Vendor)} presente.");

        // 1) Cópia para a pasta do aplicador (só administradores alteram) e SHA-256 da CÓPIA.
        var profile = _profiles.UserProfile(t.UserSid)
                      ?? throw new InvalidOperationException("Não foi possível achar a pasta do seu usuário (o aplicador roda com outra conta?). Nada foi executado.");
        var source = VendorDriverPolicy.InstallerPath(VendorDriverPolicy.UserDownloadRoot(profile), t.Vendor, t.Version);
        var copy = VendorDriverPolicy.AdminCopyPath(_dataDirectory, t.Vendor, t.Version);
        try
        {
            CopyAndHash(source, copy, t.Sha256);

            // 2) Assinatura da cópia (o arquivo que vai rodar).
            var sig = _verifier.Verify(copy);
            if (!sig.Valid)
                throw new InvalidOperationException($"Assinatura digital inválida ({sig.Reason}): o instalador NÃO foi executado e foi apagado.");
            if (!VendorDriverPolicy.IsAllowedSigner(t.Vendor, sig.Signer))
                throw new InvalidOperationException($"O instalador está assinado por \"{sig.Signer ?? "desconhecido"}\", não pela {GpuIdentity.VendorName(t.Vendor)}: NÃO foi executado.");

            // 3) Backup do driver atual + manifesto (antes de qualquer mudança).
            var manifest = Backup(t, adapters);

            // 4) Instalador do fabricante, silencioso, com tempo limite.
            var timeout = t.Vendor == GpuVendor.Nvidia ? NvidiaTimeout : AmdTimeout;
            var run = _runner.Run(copy, SilentArguments(t.Vendor), timeout);
            if (run.TimedOut)
                throw new InvalidOperationException($"O instalador da {GpuIdentity.VendorName(t.Vendor)} passou de {timeout.TotalMinutes:0} min e ainda não terminou. " +
                                                    "Ele pode continuar sozinho; espere, reinicie o PC e confira a versão nesta página.");
            var (ok, reboot) = Interpret(t.Vendor, run.ExitCode);
            if (!ok)
            {
                // Alguns instaladores devolvem código diferente mesmo instalando: vale o que o Windows diz do driver.
                var changed = Adapters(t.Vendor).Any(d => manifest.DeviceInstanceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase) &&
                                                          manifest.PreviousVersion is not null && DeviceMatching.CompareVersions(d.DriverVersion, manifest.PreviousVersion) > 0);
                if (!changed)
                {
                    WriteManifest(manifest with { ExitCode = run.ExitCode });
                    throw new InvalidOperationException($"O instalador da {GpuIdentity.VendorName(t.Vendor)} terminou com o código {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"} e o driver não mudou.");
                }
            }
            WriteManifest(manifest with { InstalledUtc = _utcNow(), ExitCode = run.ExitCode, RebootRequired = reboot });
        }
        finally
        {
            TryDelete(copy); // ~1 GB: não fica na pasta de dados
        }
    }

    /// <summary>Código de saída → (sucesso, reinício). NVIDIA: 0 ok, 1 ok + reinício. AMD: 0 ok, 3010/1641 ok + reinício.</summary>
    public static (bool Ok, bool Reboot) Interpret(GpuVendor vendor, int? exitCode) => (vendor, exitCode) switch
    {
        (_, 0) => (true, false),
        (GpuVendor.Nvidia, 1) => (true, true),
        (_, 3010) or (_, 1641) => (true, true),
        _ => (false, false),
    };

    private static void CopyAndHash(string source, string copy, string expectedSha)
    {
        var info = new FileInfo(source);
        if (!info.Exists)
            throw new InvalidOperationException("O instalador baixado não está mais na pasta de downloads do NEXUS. Baixe de novo. Nada foi executado.");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length <= 0 || info.Length > VendorDriverPolicy.MaxInstallerBytes)
            throw new InvalidOperationException("O arquivo baixado não é um instalador válido (link ou tamanho fora do limite). Nada foi executado.");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        TryDelete(copy);
        string actual;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 17))
        using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 17))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 17];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > VendorDriverPolicy.MaxInstallerBytes) throw new InvalidOperationException("Instalador acima do limite de tamanho. Nada foi executado.");
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
            }
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        if (!string.Equals(actual, expectedSha, StringComparison.Ordinal))
            throw new InvalidOperationException("O instalador mudou depois de conferido (SHA-256 diferente): NÃO foi executado. Baixe de novo pelo NEXUS.");
    }

    private VendorDriverManifest Backup(Target t, List<DeviceDriver> adapters)
    {
        var primary = adapters.FirstOrDefault(d => d.InfName is not null) ?? adapters[0];
        var plan = RollbackRules.Plan(primary);
        var dir = BackupDirectory(t.Vendor, t.Version);
        if (Directory.Exists(dir))
            Directory.Move(dir, dir + "-" + _utcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)); // nunca sobrescreve backup anterior
        Directory.CreateDirectory(dir);
        string? original = null;
        if (plan.Source == RollbackSource.Backup)
        {
            original = _tool.OriginalInfName(plan.InfName!) ?? throw new InvalidOperationException($"{plan.InfName} não aparece no repositório de drivers: nada foi instalado.");
            _tool.Export(plan.InfName!, dir);
            if (DriverUpdateInstallHandler.FindInf(dir, original) is null)
                throw new InvalidOperationException($"O backup do driver atual ({plan.InfName}) não foi criado: nada foi instalado.");
        }
        else if (plan.Source == RollbackSource.Inbox)
        {
            original = plan.InfName;
            if (!File.Exists(Path.Combine(_tool.InboxInfDirectory, plan.InfName!))) plan = plan with { Source = RollbackSource.None };
        }
        var manifest = new VendorDriverManifest(VendorDriverPolicy.VendorToken(t.Vendor), t.Version, t.Sha256, adapters.Select(d => d.InstanceId).ToList(),
            plan.Source, plan.InfName, original, plan.MatchingDeviceId, plan.PreviousVersion, VendorVersionOf(t.Vendor, primary), _utcNow());
        WriteManifest(manifest);
        return manifest;
    }

    public void Restore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        if (original.Value != Pending) return;
        var t = Require(action);
        var m = ReadManifest(t.Vendor, t.Version) ?? throw new InvalidOperationException("Manifesto do backup não encontrado: não há como voltar ao driver anterior.");
        switch (m.Source)
        {
            case RollbackSource.Backup:
            {
                var inf = DriverUpdateInstallHandler.FindInf(BackupDirectory(t.Vendor, t.Version), m.PreviousOriginalInf ?? "")
                          ?? throw new InvalidOperationException($"{m.PreviousOriginalInf} não está no backup.");
                _tool.Add(inf);
                _tool.ForceInstall(m.PreviousMatchingId!, inf);
                break;
            }
            case RollbackSource.Inbox:
            {
                var inf = Path.Combine(_tool.InboxInfDirectory, m.PreviousOriginalInf!);
                if (!File.Exists(inf)) throw new InvalidOperationException($"{m.PreviousOriginalInf} não existe mais em {_tool.InboxInfDirectory}.");
                _tool.ForceInstall(m.PreviousMatchingId!, inf);
                break;
            }
            default:
                throw new InvalidOperationException(DriverUpdateInstallHandler.NoRollback);
        }
        // Desfeito: o "instalado" do manifesto não vale mais (Detect volta a olhar só a versão do driver).
        WriteManifest(m with { InstalledUtc = null, RebootRequired = false });
    }

    public string? CheckRestore(ActionInstance action, StateSnapshot original)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(original);
        var t = Parse(action);
        if (t is null) return "Pedido inválido no diário.";
        if (original.Type != StateType || (original.Value != Pending && original.Value != Installed)) return "Original inválido.";
        if (original.Value == Installed) return null;
        var m = ReadManifest(t.Vendor, t.Version);
        if (m is null || m.Sha256 != t.Sha256) return "Manifesto do backup não encontrado; nada foi escrito.";
        if (m.Source == RollbackSource.None) return DriverUpdateInstallHandler.NoRollback;
        if (string.IsNullOrWhiteSpace(m.PreviousMatchingId) || m.PreviousMatchingId.Length > 200 || m.PreviousMatchingId.Any(char.IsControl)) return "ID do dispositivo ausente no manifesto.";
        if (m.Source == RollbackSource.Backup && (m.PreviousOriginalInf is null || DriverUpdateInstallHandler.FindInf(BackupDirectory(t.Vendor, t.Version), m.PreviousOriginalInf) is null))
            return "O pacote do driver anterior não está no backup; nada foi escrito.";
        if (m.Source == RollbackSource.Inbox && !RollbackRules.IsInboxInfName(m.PreviousOriginalInf)) return "INF do Windows inválido no manifesto.";
        return null;
    }

    // ------------------------------------------------------------------ manifesto

    public VendorDriverManifest? ReadManifest(GpuVendor vendor, string version)
    {
        if (!VendorDriverPolicy.IsValidVersion(vendor, version)) return null;
        var path = ManifestPath(vendor, version);
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<VendorDriverManifest>(File.ReadAllBytes(path));
            return m is not null && m.Vendor == VendorDriverPolicy.VendorToken(vendor) && m.Version == version ? m : null;
        }
        catch (JsonException) { return null; }
    }

    private void WriteManifest(VendorDriverManifest m)
    {
        var vendor = VendorDriverPolicy.VendorFromToken(m.Vendor)!.Value;
        var path = ManifestPath(vendor, m.Version);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(m));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
