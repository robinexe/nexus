using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nexus.Lab.Memory;

/// <summary>Parâmetros das medições. Use <see cref="ForAvailableMemory"/> para tamanhos seguros no PC atual.</summary>
public sealed record BenchmarkOptions
{
    public long BufferBytes { get; init; } = 256L * 1024 * 1024;
    public int Threads { get; init; } = Math.Max(1, Environment.ProcessorCount);
    public int Passes { get; init; } = 7;
    public long LatencyBytes { get; init; } = 512L * 1024 * 1024;
    public TimeSpan LatencyDuration { get; init; } = TimeSpan.FromSeconds(1.2);
    public bool TryLargePages { get; init; } = true;

    private const long Mb = 1024L * 1024;

    /// <summary>
    /// Tamanhos que cabem na memória livre sem empurrar o Windows para o arquivo de paginação: cada bloco de banda até 1/10 do
    /// livre (64–256 MB, muito maior que qualquer cache L3), bloco de latência até 1/5 do livre (128 MB–1 GB, potência de 2).
    /// </summary>
    public static BenchmarkOptions ForAvailableMemory(ulong availableBytes, int logicalProcessors)
    {
        var avail = (long)Math.Min(availableBytes, long.MaxValue / 2);
        var buffer = Math.Clamp(avail / 10, 64 * Mb, 256 * Mb) / (4 * Mb) * (4 * Mb);
        var lat = Math.Clamp(avail / 5, 128 * Mb, 1024 * Mb);
        var pow = 128 * Mb;
        while (pow * 2 <= lat) pow *= 2;
        return new BenchmarkOptions { BufferBytes = buffer, LatencyBytes = pow, Threads = Math.Max(1, logicalProcessors) };
    }
}

public sealed record BandwidthResult(
    double ReadGbps, double WriteGbps, double CopyGbps,
    IReadOnlyList<double> ReadSamples, IReadOnlyList<double> WriteSamples, IReadOnlyList<double> CopySamples,
    int Threads, long BufferBytes, int Passes, string Instructions);

public sealed record LatencyResult(double RandomNs, double PageLocalNs, long BlockBytes, bool LargePages, string PageNote);

/// <summary>Bloco de memória nativa alinhado (liberado no Dispose).</summary>
public sealed unsafe class NativeBlock : IDisposable
{
    private readonly Action<nint>? _free;
    public byte* Address { get; private set; }
    public long Length { get; }
    public bool LargePages { get; }

    public NativeBlock(byte* address, long length, bool largePages, Action<nint>? free)
    {
        Address = address; Length = length; LargePages = largePages; _free = free;
    }

    public static NativeBlock Allocate(long bytes, nuint alignment = 4096)
    {
        var p = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, alignment);
        return new NativeBlock(p, bytes, false, ptr => NativeMemory.AlignedFree((void*)ptr));
    }

    public void Dispose()
    {
        if (Address is null) return;
        var p = (nint)Address;
        Address = null;
        _free?.Invoke(p);
    }
}

/// <summary>Fornece memória para a medição de latência (no Windows tenta páginas grandes; ver <see cref="WindowsLargePages"/>).</summary>
public interface ILatencyAllocator
{
    NativeBlock Allocate(long bytes, bool tryLargePages, out string note);
}

public sealed class DefaultLatencyAllocator : ILatencyAllocator
{
    public NativeBlock Allocate(long bytes, bool tryLargePages, out string note)
    {
        if (!tryLargePages) note = "Páginas normais de 4 KB.";
        else if (!OperatingSystem.IsWindows()) note = "Páginas normais de 4 KB (páginas grandes só no Windows).";
        else if (WindowsLargePages.TryAllocate(bytes, out var block, out var why) && block is not null) { note = why; return block; }
        else note = why;
        return NativeBlock.Allocate(bytes);
    }
}

/// <summary>
/// Medições de memória sem driver: banda (leitura, escrita, cópia) com várias threads e latência por perseguição de ponteiros.
/// Puro .NET (Vector256/Vector128 quando o processador tem), roda em Linux nos testes com blocos pequenos.
/// </summary>
public static unsafe class MemoryBenchmark
{
    private enum Op { Read, Write, Copy }

    public static string InstructionSet =>
        Avx.IsSupported ? "AVX (256 bits) com escrita não temporal"
        : Sse2.IsSupported ? "SSE2 (128 bits) com escrita não temporal"
        : Vector128.IsHardwareAccelerated ? "vetores de 128 bits"
        : "escalar (64 bits)";

    /// <summary>
    /// Banda: dois blocos alinhados muito maiores que o cache, divididos entre as threads. Cada passada lê (ou escreve, ou
    /// copia) o bloco inteiro uma vez; o tempo vai da largada simultânea de todas as threads até a última terminar.
    /// Resultado = mediana das passadas, em GB/s decimais (cópia conta os bytes copiados uma vez).
    /// </summary>
    public static BandwidthResult MeasureBandwidth(BenchmarkOptions options, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var threads = Math.Max(1, options.Threads);
        var chunk = Math.Max(4096, options.BufferBytes / threads / 4096 * 4096);
        var size = chunk * threads;
        using var src = NativeBlock.Allocate(size);
        using var dst = NativeBlock.Allocate(size);
        // Toca todas as páginas antes de medir: a primeira escrita em cada página custa uma falta de página do sistema.
        Fill(src.Address, size, 0x5A);
        Fill(dst.Address, size, 0x00);
        cancel.ThrowIfCancellationRequested();

        var sink = new ulong[threads * 8];
        var op = Op.Read;
        using var start = new Barrier(threads + 1);
        using var end = new Barrier(threads + 1);
        var stop = false;
        var workers = new Thread[threads];
        var srcP = (nint)src.Address;
        var dstP = (nint)dst.Address;
        for (var t = 0; t < threads; t++)
        {
            var id = t;
            workers[t] = new Thread(() =>
            {
                while (true)
                {
                    start.SignalAndWait(CancellationToken.None);
                    if (Volatile.Read(ref stop)) return;
                    var s = (byte*)srcP + id * chunk;
                    var d = (byte*)dstP + id * chunk;
                    switch (op)
                    {
                        case Op.Read: sink[id * 8] ^= ReadKernel(s, chunk); break;
                        case Op.Write: WriteKernel(d, chunk); break;
                        case Op.Copy: CopyKernel(s, d, chunk); break;
                    }
                    end.SignalAndWait(CancellationToken.None);
                }
            }) { IsBackground = true, Name = "NEXUS memória " + id, Priority = ThreadPriority.AboveNormal };
            workers[t].Start();
        }

        try
        {
            var passes = Math.Max(1, options.Passes);
            var results = new Dictionary<Op, List<double>>();
            foreach (var o in new[] { Op.Read, Op.Write, Op.Copy })
            {
                progress?.Report(o switch { Op.Read => "Medindo leitura…", Op.Write => "Medindo escrita…", _ => "Medindo cópia…" });
                op = o;
                var list = new List<double>();
                for (var p = 0; p < passes + 1; p++) // a primeira passada aquece e é descartada
                {
                    cancel.ThrowIfCancellationRequested();
                    start.SignalAndWait(CancellationToken.None);
                    var t0 = Stopwatch.GetTimestamp();
                    end.SignalAndWait(CancellationToken.None);
                    var secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;
                    if (p > 0 && secs > 0) list.Add(size / secs / 1e9);
                }
                results[o] = list;
            }
            return new BandwidthResult(Median(results[Op.Read]), Median(results[Op.Write]), Median(results[Op.Copy]),
                results[Op.Read], results[Op.Write], results[Op.Copy], threads, size, passes, InstructionSet);
        }
        finally
        {
            Volatile.Write(ref stop, true);
            start.SignalAndWait(CancellationToken.None);
            foreach (var w in workers) w.Join();
            GC.KeepAlive(sink);
        }
    }

    /// <summary>
    /// Latência: bloco muito maior que o L3 ligado numa única volta aleatória (uma linha de cache de 64 B por nó). Cada passo
    /// depende do anterior, então o processador não consegue adiantar leituras: o tempo por passo é a latência da RAM. A volta
    /// "aleatória" inclui a falta de TLB (tradução de endereço) de cada página; a volta "por página" visita as 64 linhas de uma
    /// página antes de ir para outra, o que tira quase todo esse custo (estimativa da latência só da RAM).
    /// </summary>
    public static LatencyResult MeasureLatency(BenchmarkOptions options, ILatencyAllocator? allocator = null, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        allocator ??= new DefaultLatencyAllocator();
        using var block = allocator.Allocate(options.LatencyBytes, options.TryLargePages, out var note);
        var nodes = (int)Math.Min(block.Length / 64, int.MaxValue / 2);
        var rng = new Random(20261031);

        progress?.Report("Montando a volta aleatória…");
        cancel.ThrowIfCancellationRequested();
        var order = new int[nodes];
        for (var i = 0; i < nodes; i++) order[i] = i;
        for (var i = nodes - 1; i > 0; i--) { var j = rng.Next(i); (order[i], order[j]) = (order[j], order[i]); } // Sattolo: uma volta só
        Link(block.Address, order);
        progress?.Report("Medindo latência (aleatória)…");
        var random = Chase(block.Address, options.LatencyDuration, cancel);

        progress?.Report("Medindo latência (por página)…");
        cancel.ThrowIfCancellationRequested();
        order = PageLocalOrder(order, rng);
        Link(block.Address, order);
        var local = Chase(block.Address, options.LatencyDuration, cancel);
        return new LatencyResult(random, local, block.Length, block.LargePages, note);
    }

    // ---------------------------------------------------------------- núcleos

    private static void Fill(byte* p, long n, byte value)
    {
        for (long off = 0; off < n; off += 1 << 20)
            new Span<byte>(p + off, (int)Math.Min(1 << 20, n - off)).Fill(value);
    }

    private static ulong ReadKernel(byte* p, long n)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            var a = Vector256<ulong>.Zero; var b = a; var c = a; var d = a;
            long i = 0;
            for (; i + 128 <= n; i += 128)
            {
                a ^= Vector256.LoadAligned((ulong*)(p + i));
                b ^= Vector256.LoadAligned((ulong*)(p + i + 32));
                c ^= Vector256.LoadAligned((ulong*)(p + i + 64));
                d ^= Vector256.LoadAligned((ulong*)(p + i + 96));
            }
            var r = a ^ b ^ c ^ d;
            return r.GetElement(0) ^ r.GetElement(1) ^ r.GetElement(2) ^ r.GetElement(3);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var a = Vector128<ulong>.Zero; var b = a; var c = a; var d = a;
            long i = 0;
            for (; i + 64 <= n; i += 64)
            {
                a ^= Vector128.LoadAligned((ulong*)(p + i));
                b ^= Vector128.LoadAligned((ulong*)(p + i + 16));
                c ^= Vector128.LoadAligned((ulong*)(p + i + 32));
                d ^= Vector128.LoadAligned((ulong*)(p + i + 48));
            }
            var r = a ^ b ^ c ^ d;
            return r.GetElement(0) ^ r.GetElement(1);
        }
        ulong x = 0;
        for (long i = 0; i + 8 <= n; i += 8) x ^= *(ulong*)(p + i);
        return x;
    }

    private static void WriteKernel(byte* p, long n)
    {
        if (Avx.IsSupported)
        {
            var v = Vector256.Create((byte)0xA5);
            for (long i = 0; i + 128 <= n; i += 128)
            {
                Avx.StoreAlignedNonTemporal(p + i, v);
                Avx.StoreAlignedNonTemporal(p + i + 32, v);
                Avx.StoreAlignedNonTemporal(p + i + 64, v);
                Avx.StoreAlignedNonTemporal(p + i + 96, v);
            }
            Sse.StoreFence();
            return;
        }
        if (Sse2.IsSupported)
        {
            var v = Vector128.Create((byte)0xA5);
            for (long i = 0; i + 64 <= n; i += 64)
            {
                Sse2.StoreAlignedNonTemporal(p + i, v);
                Sse2.StoreAlignedNonTemporal(p + i + 16, v);
                Sse2.StoreAlignedNonTemporal(p + i + 32, v);
                Sse2.StoreAlignedNonTemporal(p + i + 48, v);
            }
            Sse.StoreFence();
            return;
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var v = Vector128.Create((byte)0xA5);
            for (long i = 0; i + 16 <= n; i += 16) v.StoreAligned(p + i);
            return;
        }
        for (long i = 0; i + 8 <= n; i += 8) *(ulong*)(p + i) = 0xA5A5A5A5A5A5A5A5UL;
    }

    private static void CopyKernel(byte* s, byte* d, long n)
    {
        if (Avx.IsSupported)
        {
            for (long i = 0; i + 128 <= n; i += 128)
            {
                var a = Avx.LoadAlignedVector256(s + i);
                var b = Avx.LoadAlignedVector256(s + i + 32);
                var c = Avx.LoadAlignedVector256(s + i + 64);
                var e = Avx.LoadAlignedVector256(s + i + 96);
                Avx.StoreAlignedNonTemporal(d + i, a);
                Avx.StoreAlignedNonTemporal(d + i + 32, b);
                Avx.StoreAlignedNonTemporal(d + i + 64, c);
                Avx.StoreAlignedNonTemporal(d + i + 96, e);
            }
            Sse.StoreFence();
            return;
        }
        if (Sse2.IsSupported)
        {
            for (long i = 0; i + 64 <= n; i += 64)
            {
                var a = Sse2.LoadAlignedVector128(s + i);
                var b = Sse2.LoadAlignedVector128(s + i + 16);
                var c = Sse2.LoadAlignedVector128(s + i + 32);
                var e = Sse2.LoadAlignedVector128(s + i + 48);
                Sse2.StoreAlignedNonTemporal(d + i, a);
                Sse2.StoreAlignedNonTemporal(d + i + 16, b);
                Sse2.StoreAlignedNonTemporal(d + i + 32, c);
                Sse2.StoreAlignedNonTemporal(d + i + 48, e);
            }
            Sse.StoreFence();
            return;
        }
        Buffer.MemoryCopy(s, d, n, n);
    }

    private static void Link(byte* basePtr, int[] order)
    {
        var n = order.Length;
        for (var k = 0; k < n; k++)
        {
            var from = basePtr + (long)order[k] * 64;
            var to = basePtr + (long)order[(k + 1) % n] * 64;
            *(nint*)from = (nint)to;
        }
    }

    /// <summary>Ordem "por página": páginas de 4 KB em ordem aleatória; dentro de cada uma, as 64 linhas em ordem aleatória.</summary>
    private static int[] PageLocalOrder(int[] order, Random rng)
    {
        const int linesPerPage = 4096 / 64;
        var pages = order.Length / linesPerPage;
        if (pages < 2) return order;
        var pageOrder = new int[pages];
        for (var i = 0; i < pages; i++) pageOrder[i] = i;
        rng.Shuffle(pageOrder);
        var lines = new int[linesPerPage];
        var k = 0;
        foreach (var page in pageOrder)
        {
            for (var l = 0; l < linesPerPage; l++) lines[l] = l;
            rng.Shuffle(lines);
            foreach (var l in lines) order[k++] = page * linesPerPage + l;
        }
        // linhas que sobram (bloco que não é múltiplo de 4 KB) ficam fora da volta
        return k < order.Length ? order[..k] : order;
    }

    private static double Chase(byte* start, TimeSpan duration, CancellationToken cancel)
    {
        var p = (nint)start;
        // aquecimento: percorre um pedaço para tirar o efeito da primeira passada
        for (var i = 0; i < 200_000; i++) p = *(nint*)p;
        long steps = 0;
        var t0 = Stopwatch.GetTimestamp();
        var limit = (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            for (var i = 0; i < 100_000; i++)
            {
                p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
                p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
            }
            steps += 800_000;
            if (Stopwatch.GetTimestamp() - t0 >= limit) break;
            cancel.ThrowIfCancellationRequested();
        }
        var secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        GC.KeepAlive(p);
        _sinkPointer = p;
        return secs * 1e9 / steps;
    }

    private static nint _sinkPointer;

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}
