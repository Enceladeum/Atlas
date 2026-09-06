using System.Text;
using Lumina.Text.ReadOnly;

namespace Atlas.Core;

/// <summary>
/// The single CSV convention implementation. Do not re-implement
/// escaping or value formatting in module code.
/// </summary>
public static class Csv
{
    /// <summary>Quote iff needed; embedded quotes doubled.</summary>
    public static string Escape(string s) =>
        s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>Canonical value formatting: floats "R", bools True/False, SeStrings extracted, null empty.</summary>
    public static string Str(object? v) => v switch
    {
        ReadOnlySeString rss => rss.ExtractText(),
        bool b => b ? "True" : "False",
        float f => f.ToString("R"),
        double d => d.ToString("R"),
        null => "",
        _ => v.ToString() ?? ""
    };

    /// <summary>UTF-8 no BOM, overwrite.</summary>
    public static StreamWriter OpenWriter(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new StreamWriter(path, false, new UTF8Encoding(false));
    }
}
