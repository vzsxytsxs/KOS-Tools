using System.Text.Json.Serialization;
using KosToolbox.Core.Gpu;
using KosToolbox.Core.SystemInfo;

namespace KosToolbox.Core.Persistence;

/// <summary>
/// Source-generated JSON for the files the governor engine (Native AOT, no reflection) also reads or writes: the fan
/// curve and its state, the GPU tuning saved for sign-in, the clock-lock record, the gamemode GPU profiles and the gamemode session reports. Same options as <see cref="JsonFile"/>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GovernorFanCurve))]
[JsonSerializable(typeof(GovernorFanCurve.State))]
[JsonSerializable(typeof(GpuTuning))]
[JsonSerializable(typeof(ClockLockRecord.Entry))]
[JsonSerializable(typeof(GamemodeGpu))]
[JsonSerializable(typeof(GamemodeGpuState))]
[JsonSerializable(typeof(GamemodeSessionList))]
internal sealed partial class KosJson : JsonSerializerContext { }
