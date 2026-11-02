using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Nexus.HardwareDiscovery;

/// <summary>
/// Descoberta no Windows, SOMENTE LEITURA. Cada área é isolada: uma falha vira "indisponível" com o motivo,
/// sem derrubar as demais. Chaves de registro são sempre abertas com <c>OpenSubKey(nome)</c> (sem escrita).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsDiscovery
{
    private const string RegCurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static SystemSnapshot Capture()
    {
        var sw = Stopwatch.StartNew();
        var signals = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new Dictionary<string, string>(StringComparer.Ordinal);
        var readSignals = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<DiscoverySection>();

        SmbiosParser.Table? smbios = null;
        string? smbiosError = null;
        try { smbios = ReadSmbios(); }
        catch (Exception ex) when (IsReadFailure(ex)) { smbiosError = ex.Message; }

        var (osSection, build, edition, elevated) = Guard("Windows", OperatingSystemSection);
        sections.Add(osSection);
        var (cpuSection, cpuModel, cpuVendor, cores, logical) = Guard("Processador", CpuSection);
        sections.Add(cpuSection);
        var (gpuSection, gpus) = Guard("GPU e driver", GpuSection);
        sections.Add(gpuSection);
        var (memSection, totalMem, modules) = Guard("Memória", () => MemorySection(smbios, smbiosError));
        sections.Add(memSection);
        var (boardSection, sysMan, sysModel, isVm) = Guard("Placa e firmware", () => BoardSection(smbios, smbiosError));
        sections.Add(boardSection);
        var (diskSection, drives, systemDrive, physicalDisks) = Guard("Discos", DiskSection);
        sections.Add(diskSection);
        sections.Add(Guard("Rede", NetworkSection));
        var (powerSection, scheme, schemeName) = Guard("Plano de energia", PowerSection);
        sections.Add(powerSection);
        var (featureSection, featureSignals, featureUnreadable, featureRead) = Guard("Recursos de jogo e gráficos", FeatureSection);
        sections.Add(featureSection);
        foreach (var k in featureRead ?? []) readSignals.Add(k);
        foreach (var kv in featureSignals ?? []) signals[kv.Key] = kv.Value;
        foreach (var kv in featureUnreadable ?? []) unreadable[kv.Key] = kv.Value;
        var (serviceSection, services) = Guard("Serviços", ServiceSection);
        sections.Add(serviceSection);
        var (startupSection, startup) = Guard("Itens de inicialização", StartupSection);
        sections.Add(startupSection);

        var facts = new SystemFacts
        {
            WindowsBuild = build,
            WindowsEdition = edition,
            CpuModel = cpuModel,
            CpuVendor = cpuVendor,
            PhysicalCores = cores,
            LogicalProcessors = logical,
            Gpus = gpus ?? [],
            TotalMemoryBytes = totalMem,
            MemoryModules = modules ?? [],
            ActivePowerScheme = scheme,
            ActivePowerSchemeName = schemeName,
            IsElevated = elevated,
            IsVirtualMachine = isVm,
            SystemManufacturer = sysMan,
            SystemModel = sysModel,
            Drives = drives ?? [],
            SystemDrive = systemDrive,
            PhysicalDisks = physicalDisks ?? [],
            SystemDiskMedia = drives?.FirstOrDefault(d => string.Equals(d.Name, systemDrive, StringComparison.OrdinalIgnoreCase))?.Media ?? DiskMedia.Unknown,
            SystemDiskBus = SystemDiskBus(physicalDisks, systemDrive),
            StartupItems = startup ?? [],
            Services = services ?? [],
            RegistrySignals = signals,
            ReadSignals = readSignals,
            UnreadableSignals = unreadable,
        };
        return new SystemSnapshot(DateTimeOffset.Now, sections, facts, sw.Elapsed);
    }

    // ------------------------------------------------------------------ infraestrutura

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException
            or InvalidDataException or ExternalException or FormatException or OverflowException or ArgumentException
            or NotSupportedException or NetworkInformationException or IndexOutOfRangeException;

    /// <summary>Executa uma área; em falha, devolve uma seção "indisponível" com o motivo e valores padrão (nulos).</summary>
    private static T Guard<T>(string title, Func<T> probe)
    {
        try { return probe(); }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            var section = new DiscoverySection(title, [Fact.Unavailable(title, "descoberta", $"{ex.GetType().Name}: {ex.Message}")]);
            return FailureValue<T>(section);
        }
    }

    private static T FailureValue<T>(DiscoverySection section)
    {
        if (typeof(T) == typeof(DiscoverySection)) return (T)(object)section;
        // Tuplas (DiscoverySection, ...): seção na primeira posição, demais campos com default.
        var args = typeof(T).GetGenericArguments();
        var values = args.Select((t, i) => i == 0 ? section : (t.IsValueType ? Activator.CreateInstance(t) : null)).ToArray();
        return (T)Activator.CreateInstance(typeof(T), values)!;
    }

    private static RegistryKey? Open(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        return root.OpenSubKey(path); // somente leitura
    }

    private static string? RegString(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Registry64)
    {
        using var k = Open(hive, path, view);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            string s => s,
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            byte[] b => Convert.ToHexString(b),
            string[] a => string.Join(";", a),
            var o => o.ToString(),
        };
    }

    private static string HklmSource(string path, string name) => $@"registro HKLM\{path}\{name}";
    private static string HkcuSource(string path, string name) => $@"registro HKCU\{path}\{name}";

    // ------------------------------------------------------------------ Windows

    private static (DiscoverySection, int?, string?, bool?) OperatingSystemSection()
    {
        var facts = new List<Fact>();
        var product = RegString(RegistryHive.LocalMachine, RegCurrentVersion, "ProductName");
        var editionId = RegString(RegistryHive.LocalMachine, RegCurrentVersion, "EditionID");
        var display = RegString(RegistryHive.LocalMachine, RegCurrentVersion, "DisplayVersion");
        var buildText = RegString(RegistryHive.LocalMachine, RegCurrentVersion, "CurrentBuildNumber")
                        ?? RegString(RegistryHive.LocalMachine, RegCurrentVersion, "CurrentBuild");
        var ubr = RegString(RegistryHive.LocalMachine, RegCurrentVersion, "UBR");
        int? build = int.TryParse(buildText, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;

        // ProductName continua "Windows 10 ..." no Windows 11 (comportamento conhecido): o nome é inferido pelo build.
        if (product is not null && build is >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            facts.Add(Fact.Inferred("Produto", "Windows 11 " + product["Windows 10".Length..].Trim(), HklmSource(RegCurrentVersion, "ProductName"),
                $"O registro diz \"{product}\", mas o build {build} (≥ 22000) é Windows 11."));
        else
            facts.Add(Fact.FromOptional("Produto", product, HklmSource(RegCurrentVersion, "ProductName"), "Valor ausente."));
        facts.Add(Fact.FromOptional("Edição", editionId, HklmSource(RegCurrentVersion, "EditionID"), "Valor ausente."));
        facts.Add(Fact.FromOptional("Versão", display, HklmSource(RegCurrentVersion, "DisplayVersion"), "Valor ausente (Windows anterior ao 20H2)."));
        facts.Add(build is null
            ? Fact.Unavailable("Build", HklmSource(RegCurrentVersion, "CurrentBuildNumber"), "Valor ausente ou não numérico.")
            : Fact.Detected("Build", ubr is null ? build.Value.ToString(CultureInfo.InvariantCulture) : $"{build}.{ubr}", HklmSource(RegCurrentVersion, "CurrentBuildNumber + UBR")));
        facts.Add(Fact.Detected("Arquitetura", RuntimeInformation.OSArchitecture.ToString(), "RuntimeInformation.OSArchitecture"));

        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        facts.Add(Fact.Detected("Processo elevado", elevated ? "Sim" : "Não (algumas leituras podem ficar indisponíveis)", "WindowsPrincipal.IsInRole(Administrator)"));
        facts.Add(Fact.Detected("Nome do computador", Environment.MachineName, "Environment.MachineName"));
        return (new DiscoverySection("Windows", facts), build, editionId, elevated);
    }

    // ------------------------------------------------------------------ CPU

    private static (DiscoverySection, string?, string?, int?, int?) CpuSection()
    {
        const string cpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        var facts = new List<Fact>();
        var model = RegString(RegistryHive.LocalMachine, cpuKey, "ProcessorNameString")?.Trim();
        var vendorId = RegString(RegistryHive.LocalMachine, cpuKey, "VendorIdentifier");
        var mhz = RegString(RegistryHive.LocalMachine, cpuKey, "~MHz");
        facts.Add(Fact.FromOptional("Modelo", model, HklmSource(cpuKey, "ProcessorNameString"), "Valor ausente."));
        var vendor = vendorId switch { "AuthenticAMD" => "AMD", "GenuineIntel" => "Intel", null => null, var v => v };
        facts.Add(Fact.FromOptional("Fabricante", vendor, HklmSource(cpuKey, "VendorIdentifier"), "Valor ausente."));
        facts.Add(mhz is null
            ? Fact.Unavailable("Frequência nominal", HklmSource(cpuKey, "~MHz"), "Valor ausente.")
            : Fact.Detected("Frequência nominal", $"{mhz} MHz", HklmSource(cpuKey, "~MHz"), "Valor informado pelo firmware no boot; não é a frequência atual."));

        int? cores = null, logical = null;
        try
        {
            var topo = CpuTopologyParser.Parse(ReadProcessorInformation());
            cores = topo.PhysicalCores;
            logical = topo.LogicalProcessors;
            const string src = "GetLogicalProcessorInformationEx(RelationAll)";
            facts.Add(Fact.Detected("Soquetes", topo.Packages.ToString(CultureInfo.InvariantCulture), src));
            facts.Add(Fact.Detected("Núcleos físicos", topo.PhysicalCores.ToString(CultureInfo.InvariantCulture), src));
            facts.Add(Fact.Detected("Processadores lógicos", topo.LogicalProcessors.ToString(CultureInfo.InvariantCulture), src));
            facts.Add(Fact.Detected("SMT/Hyper-Threading", topo.Smt ? "Ativo" : "Inativo ou não suportado", src));
            facts.Add(topo.EfficiencyClasses > 1
                ? Fact.Detected("Arquitetura híbrida", $"Sim: {topo.EfficiencyClasses} classes de eficiência, {topo.PerformanceCores} núcleos na classe de desempenho", src)
                : Fact.Detected("Arquitetura híbrida", "Não (uma classe de eficiência)", src));
            facts.Add(topo.L3CacheBytes.Count == 0
                ? Fact.Unavailable("Cache L3", src, "Nenhuma relação de cache L3 informada.")
                : Fact.Detected("Cache L3", string.Join(" + ", topo.L3CacheBytes.Select(c => Units.Bytes(c))) +
                    (topo.L3CacheBytes.Count > 1 ? $" ({topo.L3CacheBytes.Count} blocos L3)" : ""), src));
            facts.Add(Fact.Detected("Nós NUMA", topo.NumaNodes.ToString(CultureInfo.InvariantCulture), src));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            facts.Add(Fact.Unavailable("Topologia", "GetLogicalProcessorInformationEx", ex.Message));
        }
        return (new DiscoverySection("Processador", facts), model, vendor, cores, logical);
    }

    private static unsafe byte[] ReadProcessorInformation()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF, null, ref len);
        if (len == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetLogicalProcessorInformationEx não informou o tamanho.");
        var buffer = new byte[len];
        fixed (byte* p = buffer)
        {
            if (GetLogicalProcessorInformationEx(0xFFFF, p, ref len) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetLogicalProcessorInformationEx falhou.");
        }
        return buffer.AsSpan(0, (int)len).ToArray();
    }

    // ------------------------------------------------------------------ GPU

    private static (DiscoverySection, IReadOnlyList<GpuInfo>) GpuSection()
    {
        var gpus = new List<GpuInfo>();
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var (description, hardwareId, driverKey) in EnumeratePresentDisplayAdapters())
        {
            var vendor = VendorFromHardwareId(hardwareId);
            string? version = null, date = null, provider = null;
            ulong? memory = null;
            if (driverKey is not null)
            {
                using var k = Open(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
                if (k is not null)
                {
                    version = k.GetValue("DriverVersion") as string;
                    date = k.GetValue("DriverDate") as string;
                    provider = k.GetValue("ProviderName") as string;
                    memory = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l when l > 0 => (ulong)l,
                        byte[] q when q.Length >= 8 => BitConverter.ToUInt64(q, 0),
                        _ => k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i when i != 0 => (uint)i,
                            byte[] d when d.Length >= 4 => BitConverter.ToUInt32(d, 0),
                            _ => null,
                        },
                    };
                }
            }
            gpus.Add(new GpuInfo(description, vendor, version, date, provider, memory));
            rows.Add([description, vendor ?? "?", version ?? "indisponível", date ?? "indisponível", memory is null ? "indisponível" : Units.Bytes(memory)]);
        }

        var facts = new List<Fact>
        {
            gpus.Count == 0
                ? Fact.Unavailable("Adaptadores de vídeo", "SetupAPI (DIGCF_PRESENT)", "Nenhum adaptador de vídeo presente foi enumerado.")
                : Fact.Detected("Adaptadores de vídeo presentes", gpus.Count.ToString(CultureInfo.InvariantCulture), "SetupAPI (DIGCF_PRESENT)"),
        };
        var table = new DiscoveryTable("Adaptadores", ["Nome", "Fabricante", "Versão do driver", "Data do driver", "Memória dedicada"], rows,
            FactStatus.Detected, $@"SetupAPI + registro HKLM\SYSTEM\CurrentControlSet\Control\Class\{DisplayClass}\<n>",
            "Memória dedicada conforme informada pelo driver (HardwareInformation.qwMemorySize).");
        return (new DiscoverySection("GPU e driver", facts, [table]), gpus);
    }

    private static string? VendorFromHardwareId(string? hwid)
    {
        if (hwid is null) return null;
        var i = hwid.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > hwid.Length) return null;
        return hwid.Substring(i + 4, 4).ToUpperInvariant() switch
        {
            "10DE" => "NVIDIA",
            "1002" => "AMD",
            "8086" => "Intel",
            "1414" => "Microsoft",
            var v => $"PCI {v}",
        };
    }

    private static unsafe List<(string Description, string? HardwareId, string? DriverKey)> EnumeratePresentDisplayAdapters()
    {
        var result = new List<(string, string?, string?)>();
        var classGuid = Guid.Parse(DisplayClass);
        const uint digcfPresent = 0x2;
        var set = SetupDiGetClassDevsW(&classGuid, IntPtr.Zero, IntPtr.Zero, digcfPresent);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs falhou.");
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SpDevinfoData { CbSize = (uint)sizeof(SpDevinfoData) };
                if (SetupDiEnumDeviceInfo(set, i, &data) == 0) break; // ERROR_NO_MORE_ITEMS
                var desc = DeviceProperty(set, &data, 0 /* SPDRP_DEVICEDESC */) ?? "(sem descrição)";
                var hwid = DeviceProperty(set, &data, 1 /* SPDRP_HARDWAREID */);
                var driver = DeviceProperty(set, &data, 9 /* SPDRP_DRIVER */);
                result.Add((desc, hwid, driver));
                if (i > 64) break; // proteção
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static unsafe string? DeviceProperty(IntPtr set, SpDevinfoData* data, uint property)
    {
        var buffer = new byte[2048];
        fixed (byte* p = buffer)
        {
            if (SetupDiGetDeviceRegistryPropertyW(set, data, property, out _, p, (uint)buffer.Length, out var required) == 0)
                return null;
            var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(required, (uint)buffer.Length));
            var end = text.IndexOf('\0', StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text; // REG_MULTI_SZ: primeira entrada
        }
    }

    // ------------------------------------------------------------------ Memória

    private static (DiscoverySection, ulong?, IReadOnlyList<MemoryModule>) MemorySection(SmbiosParser.Table? smbios, string? smbiosError)
    {
        var facts = new List<Fact>();
        ulong? total = null;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status) != 0)
        {
            total = status.TotalPhys;
            facts.Add(Fact.Detected("Total utilizável pelo Windows", Units.Bytes(status.TotalPhys), "GlobalMemoryStatusEx"));
            facts.Add(Fact.Detected("Disponível agora", $"{Units.Bytes(status.AvailPhys)} ({100 - status.MemoryLoad}% livre)", "GlobalMemoryStatusEx"));
        }
        else
        {
            facts.Add(Fact.Unavailable("Total", "GlobalMemoryStatusEx", $"Falhou (erro {Marshal.GetLastPInvokeError()})."));
        }

        IReadOnlyList<MemoryModule> modules = [];
        var tables = new List<DiscoveryTable>();
        if (smbios is null)
        {
            facts.Add(Fact.Unavailable("Módulos", "SMBIOS tipo 17", smbiosError ?? "Tabela SMBIOS indisponível."));
        }
        else
        {
            modules = SmbiosParser.MemoryModules(smbios);
            var installed = modules.Where(m => m.SizeBytes is not null).Aggregate(0UL, (a, m) => a + m.SizeBytes!.Value);
            facts.Add(Fact.Detected("Módulos instalados", modules.Count.ToString(CultureInfo.InvariantCulture), "SMBIOS tipo 17 (firmware)"));
            if (installed > 0) facts.Add(Fact.Detected("Capacidade instalada", Units.Bytes(installed), "SMBIOS tipo 17 (firmware)"));
            tables.Add(new DiscoveryTable("Módulos de memória",
                ["Slot", "Tamanho", "Tipo", "Velocidade máx. (MT/s)", "Configurada (MT/s)", "Fabricante", "Modelo"],
                modules.Select(m => (IReadOnlyList<string?>)[m.Locator, Units.Bytes(m.SizeBytes), m.Type,
                    m.MaxSpeedMts?.ToString(CultureInfo.InvariantCulture), m.ConfiguredSpeedMts?.ToString(CultureInfo.InvariantCulture),
                    m.Manufacturer, m.PartNumber]).ToList(),
                FactStatus.Detected, "SMBIOS tipo 17 via GetSystemFirmwareTable('RSMB')",
                "\"Velocidade máx.\" é a que o módulo anuncia ao firmware (geralmente o perfil JEDEC), não necessariamente o perfil XMP/EXPO."));
        }
        return (new DiscoverySection("Memória", facts, tables), total, modules);
    }

    private static unsafe SmbiosParser.Table ReadSmbios()
    {
        const uint rsmb = 0x52534D42; // 'RSMB'
        var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
        if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSystemFirmwareTable('RSMB') não informou o tamanho.");
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var got = GetSystemFirmwareTable(rsmb, 0, p, size);
            if (got == 0 || got > size) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSystemFirmwareTable('RSMB') falhou.");
        }
        return SmbiosParser.Parse(buffer);
    }

    // ------------------------------------------------------------------ Placa / firmware

    private static (DiscoverySection, string?, string?, bool?) BoardSection(SmbiosParser.Table? smbios, string? smbiosError)
    {
        const string src = "SMBIOS via GetSystemFirmwareTable('RSMB')";
        if (smbios is null)
            return (new DiscoverySection("Placa e firmware", [Fact.Unavailable("SMBIOS", src, smbiosError ?? "Indisponível.")]), null, null, null);

        var (man, product) = SmbiosParser.SystemInfo(smbios);
        var (boardMan, boardProduct) = SmbiosParser.Baseboard(smbios);
        var (biosVendor, biosVersion, biosDate) = SmbiosParser.Bios(smbios);
        var vm = SmbiosParser.LooksVirtual(man, product);
        var facts = new List<Fact>
        {
            Fact.FromOptional("Fabricante do sistema", man, src + " tipo 1", "String vazia no firmware."),
            Fact.FromOptional("Modelo do sistema", product, src + " tipo 1", "String vazia no firmware."),
            Fact.FromOptional("Placa-mãe", boardMan is null && boardProduct is null ? null : $"{boardMan} {boardProduct}".Trim(), src + " tipo 2", "Sem estrutura tipo 2."),
            Fact.FromOptional("BIOS/UEFI", biosVendor is null ? null : $"{biosVendor} {biosVersion} ({biosDate})", src + " tipo 0", "Sem estrutura tipo 0."),
            Fact.Detected("Versão SMBIOS", $"{smbios.Major}.{smbios.Minor}", src),
            vm is null
                ? Fact.Unavailable("Máquina virtual", src, "Fabricante e modelo ausentes.")
                : Fact.Inferred("Máquina virtual", vm.Value ? "Sim" : "Não", src + " tipo 1", "Procura marcadores de hipervisor (VMware, VirtualBox, QEMU, Hyper-V \"Virtual Machine\"...) no fabricante/modelo."),
        };
        return (new DiscoverySection("Placa e firmware", facts), man, product, vm);
    }

    // ------------------------------------------------------------------ Discos

    private static (DiscoverySection, IReadOnlyList<DriveFacts>, string?, IReadOnlyList<PhysicalDiskFacts>) DiskSection()
    {
        var ready = new List<DriveInfo>();
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try { if (d.IsReady) ready.Add(d); else rows.Add([d.Name, "não pronto", null, null, null, null]); }
            catch (Exception ex) when (IsReadFailure(ex)) { rows.Add([d.Name, $"indisponível: {ex.Message}", null, null, null, null]); }
        }

        // SSD/HDD pelo próprio disco (IOCTL, sem administrador). Falha aqui só deixa o tipo como desconhecido.
        IReadOnlyList<PhysicalDiskFacts> physicalDisks = [];
        IReadOnlyDictionary<string, DiskMedia> volumeMedia = new Dictionary<string, DiskMedia>();
        string? storageError = null;
        try { (physicalDisks, volumeMedia) = StorageClassifier.Read(WindowsStorageQuery.Instance, ready.Select(d => d.Name)); }
        catch (Exception ex) when (IsReadFailure(ex)) { storageError = $"{ex.GetType().Name}: {ex.Message}"; }

        var drives = new List<DriveFacts>();
        foreach (var d in ready)
        {
            try
            {
                var media = volumeMedia.GetValueOrDefault(d.Name, DiskMedia.Unknown);
                drives.Add(new DriveFacts(d.Name, d.DriveFormat, (ulong)d.TotalSize, (ulong)d.TotalFreeSpace, media));
                var pct = d.TotalSize > 0 ? 100.0 * d.TotalFreeSpace / d.TotalSize : 0;
                var disk = physicalDisks.FirstOrDefault(p => p.Volumes.Contains(d.Name, StringComparer.OrdinalIgnoreCase));
                rows.Add([d.Name, d.VolumeLabel, d.DriveFormat, Units.Bytes((ulong)d.TotalSize),
                    $"{Units.Bytes((ulong)d.TotalFreeSpace)} ({pct.ToString("0", CultureInfo.InvariantCulture)}%)",
                    media == DiskMedia.Unknown ? "desconhecido" : StorageClassifier.Describe(media, disk?.Bus)]);
            }
            catch (Exception ex) when (IsReadFailure(ex)) { rows.Add([d.Name, $"indisponível: {ex.Message}", null, null, null, null]); }
        }

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        var tables = new List<DiscoveryTable>
        {
            new("Volumes (unidades fixas)", ["Unidade", "Rótulo", "Sistema de arquivos", "Tamanho", "Livre", "Tipo de disco"], rows, FactStatus.Detected,
                "System.IO.DriveInfo + IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS"),
        };
        if (physicalDisks.Count > 0)
        {
            tables.Add(new("Discos físicos", ["Disco", "Modelo", "Tipo", "Barramento", "Tamanho", "Unidades"],
                physicalDisks.Select(p => (IReadOnlyList<string?>)[p.Number.ToString(CultureInfo.InvariantCulture), p.Model, p.MediaText, p.Bus,
                    p.SizeBytes is { } sz ? Units.Bytes(sz) : null, string.Join(" ", p.Volumes)]).ToList(),
                FactStatus.Detected, @"\\.\PhysicalDriveN: IOCTL_STORAGE_QUERY_PROPERTY (penalidade de busca, barramento), IOCTL_DISK_GET_DRIVE_GEOMETRY_EX",
                "Tipo pelo próprio disco, sem administrador: sem penalidade de busca = SSD (mesmo critério do Otimizar unidades do Windows); sem resposta, barramento NVMe = SSD."));
        }
        else
        {
            // Sem resposta do IOCTL: lista do registro, como antes (modelo e barramento inferidos do ID de instância).
            var physical = new List<IReadOnlyList<string?>>();
            using (var k = Open(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\disk\Enum"))
            {
                var count = k?.GetValue("Count") is int c ? c : 0;
                for (var i = 0; i < count && i < 64; i++)
                    if (k!.GetValue(i.ToString(CultureInfo.InvariantCulture)) is string id)
                        physical.Add([ModelFromInstanceId(id), BusFromInstanceId(id), id]);
            }
            tables.Add(new("Discos físicos", ["Modelo (inferido)", "Barramento (inferido)", "ID de instância"], physical, FactStatus.Inferred,
                @"registro HKLM\SYSTEM\CurrentControlSet\Services\disk\Enum",
                "Modelo e barramento extraídos do ID de instância do dispositivo. Tipo de mídia (SSD/HDD) indisponível: " +
                (storageError ?? "nenhum disco respondeu à consulta IOCTL.")));
        }
        var facts = new List<Fact> { Fact.FromOptional("Unidade do sistema", systemDrive, "Environment.SystemDirectory", "Indisponível.") };
        var sysDisk = physicalDisks.FirstOrDefault(p => p.Volumes.Contains(systemDrive ?? "", StringComparer.OrdinalIgnoreCase));
        var sysMedia = systemDrive is null ? DiskMedia.Unknown : volumeMedia.GetValueOrDefault(systemDrive, DiskMedia.Unknown);
        facts.Add(sysMedia != DiskMedia.Unknown && sysDisk is not null
            ? Fact.Detected("Tipo do disco do sistema", StorageClassifier.Describe(sysMedia, sysDisk.Bus), "IOCTL_STORAGE_QUERY_PROPERTY",
                $"Disco {sysDisk.Number}{(sysDisk.Model is null ? "" : $" ({sysDisk.Model})")}: {sysDisk.MediaSource}.")
            : Fact.Unavailable("Tipo do disco do sistema", "IOCTL_STORAGE_QUERY_PROPERTY",
                storageError ?? (sysDisk is null ? "Não deu para ligar a unidade do sistema a um disco físico." : $"Disco {sysDisk.Number}: {sysDisk.MediaSource}.")));
        return (new DiscoverySection("Discos", facts, tables), drives, systemDrive, physicalDisks);
    }

    private static string? SystemDiskBus(IReadOnlyList<PhysicalDiskFacts>? disks, string? systemDrive) =>
        systemDrive is null ? null : disks?.FirstOrDefault(p => p.Volumes.Contains(systemDrive, StringComparer.OrdinalIgnoreCase))?.Bus;

    internal static string? ModelFromInstanceId(string id)
    {
        // Ex.: SCSI\Disk&Ven_NVMe&Prod_Samsung_SSD_980\5&2d6...  ou  USBSTOR\Disk&Ven_SanDisk&Prod_Ultra&Rev_1.00\...
        var parts = id.Split('\\');
        if (parts.Length < 2) return null;
        var fields = parts[1].Split('&');
        var ven = fields.FirstOrDefault(f => f.StartsWith("Ven_", StringComparison.OrdinalIgnoreCase))?[4..];
        var prod = fields.FirstOrDefault(f => f.StartsWith("Prod_", StringComparison.OrdinalIgnoreCase))?[5..];
        var text = string.Join(" ", new[] { ven, prod }.Where(s => !string.IsNullOrEmpty(s) && !s.Equals("NVMe", StringComparison.OrdinalIgnoreCase)));
        return text.Length == 0 ? null : text.Replace('_', ' ');
    }

    internal static string? BusFromInstanceId(string id)
    {
        var bus = id.Split('\\')[0];
        if (id.Contains("Ven_NVMe", StringComparison.OrdinalIgnoreCase)) return "NVMe";
        return bus.ToUpperInvariant() switch { "SCSI" => "SCSI/SATA/NVMe (via storport)", "USBSTOR" => "USB", "IDE" => "IDE/SATA", var b => b };
    }

    // ------------------------------------------------------------------ Rede

    private static DiscoverySection NetworkSection()
    {
        var rows = new List<(bool Connected, IReadOnlyList<string?> Cells)>();
        var connected = new List<string>();
        var gateways = new HashSet<string>(StringComparer.Ordinal);
        int bindings = 0, notPresent = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (NetworkClassifier.IsFilterBinding(n.Name, n.Description)) { bindings++; continue; }
            if (n.OperationalStatus == OperationalStatus.NotPresent) { notPresent++; continue; }
            var props = n.GetIPProperties();
            var ipv4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList();
            var gw = props.GatewayAddresses.Select(g => g.Address.ToString()).Where(a => a != "0.0.0.0" && a.Contains('.', StringComparison.Ordinal)).ToList();
            var dns = string.Join(", ", props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));
            var up = n.OperationalStatus == OperationalStatus.Up;
            var speed = up && n.Speed > 0
                ? (n.Speed >= 1_000_000_000 ? $"{n.Speed / 1_000_000_000.0:0.#} Gb/s" : $"{n.Speed / 1_000_000} Mb/s")
                : null;
            var isConnected = up && ipv4.Any(NetworkClassifier.IsUsableIpv4);
            if (isConnected)
            {
                connected.Add(n.Name);
                foreach (var g in gw) gateways.Add(g);
            }
            var type = NetworkClassifier.TypeName((int)n.NetworkInterfaceType,
                Enum.IsDefined(n.NetworkInterfaceType) ? n.NetworkInterfaceType.ToString() : null);
            rows.Add((isConnected, [n.Name, n.Description, type, n.OperationalStatus.ToString(), speed, string.Join(", ", ipv4), string.Join(", ", gw), dns]));
        }

        const string src = "System.Net.NetworkInformation";
        var facts = new List<Fact>
        {
            Fact.Detected("Adaptadores", rows.Count.ToString(CultureInfo.InvariantCulture), src,
                $"Omitidos: {bindings} vínculo(s) de filtro NDIS (WFP, QoS, monitores; nome terminado em \"-0000\") e {notPresent} adaptador(es) não presente(s)."),
            connected.Count == 0
                ? Fact.Unavailable("Conectados (ligados e com IPv4 utilizável)", src, "Nenhum adaptador ligado com IPv4 fora de 169.254/16.")
                : Fact.Detected("Conectados (ligados e com IPv4 utilizável)", string.Join(", ", connected), src),
            gateways.Count == 0
                ? Fact.Unavailable("Gateway padrão", src, "Nenhum adaptador conectado informa gateway IPv4.")
                : Fact.Detected("Gateway padrão", string.Join(", ", gateways), src),
        };
        return new DiscoverySection("Rede", facts,
            [new DiscoveryTable("Adaptadores", ["Nome", "Descrição", "Tipo", "Estado", "Velocidade do link", "IPv4", "Gateway", "DNS"],
                rows.OrderByDescending(r => r.Connected).Select(r => r.Cells).ToList(),
                FactStatus.Detected, $"{src}.NetworkInterface",
                "Velocidade do link é a negociada (adaptadores virtuais informam valores nominais); não é medição de latência nem de banda.")]);
    }

    // ------------------------------------------------------------------ Energia

    private static readonly Dictionary<Guid, string> KnownSchemes = new()
    {
        [Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e")] = "Equilibrado",
        [Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "Alto desempenho",
        [Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a")] = "Economia de energia",
        [Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61")] = "Desempenho máximo",
    };

    private static unsafe (DiscoverySection, Guid?, string?) PowerSection()
    {
        var facts = new List<Fact>();
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0)
            return (new DiscoverySection("Plano de energia", [Fact.Unavailable("Esquema ativo", "PowerGetActiveScheme", "Chamada falhou.")]), null, null);
        Guid scheme;
        try { scheme = Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }

        string? name = null;
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, &scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) == 0 && size > 0)
        {
            var buf = new byte[size];
            fixed (byte* b = buf)
                if (PowerReadFriendlyName(IntPtr.Zero, &scheme, IntPtr.Zero, IntPtr.Zero, b, ref size) == 0)
                    name = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        }

        facts.Add(Fact.Detected("GUID do esquema ativo", scheme.ToString("D"), "PowerGetActiveScheme"));
        facts.Add(name is null
            ? Fact.Unavailable("Nome", "PowerReadFriendlyName", "Leitura do nome falhou.")
            : Fact.Detected("Nome", name, "PowerReadFriendlyName"));
        facts.Add(KnownSchemes.TryGetValue(scheme, out var known)
            ? Fact.Detected("Esquema base", known, "GUID conhecido do Windows")
            : Fact.Inferred("Esquema base", "Personalizado ou do fabricante", "PowerGetActiveScheme", "GUID não corresponde aos esquemas padrão do Windows."));
        return (new DiscoverySection("Plano de energia", facts), scheme, name ?? known);
    }

    // ------------------------------------------------------------------ Recursos de jogo / gráficos


    private static (DiscoverySection, Dictionary<string, string>, Dictionary<string, string>, HashSet<string>) FeatureSection()
    {
        var signals = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);
        var facts = new List<Fact>();

        void Read(string signal, RegistryHive hive, string path, string name, string label, Func<string?, (string Text, FactStatus Status, string? Note)> interpret)
        {
            var src = hive == RegistryHive.LocalMachine ? HklmSource(path, name) : HkcuSource(path, name);
            try
            {
                var raw = RegString(hive, path, name);
                read.Add(signal);
                if (raw is not null) signals[signal] = raw;
                var (text, status, note) = interpret(raw);
                facts.Add(new Fact(label, text, status, src, note));
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                unreadable[signal] = ex.Message;
                facts.Add(Fact.Unavailable(label, src, ex.Message));
            }
        }

        Read(DiscoverySignals.GameMode, RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "Modo de Jogo",
            raw => raw switch
            {
                "1" => ("Ativado (valor explícito 1)", FactStatus.Detected, null),
                "0" => ("Desativado (valor explícito 0)", FactStatus.Detected, null),
                null => ("Ativado (padrão do Windows)", FactStatus.Inferred, "Valor ausente; desde o Windows 10 1703 o padrão é ativado."),
                _ => ($"Valor não reconhecido: {raw}", FactStatus.Detected, null),
            });
        Read(DiscoverySignals.AppCapture, RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "Captura em segundo plano (Game Bar)",
            raw => raw switch
            {
                "1" => ("Ativada", FactStatus.Detected, null),
                "0" => ("Desativada", FactStatus.Detected, null),
                null => ("Não configurada (valor ausente)", FactStatus.Detected, "Sem valor explícito; o comportamento efetivo depende da versão do Windows e da Game Bar."),
                _ => ($"Valor: {raw}", FactStatus.Detected, null),
            });
        Read(DiscoverySignals.GameDvrEnabled, RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", "Game DVR (GameConfigStore)",
            raw => raw is null ? ("Valor ausente", FactStatus.Detected, null) : (raw == "1" ? "Ativado" : raw == "0" ? "Desativado" : raw, FactStatus.Detected, null));
        Read(DiscoverySignals.Hags, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "Agendamento de GPU acelerado por hardware (HAGS)",
            raw => raw switch
            {
                "2" => ("Ativado (configurado)", FactStatus.Detected, "Configuração; o estado efetivo após o boot exige D3DKMTQueryAdapterInfo (não implementado)."),
                "1" => ("Desativado (configurado)", FactStatus.Detected, "Configuração; o estado efetivo após o boot exige D3DKMTQueryAdapterInfo (não implementado)."),
                null => ("Padrão do driver (valor ausente)", FactStatus.Detected, "Sem configuração explícita; o padrão depende do driver e da versão do Windows."),
                _ => ($"Valor: {raw}", FactStatus.Detected, null),
            });
        Read(DiscoverySignals.VbsEnabled, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", "VBS (configuração)",
            raw => raw switch
            {
                "1" => ("Configurado como ativado", FactStatus.Detected, "Configuração no registro; o estado em execução exige Win32_DeviceGuard (não implementado). O NEXUS não altera recursos de segurança."),
                "0" => ("Configurado como desativado", FactStatus.Detected, "O NEXUS não altera recursos de segurança."),
                null => ("Sem configuração explícita", FactStatus.Detected, "O estado em execução exige Win32_DeviceGuard (não implementado)."),
                _ => ($"Valor: {raw}", FactStatus.Detected, null),
            });
        Read(DiscoverySignals.HvciEnabled, RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", "Integridade de memória / HVCI (configuração)",
            raw => raw switch
            {
                "1" => ("Configurada como ativada", FactStatus.Detected, "O NEXUS não altera recursos de segurança."),
                "0" => ("Configurada como desativada", FactStatus.Detected, "O NEXUS não altera recursos de segurança."),
                null => ("Sem configuração explícita", FactStatus.Detected, null),
                _ => ($"Valor: {raw}", FactStatus.Detected, null),
            });
        return (new DiscoverySection("Recursos de jogo e gráficos", facts), signals, unreadable, read);
    }

    // ------------------------------------------------------------------ Serviços

    private static unsafe (DiscoverySection, IReadOnlyList<ServiceFacts>) ServiceSection()
    {
        const uint scManagerEnumerate = 0x0004, serviceWin32 = 0x30, stateAll = 0x3, errorMoreData = 234;
        var scm = OpenSCManagerW(null, null, scManagerEnumerate);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "OpenSCManager (somente enumeração) falhou.");
        var services = new List<ServiceFacts>();
        // Memória nativa: os ponteiros de nome devolvidos apontam para DENTRO do buffer, então ele não pode ser movido
        // pelo coletor de lixo entre a chamada e a leitura.
        nuint size = 256 * 1024;
        var buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            uint resume = 0;
            var stride = (2 * IntPtr.Size + 36 + 7) & ~7; // ENUM_SERVICE_STATUS_PROCESSW
            while (true)
            {
                var ok = EnumServicesStatusExW(scm, 0 /* SC_ENUM_PROCESS_INFO */, serviceWin32, stateAll, buffer, (uint)size,
                    out var needed, out var returned, ref resume, null);
                var err = ok == 0 ? Marshal.GetLastPInvokeError() : 0;
                if (ok == 0 && err != errorMoreData)
                    throw new Win32Exception(err, "EnumServicesStatusEx falhou.");

                for (var i = 0; i < returned; i++)
                {
                    var entry = buffer + i * stride;
                    var name = Marshal.PtrToStringUni(*(IntPtr*)entry);
                    var display = Marshal.PtrToStringUni(*(IntPtr*)(entry + IntPtr.Size));
                    var state = *(uint*)(entry + 2 * IntPtr.Size + 4); // SERVICE_STATUS_PROCESS.dwCurrentState
                    if (name is null) continue;
                    services.Add(new ServiceFacts(name, display, StateName(state), StartTypeOf(name)));
                }
                if (ok != 0) break;
                if (returned == 0 && needed <= size) break; // sem progresso: evita laço infinito
                if (needed > size)
                {
                    NativeMemory.Free(buffer);
                    buffer = null;
                    size = needed;
                    buffer = (byte*)NativeMemory.Alloc(size);
                }
                if (services.Count > 5000) break; // proteção
            }
        }
        finally
        {
            if (buffer is not null) NativeMemory.Free(buffer);
            CloseServiceHandle(scm);
        }

        services.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var running = services.Count(s => s.State == "Em execução");
        var facts = new List<Fact>
        {
            Fact.Detected("Serviços Win32", services.Count.ToString(CultureInfo.InvariantCulture), "EnumServicesStatusEx (SC_MANAGER_ENUMERATE_SERVICE)"),
            Fact.Detected("Em execução", running.ToString(CultureInfo.InvariantCulture), "EnumServicesStatusEx"),
        };
        var table = new DiscoveryTable("Serviços", ["Nome", "Nome de exibição", "Estado", "Inicialização"],
            services.Select(s => (IReadOnlyList<string?>)[s.Name, s.DisplayName, s.State, s.StartType]).ToList(),
            FactStatus.Detected, @"SCM (estado) + registro HKLM\SYSTEM\CurrentControlSet\Services\<nome>\Start (tipo de inicialização)");
        return (new DiscoverySection("Serviços", facts, [table]), services);
    }

    private static string StateName(uint s) => s switch
    {
        1 => "Parado",
        2 => "Iniciando",
        3 => "Parando",
        4 => "Em execução",
        5 => "Continuando",
        6 => "Pausando",
        7 => "Pausado",
        _ => $"Desconhecido ({s})",
    };

    private static string? StartTypeOf(string service)
    {
        try
        {
            using var k = Open(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}");
            if (k is null) return null;
            var start = k.GetValue("Start") as int?;
            var delayed = k.GetValue("DelayedAutostart") as int? == 1;
            return start switch
            {
                0 => "Boot",
                1 => "Sistema",
                2 => delayed ? "Automático (atraso)" : "Automático",
                3 => "Manual",
                4 => "Desativado",
                null => null,
                _ => $"Desconhecido ({start})",
            };
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return "indisponível";
        }
    }

    // ------------------------------------------------------------------ Inicialização

    private static (DiscoverySection, IReadOnlyList<StartupItem>) StartupSection()
    {
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
        var items = new List<StartupItem>();

        void FromRun(RegistryHive hive, RegistryView view, string location, string approvedKind)
        {
            using var k = Open(hive, run, view);
            if (k is null) return;
            using var a = Open(hive, $@"{approved}\{approvedKind}");
            foreach (var name in k.GetValueNames())
            {
                if (name.Length == 0) continue;
                var cmd = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
                items.Add(new StartupItem(name, location, cmd, ApprovedState(a?.GetValue(name) as byte[])));
            }
        }

        FromRun(RegistryHive.CurrentUser, RegistryView.Registry64, @"HKCU\...\Run", "Run");
        FromRun(RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\...\Run", "Run");
        FromRun(RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\...\Run (32 bits)", "Run32");

        void FromFolder(Environment.SpecialFolder folder, RegistryHive hive, string location)
        {
            var dir = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            using var a = Open(hive, $@"{approved}\StartupFolder");
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new StartupItem(name, location, f, ApprovedState(a?.GetValue(name) as byte[])));
            }
        }

        FromFolder(Environment.SpecialFolder.Startup, RegistryHive.CurrentUser, "Pasta Inicializar (usuário)");
        FromFolder(Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine, "Pasta Inicializar (todos)");

        var enabled = items.Count(i => i.Enabled != false);
        var facts = new List<Fact>
        {
            Fact.Detected("Itens encontrados", items.Count.ToString(CultureInfo.InvariantCulture), "Run (HKCU/HKLM 64 e 32 bits) + pastas Inicializar"),
            Fact.Inferred("Habilitados", enabled.ToString(CultureInfo.InvariantCulture), "Explorer\\StartupApproved",
                "Estado lido do valor binário StartupApproved (formato não documentado: byte 0 par = habilitado, ímpar = desabilitado; ausente = habilitado)."),
        };
        var table = new DiscoveryTable("Itens", ["Nome", "Origem", "Estado (inferido)", "Comando"],
            items.Select(i => (IReadOnlyList<string?>)[i.Name, i.Location, i.Enabled switch { true => "Habilitado", false => "Desabilitado", null => "Habilitado (sem registro de aprovação)" }, i.Command]).ToList(),
            FactStatus.Inferred, "Registro Run + pastas Inicializar + StartupApproved",
            "Tarefas agendadas e serviços que iniciam com o Windows não estão nesta lista (pendente).");
        return (new DiscoverySection("Itens de inicialização", facts, [table]), items);
    }

    internal static bool? ApprovedState(byte[]? raw) => raw is { Length: > 0 } ? (raw[0] & 1) == 0 : null;

    // ------------------------------------------------------------------ P/Invoke (somente leitura)

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

    [LibraryImport("powrprof.dll")]
    private static unsafe partial uint PowerReadFriendlyName(IntPtr root, Guid* scheme, IntPtr subgroup, IntPtr setting, byte* buffer, ref uint size);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static unsafe partial IntPtr SetupDiGetClassDevsW(Guid* classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static unsafe partial int SetupDiEnumDeviceInfo(IntPtr set, uint index, SpDevinfoData* data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static unsafe partial int SetupDiGetDeviceRegistryPropertyW(IntPtr set, SpDevinfoData* data, uint property, out uint regType,
        byte* buffer, uint size, out uint required);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial int SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, byte* services,
        uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int CloseServiceHandle(IntPtr handle);
}
