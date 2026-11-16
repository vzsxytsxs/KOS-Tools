using System.Runtime.InteropServices;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.Startup;

namespace KosToolbox.Core.Settings;

/// <summary>Scheduled task enabled ("1") or disabled ("0"); null = the task doesn't exist on this installation.</summary>
public sealed class ScheduledTaskSetting(string path) : ISetting
{
    public string Key => $"task:{path}";
    public string Display => $"Scheduled task {path}";
    public bool MachineWide => true;
    public string? Unavailable() => Read() is null ? $"The scheduled task {path} doesn't exist on this installation." : null;

    public string? Read()
    {
        try { return ScheduledTaskProvider.IsEnabled(path) ? "1" : "0"; }
        // Nonexistent task or folder: through dynamic COM it arrives as FileNotFound/DirectoryNotFound (or COMException).
        catch (Exception ex) when ((uint)ex.HResult is 0x80070002 or 0x80070003) { return null; }
    }

    public void Write(string? value) => ScheduledTaskProvider.SetEnabled(path, value == "1");
}

/// <summary>NVIDIA card ECC through NVML ("1"/"0", the mode that applies after a reboot), like nvidia-smi -e.</summary>
public sealed class NvidiaEccSetting(string busId) : ISetting
{
    public string Key => $"nvml-ecc:{busId}";
    public string Display => $"ECC of the GPU {busId} (NVML)";
    public bool MachineWide => true;

    public string? Unavailable()
    {
        try { return GpuTuner.EccPending(busId) is null ? "The driver reports no ECC support on this GPU." : null; }
        catch (Exception ex) { return ex.Message; }
    }

    public string? Read() => GpuTuner.EccPending(busId) switch { true => "1", false => "0", null => null };
    public void Write(string? value) => GpuTuner.SetEcc(busId, value == "1");
}
