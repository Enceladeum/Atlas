namespace Atlas.Core;

/// <summary>
/// The shared identity rule: every instance-scoped row carries
/// (TerritoryId, LgbFile, LayerId, InstanceId) — the same InstanceKey identity
/// runtime consumers use at runtime, so offline rows map 1:1 onto runtime action.
/// </summary>
public readonly record struct InstanceIdentity(uint TerritoryId, string LgbFile, uint LayerId, uint InstanceId)
{
    /// <summary>The canonical leading header columns, in order.</summary>
    public const string CsvHeader = "TerritoryId,LgbFile,LayerId,InstanceId";

    public string ToCsvPrefix() => $"{TerritoryId},{LgbFile},{LayerId},{InstanceId}";

    /// <summary>Short LGB names, canonical order.</summary>
    public static readonly string[] LgbFiles = ["bg", "planmap", "planevent", "planlive", "planner", "sound", "vfx"];
}
