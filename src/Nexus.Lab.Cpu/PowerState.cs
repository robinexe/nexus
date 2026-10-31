using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.Lab.Cpu;

/// <summary>GUIDs das configurações de processador do plano de energia (subgrupo SUB_PROCESSOR). Fonte: powercfg /qh e Microsoft Learn.</summary>
public static class ProcessorPowerSettings
{
    public static readonly Guid Subgroup = new("54533251-82be-4824-96c1-47b60b740d00");
    /// <summary>PROCTHROTTLEMAX: estado máximo do processador (%).</summary>
    public static readonly Guid MaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    /// <summary>PROCTHROTTLEMIN: estado mínimo do processador (%).</summary>
    public static readonly Guid MinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    /// <summary>PERFBOOSTMODE: modo de aumento de desempenho (0 desativado, 1 ativado, 2 agressivo, 3 eficiente, 4 eficiente agressivo...).</summary>
    public static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    /// <summary>CPMINCORES: mínimo de núcleos não estacionados (%).</summary>
    public static readonly Guid CoreParkingMin = new("0cc5b647-c1df-4637-891a-dec35c318583");

    public static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static string BoostModeName(uint v) => v switch
    {
        0 => "Desativado",
        1 => "Ativado",
        2 => "Agressivo",
        3 => "Ativado eficiente",
        4 => "Agressivo eficiente",
        5 => "Agressivo no garantido",
        6 => "Agressivo eficiente no garantido",
        _ => v.ToString(CultureInfo.InvariantCulture),
    };

    public static string OverlayName(Guid g) =>
        g == OverlayBestEfficiency ? "Melhor eficiência de energia" :
        g == OverlayBestPerformance ? "Melhor desempenho" :
        g == Guid.Empty ? "Equilibrado" : "Personalizado (" + g.ToString("D") + ")";
}

/// <summary>O que o Windows diz sobre energia e frequência do processador. Null = não foi possível ler (motivo em <see cref="Notes"/>).</summary>
public sealed record CpuPowerState
{
    /// <summary>~MHz do registro (HARDWARE\DESCRIPTION\System\CentralProcessor\0): a frequência base anunciada.</summary>
    public int? RegistryMhz { get; init; }
    /// <summary>CallNtPowerInformation(ProcessorInformation): MaxMhz (base informada pelo firmware/driver).</summary>
    public int? NtMaxMhz { get; init; }
    /// <summary>Menor MhzLimit entre os processadores: abaixo de NtMaxMhz = o Windows está limitando (térmico/energia).</summary>
    public int? NtMhzLimit { get; init; }
    public int? NtCurrentMhz { get; init; }
    public Guid? ActiveScheme { get; init; }
    public uint? MaxStateAc { get; init; }
    public uint? MaxStateDc { get; init; }
    public uint? MinStateAc { get; init; }
    public uint? BoostModeAc { get; init; }
    public uint? BoostModeDc { get; init; }
    public uint? CoreParkingMinAc { get; init; }
    public Guid? PowerModeOverlay { get; init; }
    public bool? HasBattery { get; init; }
    public bool? OnBattery { get; init; }
    public bool? BatterySaver { get; init; }
    /// <summary>Integridade de memória (HVCI) em execução, pela API do kernel (estado efetivo, não só a configuração).</summary>
    public bool? HvciRunning { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public static CpuPowerState Empty(string reason) => new() { Notes = [reason] };
}

public interface ICpuPowerReader
{
    CpuPowerState Read();
}

/// <summary>Leitura das APIs de energia do Windows. Nada aqui escreve: correções vão pelo aplicador (catálogo cpulab).</summary>
public static class CpuPowerReader
{
    public static ICpuPowerReader Create() =>
        OperatingSystem.IsWindows() ? new WindowsCpuPowerReader() : new FixedPowerReader(CpuPowerState.Empty("APIs de energia do Windows indisponíveis neste sistema."));
}

public sealed class FixedPowerReader(CpuPowerState state) : ICpuPowerReader
{
    public CpuPowerState Read() => state;
}

[SupportedOSPlatform("windows")]
public sealed partial class WindowsCpuPowerReader : ICpuPowerReader
{
    public CpuPowerState Read()
    {
        var notes = new List<string>();
        int? regMhz = null, maxMhz = null, limit = null, current = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz) regMhz = mhz;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { notes.Add("~MHz: " + ex.Message); }

        try
        {
            var n = Environment.ProcessorCount;
            var buf = new byte[n * 24];
            var status = CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)buf.Length);
            if (status == 0)
            {
                var max = new List<int>(); var lim = new List<int>(); var cur = new List<int>();
                for (var i = 0; i < n; i++)
                {
                    max.Add(BitConverter.ToInt32(buf, i * 24 + 4));
                    cur.Add(BitConverter.ToInt32(buf, i * 24 + 8));
                    lim.Add(BitConverter.ToInt32(buf, i * 24 + 12));
                }
                maxMhz = max.Max(); limit = lim.Min(); current = (int)cur.Average();
            }
            else notes.Add($"CallNtPowerInformation: NTSTATUS 0x{status:X8}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { notes.Add("CallNtPowerInformation: " + ex.Message); }

        Guid? scheme = null;
        uint? maxAc = null, maxDc = null, minAc = null, boostAc = null, boostDc = null, parkAc = null;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) == 0)
            {
                try { scheme = Marshal.PtrToStructure<Guid>(ptr); }
                finally { LocalFree(ptr); }
            }
            if (scheme is { } s)
            {
                maxAc = ReadAc(s, ProcessorPowerSettings.MaxState);
                maxDc = ReadDc(s, ProcessorPowerSettings.MaxState);
                minAc = ReadAc(s, ProcessorPowerSettings.MinState);
                boostAc = ReadAc(s, ProcessorPowerSettings.BoostMode);
                boostDc = ReadDc(s, ProcessorPowerSettings.BoostMode);
                parkAc = ReadAc(s, ProcessorPowerSettings.CoreParkingMin);
            }
            else notes.Add("Plano de energia ativo: não foi possível ler.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { notes.Add("powrprof: " + ex.Message); }

        Guid? overlay = null;
        try
        {
            Guid g;
            unsafe { if (PowerGetEffectiveOverlayScheme(&g) == 0) overlay = g; }
        }
        catch (EntryPointNotFoundException) { notes.Add("Modo de energia: API indisponível nesta versão do Windows."); }

        bool? hasBattery = null, onBattery = null, saver = null;
        if (GetSystemPowerStatus(out var ps))
        {
            onBattery = ps.ACLineStatus switch { 0 => true, 1 => false, _ => null };
            hasBattery = (ps.BatteryFlag & 128) == 0 && ps.BatteryFlag != 255;
            saver = ps.SystemStatusFlag == 1;
        }

        bool? hvci = null;
        try
        {
            var info = new byte[8];
            BitConverter.TryWriteBytes(info.AsSpan(0, 4), 8);
            if (NtQuerySystemInformation(103, info, 8, out _) == 0)
                hvci = (BitConverter.ToUInt32(info, 4) & 0x400) != 0; // CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { notes.Add("Integridade de código: " + ex.Message); }

        return new CpuPowerState
        {
            RegistryMhz = regMhz, NtMaxMhz = maxMhz, NtMhzLimit = limit, NtCurrentMhz = current, ActiveScheme = scheme,
            MaxStateAc = maxAc, MaxStateDc = maxDc, MinStateAc = minAc, BoostModeAc = boostAc, BoostModeDc = boostDc, CoreParkingMinAc = parkAc,
            PowerModeOverlay = overlay, HasBattery = hasBattery, OnBattery = onBattery, BatterySaver = saver, HvciRunning = hvci, Notes = notes,
        };
    }

    private static unsafe uint? ReadAc(Guid scheme, Guid setting)
    {
        var sub = ProcessorPowerSettings.Subgroup;
        uint v;
        return PowerReadACValueIndex(IntPtr.Zero, &scheme, &sub, &setting, &v) == 0 ? v : null;
    }

    private static unsafe uint? ReadDc(Guid scheme, Guid setting)
    {
        var sub = ProcessorPowerSettings.Subgroup;
        uint v;
        return PowerReadDCValueIndex(IntPtr.Zero, &scheme, &sub, &setting, &v) == 0 ? v : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("powrprof.dll")] private static partial uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, [Out] byte[] output, uint outputLength);
    [LibraryImport("powrprof.dll")] private static partial uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerReadACValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerReadDCValueIndex(IntPtr root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);
    [LibraryImport("powrprof.dll")] private static unsafe partial uint PowerGetEffectiveOverlayScheme(Guid* overlay);
    [LibraryImport("kernel32.dll")] private static partial IntPtr LocalFree(IntPtr mem);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
    [LibraryImport("ntdll.dll")] private static partial int NtQuerySystemInformation(int infoClass, [In, Out] byte[] info, int length, out int returned);
}
