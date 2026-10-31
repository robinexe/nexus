namespace Nexus.Monitoring;

/// <summary>Buffer circular de tamanho fixo: sem alocação depois de criado. Índice 0 = o valor mais antigo ainda guardado.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _items[(_start + index) % _items.Length];
        }
    }

    public T? Latest => Count == 0 ? default : this[Count - 1];

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
            return;
        }
        _items[_start] = item;
        _start = (_start + 1) % _items.Length;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    /// <summary>Copia em ordem (mais antigo primeiro) para <paramref name="destination"/>; devolve quantos copiou.</summary>
    public int CopyTo(Span<T> destination)
    {
        var n = Math.Min(Count, destination.Length);
        var skip = Count - n; // se o destino for menor, ficam os mais recentes
        for (var i = 0; i < n; i++) destination[i] = _items[(_start + skip + i) % _items.Length];
        return n;
    }

    /// <summary>Os últimos <paramref name="count"/> valores (mais antigo primeiro).</summary>
    public IEnumerable<T> Last(int count)
    {
        var n = Math.Min(Math.Max(0, count), Count);
        for (var i = Count - n; i < Count; i++) yield return this[i];
    }
}

/// <summary>Histórico curto do monitor: 5 minutos a 1 Hz (300 pontos) para os mini-gráficos e o detector de gargalo.</summary>
public sealed class MonitorHistory
{
    public const int DefaultCapacity = 300;

    public MonitorHistory(int capacity = DefaultCapacity)
    {
        Cpu = new RingBuffer<double>(capacity);
        Gpu = new RingBuffer<double>(capacity);
        Memory = new RingBuffer<double>(capacity);
        Disk = new RingBuffer<double>(capacity);
        Network = new RingBuffer<double>(capacity);
        Snapshots = new RingBuffer<MonitorSnapshot>(capacity);
    }

    public RingBuffer<double> Cpu { get; }
    public RingBuffer<double> Gpu { get; }
    /// <summary>% de RAM em uso.</summary>
    public RingBuffer<double> Memory { get; }
    /// <summary>% de tempo ativo do disco mais ocupado.</summary>
    public RingBuffer<double> Disk { get; }
    /// <summary>Bytes/s somando envio e recebimento de todos os adaptadores.</summary>
    public RingBuffer<double> Network { get; }
    public RingBuffer<MonitorSnapshot> Snapshots { get; }

    public void Add(MonitorSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Cpu.Add(s.Cpu.TotalPercent);
        Gpu.Add(s.Gpu?.OverallPercent ?? 0);
        Memory.Add(s.Memory?.UsedPercent ?? 0);
        Disk.Add(s.DiskActiveMax);
        Network.Add(s.NetworkTotalBytesPerSec);
        Snapshots.Add(s);
    }

    public void Clear()
    {
        Cpu.Clear();
        Gpu.Clear();
        Memory.Clear();
        Disk.Clear();
        Network.Clear();
        Snapshots.Clear();
    }
}
