using System.Buffers.Binary;
using System.Text;

namespace Nexus.Boot.Inventory;

/// <summary>
/// Leitor mínimo do formato de atalho (.lnk, [MS-SHLLINK]): só o caminho local do alvo (LinkInfo.LocalBasePath, preferindo a
/// versão Unicode) e, se não houver, a string de ambiente (ExtraData EnvironmentVariableDataBlock). Lógica pura, sem COM.
/// Fonte: learn.microsoft.com/openspecs/windows_protocols/ms-shllink.
/// </summary>
public static class ShellLink
{
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8, HasWorkingDir = 0x10,
        HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80;

    /// <summary>Caminho do alvo, ou null se o atalho não aponta para um arquivo local legível.</summary>
    public static string? TargetPath(ReadOnlySpan<byte> data)
    {
        try { return Parse(data); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    private static string? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(d) != HeaderSize) return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(d[0x14..]);
        var pos = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0)
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);

        string? local = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var info = d[pos..];
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
            if ((infoFlags & 0x1) != 0)
            {
                if (headerSize >= 0x24)
                {
                    var uni = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x1C..]);
                    if (uni > 0) local = Utf16Z(info[uni..size]);
                }
                if (string.IsNullOrEmpty(local))
                {
                    var ansi = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[0x10..]);
                    if (ansi > 0) local = AnsiZ(info[ansi..size]);
                }
            }
            pos += size;
        }
        if (!string.IsNullOrEmpty(local)) return local;

        // StringData (opcional) e depois ExtraData: procura o bloco de variáveis de ambiente (assinatura 0xA0000001).
        var unicode = (flags & IsUnicode) != 0;
        foreach (var f in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation })
        {
            if ((flags & f) == 0) continue;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(d[pos..]);
            pos += 2 + count * (unicode ? 2 : 1);
        }
        while (pos + 8 <= d.Length)
        {
            var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
            if (blockSize < 8) break;
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
            if (signature == 0xA0000001 && blockSize >= 0x314)
            {
                var w = Utf16Z(d.Slice(pos + 0x108, 520));
                return string.IsNullOrEmpty(w) ? AnsiZ(d.Slice(pos + 8, 260)) : w;
            }
            pos += blockSize;
        }
        return null;
    }

    private static string Utf16Z(ReadOnlySpan<byte> s)
    {
        var chars = new StringBuilder();
        for (var i = 0; i + 1 < s.Length; i += 2)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(s[i..]);
            if (c == '\0') break;
            chars.Append(c);
        }
        return chars.ToString();
    }

    private static string AnsiZ(ReadOnlySpan<byte> s)
    {
        var end = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? s : s[..end]);
    }
}
