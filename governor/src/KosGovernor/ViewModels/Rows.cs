using System.Collections;
using System.ComponentModel;
using System.Windows.Media;
using KosGovernor.Core;

namespace KosGovernor.ViewModels;

/// <summary>A row of the process list; services of an svchost appear as child rows (name only).</summary>
public sealed class ProcessRow : ObservableObject
{
    readonly Action<ProcessRow>? _expandChanged;
    ImageSource? _icon;
    IReadOnlyList<HostedService> _services = Array.Empty<HostedService>();
    ProcessDetails _details = new();
    bool _expanded, _showIcon = true;
    string _cpu = "", _io = "", _mem = "", _threads = "", _status = "", _boost = "", _eco = "", _affinity = "", _cpuSets = "";
    string _cpuUse = "", _memUse = "", _gpu = "", _vram = "";

    public ProcessRow(ProcessEntry entry, Action<ProcessRow> expandChanged)
    {
        Pid = entry.Pid;
        CreateTime = entry.CreateTime;
        Name = entry.Name;
        _expandChanged = expandChanged;
    }

    public ProcessRow(ProcessRow parent, HostedService service)
    {
        Parent = parent;
        Service = service;
        Pid = parent.Pid;
        CreateTime = parent.CreateTime;
        Name = parent.Name;
    }

    public int Pid { get; }
    public long CreateTime { get; }
    public string Name { get; }
    public ProcessRow? Parent { get; }
    public HostedService? Service { get; }
    public bool IsService => Service is not null;
    public string DisplayName => Service is { } s ? $"└─ {s.DisplayName}" : Name;
    public string PidText => IsService ? "" : Pid.ToString();
    public string? ImagePath { get; set; }
    /// <summary>Read on every refresh; the details panel follows it (what the process reports, not what was clicked).</summary>
    public ProcessDetails Details { get => _details; set => Set(ref _details, value); }
    public int ThreadCount { get; set; }

    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool ShowIcon { get => _showIcon; set => Set(ref _showIcon, value); }

    public IReadOnlyList<HostedService> Services
    {
        get => _services;
        set { if (Set(ref _services, value)) Raise(nameof(HasServices)); }
    }
    public bool HasServices => _services.Count > 0;

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) _expandChanged?.Invoke(this); }
    }

    public string CpuText { get => _cpu; set => Set(ref _cpu, value); }
    public string IoText { get => _io; set => Set(ref _io, value); }
    public string MemText { get => _mem; set => Set(ref _mem, value); }
    public string ThreadsText { get => _threads; set => Set(ref _threads, value); }
    public string StatusText { get => _status; set => Set(ref _status, value); }
    public string BoostText { get => _boost; set => Set(ref _boost, value); }
    public string EcoText { get => _eco; set => Set(ref _eco, value); }
    public string AffinityText { get => _affinity; set => Set(ref _affinity, value); }
    public string CpuSetsText { get => _cpuSets; set => Set(ref _cpuSets, value); }
    public string CpuUseText { get => _cpuUse; set => Set(ref _cpuUse, value); }
    public string MemUseText { get => _memUse; set => Set(ref _memUse, value); }
    public string GpuText { get => _gpu; set => Set(ref _gpu, value); }
    public string VramText { get => _vram; set => Set(ref _vram, value); }
    public double CpuUse { get; private set; }
    public long MemUse { get; private set; }
    public double Gpu { get; private set; }
    public long Vram { get; private set; }

    /// <summary>Use columns: CPU % of the whole machine, private memory, GPU % (busiest engine) and dedicated video memory.</summary>
    public void UpdateUse(double cpu, long memory, double? gpu, long? vram)
    {
        (CpuUse, MemUse, Gpu, Vram) = (cpu, memory, gpu ?? 0, vram ?? 0);
        CpuUseText = cpu >= 0.05 ? cpu.ToString("0.0") : "";
        MemUseText = memory > 0 ? $"{memory / 1048576.0:N0} MB" : "";
        GpuText = gpu is >= 0.05 ? gpu.Value.ToString("0.0") : "";
        VramText = vram is > 0 ? $"{vram.Value / 1048576.0:N0} MB" : "";
    }

    public void Update(ProcessEntry e, ProcessDetails d, CpuTopology topo)
    {
        Details = d;
        ThreadCount = e.Threads.Count;
        CpuText = Labels.Short(d.Priority);
        IoText = Labels.Short(d.Io);
        MemText = Labels.Short(d.Memory);
        ThreadsText = e.Threads.Count.ToString();
        StatusText = e.Suspended ? "Suspended" : "Running";
        BoostText = Labels.Check(d.Boost);
        EcoText = d.Efficiency switch { null => "?", Efficiency.On => "✓", _ => "" };
        AffinityText = Labels.Mask(d.Affinity, topo.AllMask);
        CpuSetsText = Labels.CpuSets(d.CpuSets, topo.AllMask);
    }

    /// <summary>Value used to sort by the column.</summary>
    public IComparable SortKey(string column) => column switch
    {
        "PID" => Pid,
        "CPU Priority" => (int?)Details.Priority ?? -1,
        "I/O Priority" => (int?)Details.Io ?? -1,
        "Memory Priority" => (int?)Details.Memory ?? -1,
        "Threads" => ThreadCount,
        "Status" => StatusText,
        "Boost" => BoostText,
        "Eco" => EcoText,
        "Affinity" => AffinityText,
        "CPU Sets" => CpuSetsText,
        "CPU %" => CpuUse,
        "Memory" => MemUse,
        "GPU %" => Gpu,
        "VRAM" => Vram,
        _ => Name.ToLowerInvariant(),
    };
}

/// <summary>Sorts by the chosen column keeping each service right below its process.</summary>
public sealed class ProcessComparer(string column, bool ascending) : IComparer
{
    public string Column => column;
    public bool Ascending => ascending;

    public int Compare(object? x, object? y)
    {
        if (x is not ProcessRow a || y is not ProcessRow b) return 0;
        var pa = a.Parent ?? a;
        var pb = b.Parent ?? b;
        if (!ReferenceEquals(pa, pb))
        {
            var c = pa.SortKey(column).CompareTo(pb.SortKey(column));
            if (c == 0) c = pa.Name.CompareTo(pb.Name) is var n && n != 0 ? n : pa.Pid.CompareTo(pb.Pid);
            return ascending ? c : -c;
        }
        if (a.IsService != b.IsService) return a.IsService ? 1 : -1;
        return string.Compare(a.Service?.DisplayName, b.Service?.DisplayName, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>A row of the threads window.</summary>
public sealed class ThreadRow : ObservableObject
{
    string _description = "", _priority = "", _base = "", _io = "", _mem = "", _ideal = "", _status = "", _boost = "", _module = "";

    public ThreadRow(ThreadEntry t)
    {
        Tid = t.Tid;
        CreateTime = t.CreateTime;
    }

    public int Tid { get; }
    public long CreateTime { get; }
    public ThreadDetails Details { get; private set; } = new();
    public int BaseValue { get; private set; }

    public string Description { get => _description; set => Set(ref _description, value); }
    public string PriorityText { get => _priority; set => Set(ref _priority, value); }
    public string BaseText { get => _base; set => Set(ref _base, value); }
    public string IoText { get => _io; set => Set(ref _io, value); }
    public string MemText { get => _mem; set => Set(ref _mem, value); }
    public string IdealText { get => _ideal; set => Set(ref _ideal, value); }
    public string StatusText { get => _status; set => Set(ref _status, value); }
    public string BoostText { get => _boost; set => Set(ref _boost, value); }
    public string Module { get => _module; set => Set(ref _module, value); }

    public void Update(ThreadEntry t, ThreadDetails d, int processBase, string? module)
    {
        Details = d;
        BaseValue = t.BasePriority - processBase;
        Description = d.Description ?? "";
        PriorityText = Labels.ThreadPriority(d.Priority);
        BaseText = BaseValue.ToString();
        IoText = Labels.Short(d.Io);
        MemText = Labels.Short(d.Memory);
        IdealText = d.Ideal is { } i ? $"{i.Group}:{i.Number}" : "?";
        StatusText = t.Suspended ? "Suspended" : "Running";
        BoostText = Labels.Check(d.Boost);
        if (module is not null) Module = module;
    }

    public IComparable SortKey(string column) => column switch
    {
        "Description" => Description.ToLowerInvariant(),
        "Priority" => Details.Priority ?? int.MinValue,
        "Base" => BaseValue,
        "I/O" => (int?)Details.Io ?? -1,
        "Memory" => (int?)Details.Memory ?? -1,
        "Ideal CPU" => Details.Ideal?.Number ?? -1,
        "Status" => StatusText,
        "Boost" => BoostText,
        "Module" => Module.ToLowerInvariant(),
        _ => Tid,
    };
}

public sealed class ThreadComparer(string column, bool ascending) : IComparer
{
    public string Column => column;
    public bool Ascending => ascending;

    public int Compare(object? x, object? y)
    {
        if (x is not ThreadRow a || y is not ThreadRow b) return 0;
        var c = a.SortKey(column).CompareTo(b.SortKey(column));
        if (c == 0) c = a.Tid.CompareTo(b.Tid);
        return ascending ? c : -c;
    }
}
