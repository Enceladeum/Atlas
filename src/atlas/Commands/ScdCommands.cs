// Scd module CLI dispatch — sound-container inspection and audio export (Atlas.Core.Scd).
//   atlas mod Scd info <gamepath>                              tables + per-entry codec/rate/loop
//   atlas mod Scd extract <gamepath> --out <file> [--entry 0]  entry -> .ogg (vorbis) / .wav (adpcm->pcm16)
// --out without extension gets the codec-appropriate one appended.

using Atlas.Core;
using Atlas.Core.Scd;

namespace Atlas.Cli.Commands;

public static class ScdCommands
{
    public static int Run(XivEnv env, List<string> argv)
    {
        string? Opt(string name)
        {
            var i = argv.IndexOf(name);
            if (i < 0 || i + 1 >= argv.Count) return null;
            var v = argv[i + 1];
            argv.RemoveRange(i, 2);
            return v;
        }

        const string usage = "usage: atlas mod Scd <info|extract> <gamepath> [--out <file>] [--entry N]";
        if (argv.Count == 0) { Console.Error.WriteLine(usage); return 1; }
        var verb = argv[0];
        try
        {
            switch (verb)
            {
                case "info":
                {
                    if (argv.Count < 2) { Console.Error.WriteLine("usage: atlas mod Scd info <gamepath>"); return 1; }
                    var s = ScdOps.Info(env, argv[1]);
                    Console.WriteLine($"scd: {s.Path}");
                    Console.WriteLine($"sounds: {s.SoundCount}  tracks: {s.TrackCount}  audio entries: {s.AudioCount}");
                    foreach (var e in s.Entries)
                    {
                        if (e.Error != null)
                        { Console.WriteLine($"  [{e.Index}] error: {e.Error}"); continue; }
                        var dur = e.Seconds is { } sec ? $"  ~{sec:0.0}s" : "";
                        var loop = e.LoopStart != 0 || e.LoopEnd != 0 ? $"  loop {e.LoopStart}..{e.LoopEnd}" : "";
                        var mark = e.Markers > 0 ? $"  markers {e.Markers}" : "";
                        Console.WriteLine($"  [{e.Index}] {e.Format} ({e.FormatValue})  {e.Channels}ch {e.Rate}Hz  {e.DataBytes} bytes{dur}{loop}{mark}");
                    }
                    return 0;
                }
                case "extract":
                {
                    var outPath = Opt("--out");
                    var entry = int.TryParse(Opt("--entry"), out var n) ? n : 0;
                    if (argv.Count < 2 || outPath == null)
                    { Console.Error.WriteLine("usage: atlas mod Scd extract <gamepath> --out <file> [--entry 0]"); return 1; }
                    var (data, ext, _) = ScdOps.Export(env, argv[1], entry);
                    if (!Path.HasExtension(outPath)) outPath += "." + ext;
                    var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(outPath, data);
                    Console.WriteLine($"wrote entry {entry} ({ext}, {data.Length} bytes) -> {outPath}");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown Scd verb: {verb} (verbs: info, extract)");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
