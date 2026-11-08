using System.Runtime.Versioning;

namespace Nexus.DriverUpdates;

/// <summary>Assunto de um driver, para o "o que fazer" do Atraso de drivers (sem prometer nada).</summary>
public enum DriverCategory { Unknown, Network, Audio, Storage, Video, Usb, Kernel }

/// <summary>O que o NEXUS sabe de um arquivo .sys: o dispositivo (pelo inventário de drivers), a classe, o fabricante e o assunto.</summary>
/// <param name="Device">Nome do dispositivo que usa este driver (ex.: "Intel(R) Wi-Fi 6 AX201"), ou null quando nenhum dispositivo aponta para ele.</param>
/// <param name="FileDescription">Descrição gravada no próprio arquivo (ex.: "NDIS 6.x wrapper driver"), quando foi possível ler.</param>
public sealed record DriverModuleInfo(string Module, string? Device, string? ClassName, string? Provider, string? FileDescription, DriverCategory Category);

/// <summary>
/// Liga um arquivo .sys ao dispositivo: dispositivo → serviço (SPDRP_SERVICE) → ImagePath do serviço (HKLM\SYSTEM\CurrentControlSet\Services)
/// → nome do arquivo. Sem ImagePath, o Windows usa <c>System32\drivers\&lt;serviço&gt;.sys</c>, então o nome do serviço também vale.
/// Lógica pura (testada); a leitura do registro e dos arquivos fica em <see cref="WindowsDriverModuleSource"/>.
/// </summary>
public sealed class DriverModuleLookup
{
    private readonly Dictionary<string, DeviceDriver> _byService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serviceByModule = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _fileDescription;

    /// <param name="serviceImagePaths">serviço → ImagePath (qualquer formato: "\SystemRoot\System32\drivers\x.sys", "System32\drivers\x.sys", "\??\C:\...\x.sys").</param>
    /// <param name="fileDescription">Descrição do arquivo pelo nome do módulo (null quando não dá para ler).</param>
    public DriverModuleLookup(IEnumerable<DeviceDriver> devices, IReadOnlyDictionary<string, string> serviceImagePaths, Func<string, string?>? fileDescription = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(serviceImagePaths);
        _fileDescription = fileDescription ?? (_ => null);
        foreach (var d in devices)
            if (!string.IsNullOrWhiteSpace(d.Service) && !_byService.ContainsKey(d.Service)) _byService[d.Service] = d;
        foreach (var (service, image) in serviceImagePaths)
        {
            var file = FileName(image);
            if (file.Length > 0 && !_serviceByModule.ContainsKey(file)) _serviceByModule[file] = service;
        }
    }

    /// <summary>"\SystemRoot\System32\drivers\ndis.sys" → "ndis.sys".</summary>
    public static string FileName(string? path)
    {
        var s = (path ?? "").Trim().Trim('"');
        var cut = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        return (cut >= 0 ? s[(cut + 1)..] : s).Trim();
    }

    public DriverModuleInfo Describe(string module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var name = FileName(module);
        DeviceDriver? device = null;
        if (_serviceByModule.TryGetValue(name, out var service)) _byService.TryGetValue(service, out device);
        if (device is null && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) _byService.TryGetValue(name[..^4], out device);
        string? description = null;
        try { description = _fileDescription(name); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new DriverModuleInfo(name, device?.Description, device?.ClassName, device?.DriverProvider ?? device?.Manufacturer, description,
            CategoryOf(name, device?.ClassName));
    }

    /// <summary>Assunto pela classe do dispositivo (Net, Media, Display…) ou, sem dispositivo, por nomes de arquivo bem conhecidos.</summary>
    public static DriverCategory CategoryOf(string module, string? className)
    {
        var n = FileName(module).ToLowerInvariant();
        if (n.StartsWith("ntoskrnl", StringComparison.Ordinal) || n.StartsWith("ntkrnl", StringComparison.Ordinal) || n.StartsWith("ntkrla57", StringComparison.Ordinal) || n == "hal.dll")
            return DriverCategory.Kernel;
        switch ((className ?? "").ToLowerInvariant())
        {
            case "net": return DriverCategory.Network;
            case "media": case "audioendpoint": case "audioprocessingobject": return DriverCategory.Audio;
            case "display": return DriverCategory.Video;
            case "scsiadapter": case "hdc": case "diskdrive": return DriverCategory.Storage;
            case "usb": return DriverCategory.Usb;
        }
        if (n is "ndis.sys" or "tcpip.sys" or "netio.sys" or "fwpkclnt.sys" or "wfplwfs.sys" or "vwififlt.sys" || n.StartsWith("netwtw", StringComparison.Ordinal) ||
            n.StartsWith("rt640", StringComparison.Ordinal) || n.StartsWith("rtwlan", StringComparison.Ordinal) || (n.StartsWith("e1", StringComparison.Ordinal) && n.Length <= 12) ||
            n.StartsWith("athw", StringComparison.Ordinal) || n.StartsWith("qcamain", StringComparison.Ordinal) || n.StartsWith("mt7", StringComparison.Ordinal) || n.StartsWith("bcmwl", StringComparison.Ordinal))
            return DriverCategory.Network;
        if (n is "portcls.sys" or "hdaudbus.sys" or "hdaudio.sys" or "ks.sys" or "ksthunk.sys" or "usbaudio.sys" or "usbaudio2.sys" || n.StartsWith("rtkvhd", StringComparison.Ordinal) ||
            n.StartsWith("intcaudio", StringComparison.Ordinal) || n.StartsWith("intelaud", StringComparison.Ordinal))
            return DriverCategory.Audio;
        if (n is "storport.sys" or "stornvme.sys" or "storahci.sys" or "ataport.sys" or "disk.sys" or "classpnp.sys" or "ntfs.sys" or "volmgr.sys" or "volsnap.sys" ||
            n.StartsWith("iastor", StringComparison.Ordinal) || n.StartsWith("rcbottom", StringComparison.Ordinal) || n.StartsWith("amdsata", StringComparison.Ordinal) || n.StartsWith("samsung", StringComparison.Ordinal))
            return DriverCategory.Storage;
        if (n is "dxgkrnl.sys" or "dxgmms2.sys" or "nvlddmkm.sys" or "amdkmdag.sys" or "atikmdag.sys" || n.StartsWith("igdkmd", StringComparison.Ordinal) || n.StartsWith("igddim", StringComparison.Ordinal))
            return DriverCategory.Video;
        if (n is "usbxhci.sys" or "usbport.sys" or "usbhub3.sys" or "ucx01000.sys" or "usbccgp.sys" || n.StartsWith("usb", StringComparison.Ordinal))
            return DriverCategory.Usb;
        return DriverCategory.Unknown;
    }
}

/// <summary>Leitura real: inventário SetupAPI, ImagePath dos serviços de driver e a descrição gravada em cada arquivo. Só leitura, sem administrador.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsDriverModuleSource
{
    /// <summary>Serviço → ImagePath, só serviços de driver (Type 1 = kernel, 2 = sistema de arquivos, 8 = reconhecedor). Nunca lança.</summary>
    public static IReadOnlyDictionary<string, string> ServiceImagePaths()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return map;
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var k = services.OpenSubKey(name);
                    if (k is null) continue;
                    if (k.GetValue("Type") is not int type || type is not (1 or 2 or 8)) continue;
                    map[name] = k.GetValue("ImagePath") as string ?? $@"System32\drivers\{name}.sys";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return map;
    }

    /// <summary>Descrição gravada no arquivo (System32\drivers, depois System32), ou null.</summary>
    public static string? FileDescription(string module)
    {
        var name = DriverModuleLookup.FileName(module);
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal)) return null;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var dir in new[] { Path.Combine(system, "drivers"), system })
        {
            var path = Path.Combine(dir, name);
            try
            {
                if (!File.Exists(path)) continue;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var d = info.FileDescription?.Trim();
                return string.IsNullOrEmpty(d) ? info.CompanyName?.Trim() : d;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        }
        return null;
    }

    /// <summary>Monta o cruzamento completo (inventário + serviços + arquivos). Falhas de leitura viram "sem dispositivo", nunca exceção.</summary>
    public static DriverModuleLookup Build()
    {
        IReadOnlyList<DeviceDriver> devices;
        try { devices = new Windows.SetupApiDeviceInventory().List(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { devices = []; }
        return new DriverModuleLookup(devices, ServiceImagePaths(), FileDescription);
    }
}
