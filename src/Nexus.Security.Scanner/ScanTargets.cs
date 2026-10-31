using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nexus.Security.Scanner;

/// <summary>O que cada modo verifica (pastas reais do usuário que usa a interface). Só leitura.</summary>
[SupportedOSPlatform("windows")]
public static partial class ScanTargets
{
    /// <summary>
    /// Verificação rápida: pastas de risco (Downloads, Área de Trabalho, Temp, AppData, Pública), pastas Inicializar e os
    /// executáveis apontados pelas chaves Run/RunOnce (HKCU e HKLM, 64 e 32 bits).
    /// </summary>
    public static IReadOnlyList<string> Quick()
    {
        var list = new List<string>();
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p) && p.Length > 3 && (Directory.Exists(p) || File.Exists(p))) list.Add(p); }
        Add(DownloadsFolder());
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Path.GetTempPath());
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        var pub = Environment.GetEnvironmentVariable("PUBLIC");
        Add(pub);
        foreach (var exe in RunKeyExecutables()) Add(exe);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Unidades fixas prontas (para a verificação completa).</summary>
    public static IReadOnlyList<string> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

    /// <summary>Pastas nunca percorridas: a quarentena/dados do NEXUS (não reverificar o que já foi isolado).</summary>
    public static IReadOnlyList<string> DefaultExclusions() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "quarantine"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NEXUS", "quarantine"),
    ];

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Pasta Downloads real (pode ter sido movida para outro disco), pela API de pastas conhecidas.</summary>
    public static string? DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr); }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static List<string> RunKeyExecutables()
    {
        var found = new List<string>();
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(sub);
                    if (key is null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        if (key.GetValue(name) is not string command) continue;
                        var path = CommandHeuristics.ExecutablePath(Environment.ExpandEnvironmentVariables(command));
                        if (path is not null && Path.IsPathFullyQualified(path)) found.Add(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return found;
    }
}

/// <summary>Ações da interface que só ABREM telas do Windows para o usuário ver (nada é alterado).</summary>
[SupportedOSPlatform("windows")]
public static partial class ShellViews
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);

    private const uint ShopFilePath = 0x2;

    /// <summary>Abre o Explorer com o arquivo selecionado.</summary>
    public static bool ShowInExplorer(string path)
    {
        if (!IsPlainLocalPath(path) || !File.Exists(path)) return false;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var p = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Abre a janela Propriedades do arquivo (com a aba "Assinaturas Digitais" quando houver assinatura).</summary>
    public static bool ShowProperties(string path, string? page = null)
    {
        if (!IsPlainLocalPath(path) || !File.Exists(path)) return false;
        try { return SHObjectProperties(IntPtr.Zero, ShopFilePath, path, page); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Caminho absoluto comum (sem aspas, sem quebra de linha, sem fluxo alternativo).</summary>
    public static bool IsPlainLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 32000 && Path.IsPathFullyQualified(path) &&
        path.IndexOfAny(['"', '\0', '\n', '\r', '*', '?', '<', '>', '|']) < 0 && path.LastIndexOf(':') <= 1;
}
