using Lumina;

namespace Atlas.Core;

/// <summary>
/// Bootstrap: one GameData + optional EXDSchema dir. Core never reads env vars;
/// the CLI resolves --game/--schema/ATLAS_GAME/ATLAS_SCHEMA (XIVTOOL_* fallback) and passes them in.
/// </summary>
public sealed class XivEnv
{
    public GameData Game { get; }
    public string? SchemaDir { get; }

    public XivEnv(string sqpackDir, string? schemaDir = null)
    {
        Game = new GameData(sqpackDir);
        SchemaDir = schemaDir;
    }
}
