using Atlas.Core;

namespace Atlas.Cli.Commands;

// atlas raw extract <gamepath> --out <file>   — write the decompressed sqpack file as-is.
public static class RawCommands
{
    public static int Run(XivEnv env, List<string> args)
    {
        if (args.Count == 0) { Console.Error.WriteLine("usage: atlas raw extract <gamepath> --out <file>"); return 1; }
        var verb = args[0];
        try
        {
            switch (verb)
            {
                case "extract":
                {
                    var outFile = Opt(args, "--out");
                    var outDir = Opt(args, "--out-dir");
                    var listFile = Opt(args, "--list");
                    var paths = new List<string>();
                    for (int i = 1; i < args.Count; i++)
                    {
                        if (args[i].StartsWith("--")) { i++; continue; }
                        paths.Add(args[i]);
                    }
                    if (listFile != null)
                        paths.AddRange(File.ReadAllLines(listFile).Select(l => l.Trim()).Where(l => l.Length > 0));
                    if (paths.Count == 0) throw new ArgumentException("missing <gamepath>");
                    if (outFile == null && outDir == null) throw new ArgumentException("missing --out <file> or --out-dir <dir>");
                    if (paths.Count > 1 && outDir == null) throw new ArgumentException("multiple paths need --out-dir");
                    if (outDir != null) Directory.CreateDirectory(outDir);
                    int okCount = 0, missCount = 0;
                    foreach (var path in paths)
                    {
                        var f = env.Game.GetFile(path);
                        if (f == null) { Console.Error.WriteLine($"not found: {path}"); missCount++; continue; }
                        var dest = outDir != null ? Path.Combine(outDir, path.Replace('/', '_')) : outFile!;
                        File.WriteAllBytes(dest, f.Data);
                        Console.WriteLine($"{dest}  {f.Data.Length} bytes");
                        okCount++;
                    }
                    if (paths.Count > 1) Console.WriteLine($"{okCount} extracted, {missCount} missing");
                    return missCount == paths.Count ? 1 : 0;
                }
                default:
                    Console.Error.WriteLine($"unknown verb: {verb}"); return 1;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    }

    static string? Opt(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
