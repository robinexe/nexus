using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.DriverUpdates.Windows;

/// <summary>Chamadas por IDispatch (late binding) aos objetos COM do Windows Update Agent, sem biblioteca de interop.</summary>
[SupportedOSPlatform("windows")]
internal static class Com
{
    public static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false) ?? throw new PlatformNotSupportedException($"{progId} não está registrado neste Windows.");
        return Activator.CreateInstance(type) ?? throw new PlatformNotSupportedException($"Não foi possível criar {progId}.");
    }

    public static object? Get(object target, string name, params object?[] args) => Invoke(target, name, BindingFlags.GetProperty, args);
    public static void Set(object target, string name, object? value) => Invoke(target, name, BindingFlags.SetProperty, [value]);
    public static object? Call(object target, string name, params object?[] args) => Invoke(target, name, BindingFlags.InvokeMethod, args);

    private static object? Invoke(object target, string name, BindingFlags flags, object?[] args)
    {
        try { return target.GetType().InvokeMember(name, flags, null, target, args, CultureInfo.InvariantCulture); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }

    public static string? Str(object target, string name)
    {
        try { return Get(target, name) as string; }
        catch (Exception ex) when (ex is COMException or MissingMethodException or MissingMemberException) { return null; }
    }

    public static bool Bool(object target, string name)
    {
        try { return Get(target, name) is true; }
        catch (Exception ex) when (ex is COMException or MissingMethodException or MissingMemberException) { return false; }
    }

    public static int Int(object target, string name)
    {
        try { return Convert.ToInt32(Get(target, name), CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is COMException or MissingMethodException or MissingMemberException or InvalidCastException or FormatException or OverflowException) { return 0; }
    }

    public static DateTime? Date(object target, string name)
    {
        try { return Get(target, name) is DateTime d ? d : null; }
        catch (Exception ex) when (ex is COMException or MissingMethodException or MissingMemberException) { return null; }
    }

    /// <summary>IStringCollection / IUpdateCollection: Count + Item(i).</summary>
    public static IEnumerable<object> Items(object? collection)
    {
        if (collection is null) yield break;
        var count = Convert.ToInt32(Get(collection, "Count"), CultureInfo.InvariantCulture);
        for (var i = 0; i < count; i++)
            if (Get(collection, "Item", i) is { } item) yield return item;
    }

    public static IReadOnlyList<string> Strings(object target, string name)
    {
        try { return Items(Get(target, name)).OfType<string>().ToList(); }
        catch (Exception ex) when (ex is COMException or MissingMethodException or MissingMemberException) { return []; }
    }
}

/// <summary>Registro de atualização lido por inteiro do objeto COM (na thread que fez a busca).</summary>
[SupportedOSPlatform("windows")]
internal sealed class ComUpdateRecord : IWuUpdateRecord
{
    public ComUpdateRecord(object u)
    {
        Title = Com.Str(u, "Title") ?? "";
        var identity = Com.Get(u, "Identity") ?? throw new InvalidOperationException("Atualização sem identidade.");
        UpdateId = Com.Str(identity, "UpdateID") ?? "";
        Revision = Com.Int(identity, "RevisionNumber");
        Type = Com.Int(u, "Type");
        IsInstalled = Com.Bool(u, "IsInstalled");
        IsHidden = Com.Bool(u, "IsHidden");
        BrowseOnly = Com.Bool(u, "BrowseOnly");
        EulaAccepted = Com.Bool(u, "EulaAccepted");
        IsDownloaded = Com.Bool(u, "IsDownloaded");
        try { MaxDownloadSize = Convert.ToDecimal(Com.Get(u, "MaxDownloadSize"), CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException or OverflowException or MissingMemberException) { MaxDownloadSize = 0; }
        KbArticleIds = Com.Strings(u, "KBArticleIDs");
        SupportUrl = Com.Str(u, "SupportUrl");
        MoreInfoUrls = Com.Strings(u, "MoreInfoUrls");
        LastDeploymentChangeTime = Com.Date(u, "LastDeploymentChangeTime");
        DriverClass = Com.Str(u, "DriverClass");
        DriverHardwareId = Com.Str(u, "DriverHardwareID");
        DriverManufacturer = Com.Str(u, "DriverManufacturer");
        DriverModel = Com.Str(u, "DriverModel");
        DriverProvider = Com.Str(u, "DriverProvider");
        DriverVerDate = Com.Date(u, "DriverVerDate");
    }

    public string Title { get; }
    public string UpdateId { get; }
    public int Revision { get; }
    public int Type { get; }
    public bool IsInstalled { get; }
    public bool IsHidden { get; }
    public bool BrowseOnly { get; }
    public bool EulaAccepted { get; }
    public bool IsDownloaded { get; }
    public decimal MaxDownloadSize { get; }
    public IReadOnlyList<string> KbArticleIds { get; }
    public string? SupportUrl { get; }
    public IReadOnlyList<string> MoreInfoUrls { get; }
    public DateTime? LastDeploymentChangeTime { get; }
    public string? DriverClass { get; }
    public string? DriverHardwareId { get; }
    public string? DriverManufacturer { get; }
    public string? DriverModel { get; }
    public string? DriverProvider { get; }
    public DateTime? DriverVerDate { get; }
}

/// <summary>
/// Windows Update Agent (API oficial, COM "Microsoft.Update.Session"). A busca roda numa thread própria: o WUA não tem
/// cancelamento síncrono, então no tempo limite (ou ao cancelar) o NEXUS para de esperar e avisa; a busca do Windows termina
/// sozinha em segundo plano, sem efeito nenhum (é só leitura).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WuaAgent : IWindowsUpdateAgent
{
    private const string ClientId = "NEXUS";

    public WuSearchOutcome SearchDrivers(TimeSpan timeout, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested) return WuSearchOutcome.Fail(WuErrorKind.Canceled, "Busca cancelada.");
        var task = Task.Factory.StartNew(SearchCore, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            if (!task.Wait(timeout, cancel))
                return WuSearchOutcome.Fail(WuErrorKind.Timeout,
                    $"O Windows Update não respondeu em {timeout.TotalMinutes:0} min. Ele pode estar baixando outra coisa; tente de novo em alguns minutos.");
        }
        catch (OperationCanceledException) { return WuSearchOutcome.Fail(WuErrorKind.Canceled, "Busca cancelada. Nada foi alterado."); }
        return task.Result;
    }

    private static WuSearchOutcome SearchCore()
    {
        try
        {
            var session = Com.Create("Microsoft.Update.Session");
            TrySet(session, "ClientApplicationID", ClientId);
            var searcher = Com.Call(session, "CreateUpdateSearcher") ?? throw new InvalidOperationException("Sem buscador do Windows Update.");
            var result = Com.Call(searcher, "Search", WindowsUpdateQueries.DriverSearch) ?? throw new InvalidOperationException("Busca sem resultado.");
            var list = new List<WuDriverUpdate>();
            foreach (var u in Com.Items(Com.Get(result, "Updates")))
            {
                var record = new ComUpdateRecord(u);
                if (WindowsUpdateQueries.IsValidUpdateId(record.UpdateId.ToLowerInvariant()) && !record.IsInstalled)
                    list.Add(WindowsUpdateQueries.Map(record));
            }
            return WuSearchOutcome.Success(list);
        }
        catch (COMException ex)
        {
            var (kind, message) = WindowsUpdateQueries.DescribeError(ex.HResult);
            return WuSearchOutcome.Fail(kind, message, ex.HResult);
        }
        catch (PlatformNotSupportedException ex) { return WuSearchOutcome.Fail(WuErrorKind.Unsupported, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return WuSearchOutcome.Fail(WuErrorKind.Other, "Falha ao consultar o Windows Update: " + ex.Message); }
    }

    public WuInstallResult DownloadAndInstall(string updateId, int revision, TimeSpan timeout)
    {
        if (!WindowsUpdateQueries.IsValidUpdateId(updateId) || revision < 0) throw new ArgumentException("UpdateID inválido.", nameof(updateId));
        var task = Task.Factory.StartNew(() => InstallCore(updateId, revision), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (!task.Wait(timeout))
            throw new TimeoutException($"O Windows Update não terminou em {timeout.TotalMinutes:0} min; ele pode continuar em segundo plano. Confira em Configurações > Windows Update.");
        return task.GetAwaiter().GetResult();
    }

    private static WuInstallResult InstallCore(string updateId, int revision)
    {
        try
        {
            var session = Com.Create("Microsoft.Update.Session");
            TrySet(session, "ClientApplicationID", ClientId);
            var searcher = Com.Call(session, "CreateUpdateSearcher") ?? throw new InvalidOperationException("Sem buscador do Windows Update.");
            // Critério montado só com valores já validados por regex (GUID + número).
            var criteria = $"UpdateID='{updateId}' and RevisionNumber={revision.ToString(CultureInfo.InvariantCulture)}";
            var found = Com.Items(Com.Get(Com.Call(searcher, "Search", criteria)!, "Updates")).ToList();
            if (found.Count != 1) return new(4, false, 0, "A atualização não foi encontrada de novo no Windows Update.");
            var update = found[0];
            var record = new ComUpdateRecord(update);
            if (record.Type != WindowsUpdateQueries.UpdateTypeDriver) return new(4, false, 0, "O ID não é de uma atualização de driver: recusado.");
            if (record.IsInstalled) return new(4, false, 0, "A atualização já consta como instalada.");
            if (!record.EulaAccepted) return new(4, false, 0, "A atualização pede aceitar termos de licença: recusado.");

            var collection = Com.Create("Microsoft.Update.UpdateColl");
            Com.Call(collection, "Add", update);
            if (!record.IsDownloaded)
            {
                var downloader = Com.Call(session, "CreateUpdateDownloader")!;
                Com.Set(downloader, "Updates", collection);
                var d = Com.Call(downloader, "Download")!;
                var drc = Com.Int(d, "ResultCode");
                if (drc is not (2 or 3)) return new(drc, false, Com.Int(d, "HResult"), $"O download pelo Windows Update falhou (resultado {drc}).");
            }
            var installer = Com.Call(session, "CreateUpdateInstaller")!;
            if (Com.Bool(installer, "IsBusy")) return new(4, false, 0, "O Windows Update está instalando outra coisa agora. Tente de novo depois.");
            TrySet(installer, "AllowSourcePrompts", false);
            Com.Set(installer, "Updates", collection);
            var r = Com.Call(installer, "Install")!;
            var rc = Com.Int(r, "ResultCode");
            var reboot = Com.Bool(r, "RebootRequired");
            var hr = 0;
            try { hr = Com.Int(Com.Call(r, "GetUpdateResult", 0)!, "HResult"); }
            catch (COMException) { }
            return new(rc, reboot, hr, rc is 2 or 3 ? null : $"O Windows Update não instalou: {WindowsUpdateQueries.DescribeError(hr).Message}");
        }
        catch (COMException ex)
        {
            return new(4, false, ex.HResult, WindowsUpdateQueries.DescribeError(ex.HResult).Message);
        }
    }

    public bool? RebootRequired()
    {
        try { return Com.Get(Com.Create("Microsoft.Update.SystemInfo"), "RebootRequired") is true; }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or MissingMemberException) { return null; }
    }

    private static void TrySet(object target, string name, object value)
    {
        try { Com.Set(target, name, value); }
        catch (Exception ex) when (ex is COMException or MissingMemberException) { }
    }
}
