using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Measurement.Windows;

/// <summary>
/// Raw Input do mouse para o teste de taxa (WM_INPUT, https://learn.microsoft.com/en-us/windows/win32/inputdev/raw-input).
/// Registra só a janela do teste, só enquanto ela está em primeiro plano (sem RIDEV_INPUTSINK): nada é lido fora dela e nada é
/// mudado no mouse. Só diagnóstico.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe partial class RawMouseInput
{
    public const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003, RimTypeMouse = 0, RidevRemove = 0x1;
    private const ushort UsagePageGeneric = 0x01, UsageMouse = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    public static bool Register(IntPtr hwnd)
    {
        var d = new RawInputDevice { UsagePage = UsagePageGeneric, Usage = UsageMouse, Flags = 0, Target = hwnd };
        return RegisterRawInputDevices(&d, 1, (uint)sizeof(RawInputDevice));
    }

    public static void Unregister()
    {
        var d = new RawInputDevice { UsagePage = UsagePageGeneric, Usage = UsageMouse, Flags = RidevRemove, Target = IntPtr.Zero };
        RegisterRawInputDevices(&d, 1, (uint)sizeof(RawInputDevice));
    }

    /// <summary>O WM_INPUT (lParam = HRAWINPUT) é um relatório do mouse com movimento?</summary>
    public static bool IsMouseMove(IntPtr rawInput)
    {
        var headerSize = (uint)(sizeof(uint) * 2 + IntPtr.Size * 2);
        var buffer = stackalloc byte[128];
        var size = 128u;
        var read = GetRawInputData(rawInput, RidInput, buffer, ref size, headerSize);
        if (read == uint.MaxValue || read < headerSize + 20) return false;
        if (*(uint*)buffer != RimTypeMouse) return false;
        var mouse = buffer + headerSize;
        var x = *(int*)(mouse + 12);
        var y = *(int*)(mouse + 16);
        return x != 0 || y != 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(RawInputDevice* devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static partial uint GetRawInputData(IntPtr rawInput, uint command, byte* data, ref uint size, uint headerSize);
}
