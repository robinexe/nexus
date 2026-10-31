using System.Buffers.Binary;
using System.Text;

namespace Nexus.Lab.Memory.Problems;

/// <summary>Dono provável de uma tag do pool, com a fonte da informação.</summary>
public sealed record PoolTagOwner(string Tag, string Owner, string Description, string Source, bool Exact);

/// <summary>
/// Leitura de <c>SYSTEM_POOLTAG_INFORMATION</c> (NtQuerySystemInformation classe 22, a mesma do PoolMon):
/// <c>ULONG Count</c> seguido de <c>Count</c> × <c>SYSTEM_POOLTAG</c>. Em 64 bits cada item tem 40 bytes e o vetor começa no
/// deslocamento 8 (alinhamento do SIZE_T): Tag[4] @0, PagedAllocs @4, PagedFrees @8, (alinhamento) PagedUsed @16 (SIZE_T),
/// NonPagedAllocs @24, NonPagedFrees @28, NonPagedUsed @32. Em 32 bits: 28 bytes a partir do deslocamento 4.
/// </summary>
public static class PoolTagParser
{
    public static IReadOnlyList<PoolTagUsage> Parse(ReadOnlySpan<byte> buffer, bool is64Bit = true)
    {
        if (buffer.Length < 4) return [];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        int offset = is64Bit ? 8 : 4, size = is64Bit ? 40 : 28;
        var max = (buffer.Length - offset) / size;
        if (count > max) count = (uint)Math.Max(0, max);
        var list = new List<PoolTagUsage>((int)count);
        for (var i = 0; i < count; i++)
        {
            var e = buffer.Slice(offset + i * size, size);
            var tag = TagText(e[..4]);
            long pagedAllocs = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            long pagedFrees = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            long pagedUsed, npAllocs, npFrees, npUsed;
            if (is64Bit)
            {
                pagedUsed = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(e[16..]), long.MaxValue);
                npAllocs = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
                npFrees = BinaryPrimitives.ReadUInt32LittleEndian(e[28..]);
                npUsed = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(e[32..]), long.MaxValue);
            }
            else
            {
                pagedUsed = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
                npAllocs = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
                npFrees = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
                npUsed = BinaryPrimitives.ReadUInt32LittleEndian(e[24..]);
            }
            list.Add(new PoolTagUsage(tag, pagedUsed, npUsed, pagedAllocs, pagedFrees, npAllocs, npFrees));
        }
        return list;
    }

    /// <summary>Tag como texto: 4 caracteres ASCII; bytes não imprimíveis viram '?'.</summary>
    public static string TagText(ReadOnlySpan<byte> four)
    {
        Span<char> c = stackalloc char[4];
        for (var i = 0; i < 4; i++) c[i] = i < four.Length && four[i] is >= 0x20 and < 0x7F ? (char)four[i] : '?';
        return new string(c);
    }
}

/// <summary>
/// Tabela de tags conhecidas. Exatas: tags do próprio Windows listadas no <c>pooltag.txt</c> que a Microsoft distribui com as
/// Ferramentas de Depuração (WDK, pasta <c>Debuggers\x64\triage</c>). Prefixos: famílias de tags do mesmo componente (ex.:
/// "Tcp?" = tcpip.sys), marcadas como "provável". Drivers de terceiros (rede Killer/Realtek/Intel, NVIDIA, antivírus) não estão
/// no pooltag.txt: para eles o NEXUS procura a tag dentro dos arquivos .sys de <c>System32\drivers</c>, o método da própria
/// Microsoft (<c>findstr /m /l /s TAG %SystemRoot%\System32\drivers\*.sys</c>).
/// </summary>
public static class KnownPoolTags
{
    public const string PoolTagTxt = "pooltag.txt (Microsoft, Ferramentas de Depuração do Windows/WDK)";
    public const string FindstrSource = "https://learn.microsoft.com/en-us/archive/blogs/ntdebugging/troubleshooting-pool-leaks-part-2-poolmon";

    private static readonly Dictionary<string, (string Owner, string Description)> Exact = new(StringComparer.Ordinal)
    {
        ["Proc"] = ("nt!ps (núcleo)", "objetos de processo"),
        ["Thre"] = ("nt!ps (núcleo)", "objetos de thread"),
        ["File"] = ("nt!io (núcleo)", "objetos de arquivo aberto"),
        ["Toke"] = ("nt!se (núcleo)", "tokens de segurança"),
        ["Even"] = ("nt!os (núcleo)", "objetos de evento"),
        ["Irp "] = ("nt!io (núcleo)", "pacotes de E/S (IRP)"),
        ["MmSt"] = ("nt!mm (gerenciador de memória)", "PTEs de protótipo de seções (arquivos mapeados)"),
        ["MmCa"] = ("nt!mm (gerenciador de memória)", "áreas de controle de arquivos mapeados"),
        ["Ntfx"] = ("ntfs.sys", "alocações gerais do NTFS"),
        ["NtFs"] = ("ntfs.sys", "estruturas do NTFS (StrucSup.c)"),
        ["Ntff"] = ("ntfs.sys", "FCB_DATA (arquivos abertos no NTFS)"),
        ["ObNm"] = ("nt!ob (núcleo)", "nomes de objetos"),
    };

    private static readonly (string Prefix, string Owner, string Description)[] Prefixes =
    [
        ("CM", "nt!cm (registro do Windows)", "estruturas do registro"),
        ("Mm", "nt!mm (gerenciador de memória)", "gerenciador de memória"),
        ("Cc", "nt!cc (cache de arquivos)", "gerenciador de cache"),
        ("Io", "nt!io (núcleo)", "gerenciador de E/S"),
        ("Ob", "nt!ob (núcleo)", "gerenciador de objetos"),
        ("Ps", "nt!ps (núcleo)", "processos e threads"),
        ("Pp", "nt!pnp (Plug and Play)", "Plug and Play"),
        ("Etw", "nt!etw (rastreamento de eventos)", "rastreamento ETW"),
        ("Ntf", "ntfs.sys", "sistema de arquivos NTFS"),
        ("NtF", "ntfs.sys", "sistema de arquivos NTFS"),
        ("FM", "fltmgr.sys (filtros de arquivo: antivírus, backup, nuvem)", "gerenciador de minifiltros de arquivo"),
        ("ND", "ndis.sys (pilha de rede)", "NDIS: drivers de placa de rede passam por aqui"),
        ("Tcp", "tcpip.sys (pilha TCP/IP)", "TCP/IP"),
        ("Afd", "afd.sys (soquetes)", "soquetes de rede (Winsock)"),
        ("Dxg", "dxgkrnl.sys (DirectX/driver de vídeo)", "núcleo gráfico do DirectX"),
        ("Gh", "win32k (GDI)", "objetos GDI de janelas"),
        ("Gla", "win32k (GDI)", "listas do GDI"),
        ("Us", "win32k (USER)", "objetos USER de janelas"),
        ("Acp", "acpi.sys", "ACPI (energia/placa-mãe)"),
    ];

    public static PoolTagOwner? Lookup(string tag, IReadOnlyDictionary<string, (string Owner, string Description)>? localPoolTagTxt = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (localPoolTagTxt is not null && localPoolTagTxt.TryGetValue(tag, out var local))
            return new PoolTagOwner(tag, local.Owner, local.Description, PoolTagTxt + " (cópia instalada neste PC)", true);
        if (Exact.TryGetValue(tag, out var e)) return new PoolTagOwner(tag, e.Owner, e.Description, PoolTagTxt, true);
        foreach (var (prefix, owner, description) in Prefixes.OrderByDescending(p => p.Prefix.Length))
            if (tag.StartsWith(prefix, StringComparison.Ordinal))
                return new PoolTagOwner(tag, owner, description + " (provável, pela família da tag)", PoolTagTxt, false);
        return null;
    }

    /// <summary>
    /// Lê um pooltag.txt (linhas "Tag  - driver.sys   - descrição"; comentários com "rem" ou "//"). A primeira ocorrência vale.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Owner, string Description)> ParsePoolTagTxt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length < 6 || line.StartsWith("rem", StringComparison.OrdinalIgnoreCase) || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var parts = line.Split(" - ", 3, StringSplitOptions.None);
            if (parts.Length < 2) continue;
            var tag = parts[0].TrimStart();
            if (tag.Length is 0 or > 4) continue;
            tag = tag.PadRight(4);
            map.TryAdd(tag, (parts[1].Trim(), parts.Length > 2 ? parts[2].Trim() : ""));
        }
        return map;
    }

    /// <summary>A tag aparece como texto ASCII dentro do binário do driver? (o mesmo que findstr /l faz).</summary>
    public static bool BinaryContainsTag(ReadOnlySpan<byte> binary, string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        if (tag.Length != 4) return false;
        Span<byte> needle = stackalloc byte[4];
        if (Encoding.ASCII.GetBytes(tag, needle) != 4) return false;
        return binary.IndexOf(needle) >= 0;
    }
}
