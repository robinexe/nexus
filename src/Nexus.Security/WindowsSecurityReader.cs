using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Nexus.WindowsActions.Registry;

namespace Nexus.Security;

/// <summary>
/// Leitura SOMENTE LEITURA do estado de segurança deste PC: WMI/CIM via um script do PowerShell da própria Microsoft (sem
/// pacote extra; o script só lê e devolve JSON), registro e arquivos. Não escreve nada. Cada parte falha sozinha sem
/// derrubar o resto (o que não foi lido aparece como "não lido").
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsSecurityReader
{
    /// <summary>Script de leitura. Datas em ISO 8601 (o ConvertTo-Json do Windows PowerShell 5.1 usaria "/Date()/").</summary>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        function D($d) { if ($d) { try { ([datetime]$d).ToUniversalTime().ToString('o') } catch { $null } } }
        $r = @{}
        $def = 'root/Microsoft/Windows/Defender'
        try { $s = Get-CimInstance -Namespace $def -ClassName MSFT_MpComputerStatus
              $r.status = [ordered]@{ AMServiceEnabled=$s.AMServiceEnabled; AntivirusEnabled=$s.AntivirusEnabled; RealTimeProtectionEnabled=$s.RealTimeProtectionEnabled;
                BehaviorMonitorEnabled=$s.BehaviorMonitorEnabled; IsTamperProtected=$s.IsTamperProtected; AMRunningMode=[string]$s.AMRunningMode; AMProductVersion=$s.AMProductVersion;
                AntivirusSignatureVersion=$s.AntivirusSignatureVersion; AntivirusSignatureAge=$s.AntivirusSignatureAge; AntivirusSignatureLastUpdated=(D $s.AntivirusSignatureLastUpdated);
                QuickScanEndTime=(D $s.QuickScanEndTime); QuickScanAge=$s.QuickScanAge; FullScanEndTime=(D $s.FullScanEndTime); FullScanAge=$s.FullScanAge } } catch { $r.statusError = 'Defender: ' + $_.Exception.Message }
        try { $p = Get-CimInstance -Namespace $def -ClassName MSFT_MpPreference
              $r.pref = [ordered]@{ MAPSReporting=$p.MAPSReporting; EnableControlledFolderAccess=$p.EnableControlledFolderAccess; PUAProtection=$p.PUAProtection;
                ExclusionPath=@($p.ExclusionPath); ExclusionExtension=@($p.ExclusionExtension); ExclusionProcess=@($p.ExclusionProcess) } } catch { $r.prefError = 'Preferências do Defender: ' + $_.Exception.Message }
        try { $r.av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object { [ordered]@{ displayName=$_.displayName; productState=$_.productState } }) } catch { $r.avError = 'Central de Segurança: ' + $_.Exception.Message }
        try { $r.fw = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct | ForEach-Object { [ordered]@{ displayName=$_.displayName; productState=$_.productState } }) } catch { }
        try { $r.fwProfiles = @(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetFirewallProfile | ForEach-Object { [ordered]@{ Name=[string]$_.Name; Enabled=($_.Enabled -eq 1) } }) } catch { }
        try { $r.threats = @(Get-CimInstance -Namespace $def -ClassName MSFT_MpThreat | Select-Object -First 50 | ForEach-Object { [ordered]@{ ThreatID=[string]$_.ThreatID; ThreatName=$_.ThreatName; SeverityID=[int]$_.SeverityID; IsActive=[bool]$_.IsActive } }) } catch { $r.threatError = 'Histórico de ameaças: ' + $_.Exception.Message }
        try { $r.detections = @(Get-CimInstance -Namespace $def -ClassName MSFT_MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 50 | ForEach-Object { [ordered]@{ ThreatID=[string]$_.ThreatID; InitialDetectionTime=(D $_.InitialDetectionTime); ThreatStatusID=[int]$_.ThreatStatusID; Resources=@($_.Resources) } }) } catch { }
        $bl = @()
        try { $bl = @(Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume | Where-Object { $_.DriveLetter } | ForEach-Object { [ordered]@{ Drive=$_.DriveLetter; Protection=$(if ($_.ProtectionStatus -eq 1) { 1 } elseif ($_.ProtectionStatus -eq 0) { 2 } else { $null }); Source='WMI' } }) } catch { }
        if ($bl.Count -eq 0) { try { $sh = New-Object -ComObject Shell.Application
              $bl = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { $v = $sh.NameSpace($_.DeviceID + '\').Self.ExtendedProperty('System.Volume.BitLockerProtection'); [ordered]@{ Drive=$_.DeviceID; Protection=$(if ($v) { [int]$v } else { $null }); Source='Shell' } }) } catch { } }
        $r.bitlocker = $bl
        try { $t = Get-CimInstance -Namespace root/cimv2/Security/MicrosoftTpm -ClassName Win32_Tpm
              if ($t) { $r.tpm = [ordered]@{ Present=$true; Enabled=[bool]$t.IsEnabled_InitialValue; Version=([string]$t.SpecVersion).Split(',')[0] } } } catch { }
        if (-not $r.tpm) { try { $pnp = @(Get-CimInstance Win32_PnPEntity -Filter "PNPClass='SecurityDevices'" | Where-Object { $_.Name -match 'Trusted Platform Module|TPM' })
              $r.tpm = [ordered]@{ Present=($pnp.Count -gt 0); Enabled=$null; Version=$(if ($pnp.Count -gt 0 -and $pnp[0].Name -match '(\d\.\d)') { $Matches[1] } else { $null }) } } catch { } }
        try { $r.tasks = @(Get-ScheduledTask | ForEach-Object { $task = $_; foreach ($a in @($task.Actions)) { if ($a.Execute) { [ordered]@{ Path=($task.TaskPath + $task.TaskName); Execute=$a.Execute; Arguments=$a.Arguments; State=[string]$task.State; Author=$task.Author } } } }) } catch { }
        try { $ws = New-Object -ComObject WScript.Shell
              $dirs = @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup'))
              $r.startup = @(foreach ($d in $dirs) { if ($d -and (Test-Path -LiteralPath $d)) { Get-ChildItem -LiteralPath $d -File | Where-Object { $_.Name -ne 'desktop.ini' } | ForEach-Object {
                if ($_.Extension -eq '.lnk') { $l = $ws.CreateShortcut($_.FullName); [ordered]@{ Location=$d; Name=$_.Name; Target=$l.TargetPath; Arguments=$l.Arguments } }
                else { [ordered]@{ Location=$d; Name=$_.Name; Target=$_.FullName; Arguments='' } } } } }) } catch { }
        $r | ConvertTo-Json -Depth 5 -Compress
        """;

    public sealed record StartupItem(string Location, string Name, string? Target, string? Arguments);

    /// <summary>Roda o script de leitura (powershell.exe da Microsoft, oculto, sem perfil) e devolve o JSON. Null se falhar.</summary>
    public static string? RunReadScript(TimeSpan timeout, out string? error)
    {
        error = null;
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe)) { error = "powershell.exe não encontrado."; return null; }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // -EncodedCommand (UTF-16LE em Base64): o script vai inteiro, sem problemas de aspas nem de leitura linha a linha do stdin.
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) { error = "Não foi possível iniciar o PowerShell."; return null; }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            error = "A leitura demorou demais e foi interrompida.";
            return null;
        }
        var text = stdout.GetAwaiter().GetResult();
        var start = text.IndexOf('{', StringComparison.Ordinal);
        if (start < 0) { error = Text.Clip(stderr.GetAwaiter().GetResult(), 200); return null; }
        return text[start..].Trim();
    }

    private static object? Reg(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(path, false);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static bool KeyExists(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(path, false);
            return k is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    private static int? AsInt(object? o) => o switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var i) => i, _ => null };

    public static RegistrySecurityFlags ReadFlags()
    {
        const string sys = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        var lua = AsInt(Reg(RegistryHive.LocalMachine, RegistryView.Registry64, sys, "EnableLUA"));
        var consent = AsInt(Reg(RegistryHive.LocalMachine, RegistryView.Registry64, sys, "ConsentPromptBehaviorAdmin"));
        var smart = Reg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled") as string;
        var smartPolicy = AsInt(Reg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen"));
        var secureBoot = AsInt(Reg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled"));
        var reboot = KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
                     || KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        var profiles = new List<FirewallProfileState>();
        foreach (var (key, name) in new[] { ("DomainProfile", "Domain"), ("StandardProfile", "Private"), ("PublicProfile", "Public") })
            if (AsInt(Reg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + key, "EnableFirewall")) is { } v)
                profiles.Add(new FirewallProfileState(name, v != 0));
        return new RegistrySecurityFlags(lua is null ? null : lua != 0, consent, smart, smartPolicy == 0 ? true : null, secureBoot is null ? null : secureBoot != 0, reboot, profiles);
    }

    /// <summary>Leitura completa: placar, pontos suspeitos e histórico de ameaças.</summary>
    public static SecurityReport Read(CancellationToken cancel = default)
    {
        var notes = new List<string>();
        WmiSecurityData? wmi = null;
        List<StartupItem> startup = [];
        var json = RunReadScript(TimeSpan.FromSeconds(90), out var scriptError);
        if (json is not null)
        {
            try
            {
                wmi = SecurityStatus.Parse(json);
                notes.AddRange(wmi.Errors);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("startup", out var st))
                {
                    var items = st.ValueKind == JsonValueKind.Array ? st.EnumerateArray().ToList() : st.ValueKind == JsonValueKind.Object ? [st] : [];
                    startup = items.Select(x => new StartupItem(S(x, "Location") ?? "", S(x, "Name") ?? "", S(x, "Target"), S(x, "Arguments"))).ToList();
                }
            }
            catch (JsonException ex) { notes.Add("Leitura do Defender ilegível: " + ex.Message); }
        }
        else notes.Add("Leitura do Defender/WMI falhou: " + scriptError);
        cancel.ThrowIfCancellationRequested();

        var flags = ReadFlags();
        var protection = SecurityStatus.Scoreboard(wmi, flags, DateTimeOffset.Now);
        var findings = new List<Finding>();
        var reg = new WindowsSecurityRegistry();

        void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { notes.Add($"{what}: {ex.Message}"); }
        }

        Safe("Inicialização", () => findings.AddRange(Heuristics.Autoruns(Autoruns(reg, startup))));
        Safe("Tarefas agendadas", () => { if (wmi is not null) findings.AddRange(Heuristics.Tasks(wmi.Tasks)); });
        Safe("Arquivo hosts", () =>
        {
            var store = new SystemHostsStore();
            var bytes = store.Read();
            if (bytes is null) return;
            var (text, _) = HostsFileHandler.Decode(bytes);
            var entries = HostsFile.Parse(text);
            findings.AddRange(HostsFile.ToFindings(HostsFile.Analyze(entries), entries.Count, Heuristics.HostsFixAction));
            var dbPath = Reg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DataBasePath") as string;
            if (dbPath is not null && !dbPath.TrimEnd('\\').EndsWith(@"\System32\drivers\etc", StringComparison.OrdinalIgnoreCase))
                findings.Add(new Finding("Arquivo hosts", Severity.High, "O Windows foi apontado para outro arquivo hosts", dbPath,
                    "DataBasePath define a pasta do hosts. Mudá-la é um truque para esconder um hosts falso.",
                    @"Regedit: HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters (valor DataBasePath; padrão %SystemRoot%\System32\drivers\etc).",
                    "Rode verificação completa do Defender; o NEXUS não altera este valor (só o hosts padrão)."));
        });
        Safe("Proxy", () =>
        {
            const string inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
            var enabled = AsInt(Reg(RegistryHive.CurrentUser, RegistryView.Default, inet, "ProxyEnable")) == 1;
            var server = Reg(RegistryHive.CurrentUser, RegistryView.Default, inet, "ProxyServer") as string;
            var pac = Reg(RegistryHive.CurrentUser, RegistryView.Default, inet, "AutoConfigURL") as string;
            var winhttp = Heuristics.ParseWinHttpSettings(Reg(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections", "WinHttpSettings") as byte[]);
            findings.AddRange(Heuristics.Proxy(new ProxyInfo(enabled, server, pac, winhttp)));
        });
        Safe("Extensões do navegador", () => findings.AddRange(Heuristics.ForcedExtensions(ForcedExtensions(reg))));
        Safe("Exclusões do Defender", () => findings.AddRange(Heuristics.Exclusions(wmi?.Exclusions)));
        Safe("IFEO", () => findings.AddRange(Heuristics.Ifeo(PersistenceValueHandler.IfeoDebuggers(reg)
            .GroupBy(x => x.Candidate.Selection).Select(g => g.First())
            .Select(x => new IfeoEntry(x.Candidate.Key.View == RegView.Registry32 ? "32 bits" : "64 bits", x.Image, x.Debugger, x.Candidate.Selection)))));
        Safe("AppInit_DLLs", () =>
        {
            const string win = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
            var items = new[] { (RegistryView.Registry64, "64 bits"), (RegistryView.Registry32, "32 bits") }.Select(v =>
                new AppInitInfo(v.Item2, Reg(RegistryHive.LocalMachine, v.Item1, win, "AppInit_DLLs") as string ?? "",
                    AsInt(Reg(RegistryHive.LocalMachine, v.Item1, win, "LoadAppInit_DLLs")) == 1,
                    AsInt(Reg(RegistryHive.LocalMachine, v.Item1, win, "RequireSignedAppInit_DLLs")) is { } rs ? rs == 1 : null));
            findings.AddRange(Heuristics.AppInit(items, flags.SecureBoot == true));
        });
        Safe("Winlogon", () =>
        {
            const string wl = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            var userShell = PersistenceValueHandler.UserShell(reg, null);
            findings.AddRange(Heuristics.Winlogon(new WinlogonInfo(
                Reg(RegistryHive.LocalMachine, RegistryView.Registry64, wl, "Shell") as string,
                Reg(RegistryHive.LocalMachine, RegistryView.Registry64, wl, "Userinit") as string,
                userShell is null ? null : reg.Value(userShell.Key, "Shell")?.Data, userShell?.Selection, Environment.SystemDirectory)));
        });

        return new SecurityReport(protection, Heuristics.Sort(findings), wmi?.Threats ?? [], notes, DateTimeOffset.Now);
    }

    private static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<AutorunEntry> Autoruns(WindowsSecurityRegistry reg, IEnumerable<StartupItem> startup)
    {
        var list = new List<AutorunEntry>();
        var keys = new (RegKeyRef Key, string Label)[]
        {
            (new(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Run", RegView.Default), @"HKCU\...\Run"),
            (new(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", RegView.Default), @"HKCU\...\RunOnce"),
            (new(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegView.Registry64), @"HKLM\...\Run"),
            (new(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", RegView.Registry64), @"HKLM\...\RunOnce"),
            (new(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", RegView.Registry32), @"HKLM\...\Run (32 bits)"),
            (new(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", RegView.Registry64), @"HKLM\...\Policies\Explorer\Run"),
            (new(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", RegView.Default), @"HKCU\...\Policies\Explorer\Run"),
        };
        foreach (var (key, label) in keys)
            foreach (var v in reg.Values(key))
            {
                if (v.Kind is not ("String" or "ExpandString") || string.IsNullOrWhiteSpace(v.Data)) continue;
                list.Add(Entry(label, v.Name, v.Data));
            }
        foreach (var s in startup)
        {
            if (string.IsNullOrWhiteSpace(s.Target)) continue;
            var cmd = string.IsNullOrWhiteSpace(s.Arguments) ? $"\"{s.Target}\"" : $"\"{s.Target}\" {s.Arguments}";
            list.Add(Entry("Pasta Inicializar", s.Name, cmd));
        }
        return list;
    }

    private static AutorunEntry Entry(string location, string name, string command)
    {
        var raw = CommandHeuristics.ExecutablePath(command);
        var expanded = raw is null ? null : Environment.ExpandEnvironmentVariables(raw);
        var rooted = expanded is { Length: > 3 } && expanded[1] == ':';
        var exists = rooted && File.Exists(expanded);
        var zone = CommandHeuristics.Zone(expanded);
        // Assinatura só onde importa (pastas de usuário/temporárias): é a parte cara da leitura.
        bool? signed = exists && zone is PathZone.Temp or PathZone.UserAppData or PathZone.Downloads or PathZone.Public or PathZone.ProgramDataRoot
            ? Authenticode.IsSigned(expanded!) : null;
        return new AutorunEntry(location, name, command, rooted ? expanded : null, exists, signed);
    }

    private static List<ForcedExtension> ForcedExtensions(WindowsSecurityRegistry reg)
    {
        var list = new List<ForcedExtension>();
        var browsers = new (string Name, string Path)[]
        {
            ("Chrome", @"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"),
            ("Edge", @"SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"),
            ("Brave", @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist"),
            ("Chromium", @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"),
            ("Opera", @"SOFTWARE\Policies\Opera Software\Opera\ExtensionInstallForcelist"),
        };
        foreach (var (name, path) in browsers)
            foreach (var hive in new[] { RegHive.HKLM, RegHive.HKCU })
                foreach (var v in reg.Values(new RegKeyRef(hive, path, hive == RegHive.HKLM ? RegView.Registry64 : RegView.Default)))
                    if (v.Kind is "String" or "ExpandString" && !string.IsNullOrWhiteSpace(v.Data))
                        list.Add(Heuristics.ParseForcelistValue(name, hive.ToString(), v.Data));
        // Firefox: política "Extensions/Install" (lista de URLs/caminhos).
        foreach (var hive in new[] { RegHive.HKLM, RegHive.HKCU })
            foreach (var v in reg.Values(new RegKeyRef(hive, @"SOFTWARE\Policies\Mozilla\Firefox\Extensions\Install", hive == RegHive.HKLM ? RegView.Registry64 : RegView.Default)))
                if (!string.IsNullOrWhiteSpace(v.Data))
                    list.Add(new ForcedExtension("Firefox", hive.ToString(), v.Data, v.Data.Contains("addons.mozilla.org", StringComparison.OrdinalIgnoreCase) ? "addons.mozilla.org" : v.Data));
        return list;
    }
}
