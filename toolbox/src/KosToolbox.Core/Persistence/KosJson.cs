using System.Text.Json.Serialization;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.SystemInfo;

namespace KosToolbox.Core.Persistence;

/// <summary>
/// Source-generated JSON for the files KOS Engine (Native AOT, no reflection) also reads or writes: the fan
/// curve and its state, the GPU tuning saved for sign-in, the clock-lock record, the gamemode GPU profiles, the gamemode mouse settings, the gamemode session reports, the KOS Firewall config and KOS Equalizer's registration journal and profiles. Same options as <see cref="JsonFile"/>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GovernorFanCurve))]
[JsonSerializable(typeof(GovernorFanCurve.State))]
[JsonSerializable(typeof(GpuTuning))]
[JsonSerializable(typeof(ClockLockRecord.Entry))]
[JsonSerializable(typeof(GamemodeGpu))]
[JsonSerializable(typeof(GamemodeGpuState))]
[JsonSerializable(typeof(GamemodeSessionList))]
[JsonSerializable(typeof(KosToolbox.Core.Peripherals.GamemodePeripheral))]
[JsonSerializable(typeof(KosToolbox.Core.Peripherals.GamemodePeripheralState))]
[JsonSerializable(typeof(KosToolbox.Core.Firewall.FirewallConfig))]
[JsonSerializable(typeof(KosToolbox.Core.Audio.EqJournal))]
[JsonSerializable(typeof(KosToolbox.Core.Audio.EqConfig))]
internal sealed partial class KosJson : JsonSerializerContext { }
