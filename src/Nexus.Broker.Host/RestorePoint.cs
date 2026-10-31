using System.Runtime.InteropServices;

namespace Nexus.Broker.Host;

/// <summary>
/// Ponto de restauração do Windows antes de mudanças de sistema (SRSetRestorePointW). Uma vez por processo do broker. Se a
/// Restauração do Sistema estiver desligada, ou o Windows recusar (limite de um por 24 h, por padrão), o lote segue: o diário do
/// NEXUS continua permitindo desfazer cada alteração.
/// </summary>
internal static class RestorePoint
{
    private static readonly Lock Gate = new();
    private static string? _done;

    public static string? CreateOncePerProcess()
    {
        lock (Gate)
        {
            if (_done is not null) return null;
            _done = Create("NEXUS: antes de aplicar ajustes");
            return _done;
        }
    }

    private static string Create(string description)
    {
        var info = new RestorePointInfo { EventType = BeginSystemChange, RestorePtType = ModifySettings, SequenceNumber = 0, Description = description };
        if (!SRSetRestorePointW(ref info, out var status))
            return status.Status switch
            {
                1058 => "Ponto de restauração não criado: a Restauração do Sistema está desligada. O diário do NEXUS continua permitindo desfazer.",
                _ => $"Ponto de restauração não criado (código {status.Status}). O diário do NEXUS continua permitindo desfazer.",
            };
        var end = new RestorePointInfo { EventType = EndSystemChange, RestorePtType = ModifySettings, SequenceNumber = status.SequenceNumber, Description = description };
        SRSetRestorePointW(ref end, out _);
        return "Ponto de restauração do Windows criado antes de aplicar.";
    }

    private const int BeginSystemChange = 100, EndSystemChange = 101, ModifySettings = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestorePointInfo
    {
        public int EventType;
        public int RestorePtType;
        public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateMgrStatus
    {
        public int Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo info, out StateMgrStatus status);
}
