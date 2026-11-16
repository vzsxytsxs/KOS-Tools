using System.Text.Json.Serialization;

namespace KosGovernor.Core;

/// <summary>
/// Source-generated JSON for every file the governor reads or writes (config.json, gamemode state, exported profiles,
/// built-in game list). No reflection: the Native AOT engine reads the same files as the interface, in the same format.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(GovernorConfig))]
[JsonSerializable(typeof(GamemodeSystemState))]
[JsonSerializable(typeof(RuleState))]
[JsonSerializable(typeof(ConfigStore.ProfileFile))]
[JsonSerializable(typeof(List<GameEntry>))]
public sealed partial class GovernorJson : JsonSerializerContext { }
