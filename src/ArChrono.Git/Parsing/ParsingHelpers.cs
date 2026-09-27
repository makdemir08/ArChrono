using System.Text;
using ArChrono.Git.Models;

namespace ArChrono.Git.Parsing;

internal static class ParsingHelpers
{
    public const char FieldSeparator = '\x1f';
    public const char RecordSeparator = '\x1e';

    /// <summary>Git'in sekizlik mode değerini (ör. "100644") tamsayıya çevirir.</summary>
    public static int ParseMode(ReadOnlySpan<char> octal)
    {
        var value = 0;
        foreach (var c in octal)
        {
            if (c is < '0' or > '7') return 0;
            value = value * 8 + (c - '0');
        }
        return value;
    }

    public static string FormatMode(int mode) => Convert.ToString(mode, 8).PadLeft(6, '0');

    public static DateTimeOffset FromUnixSeconds(string value) =>
        long.TryParse(value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime() : DateTimeOffset.MinValue;

    public static bool IsNullSha(string? sha) => string.IsNullOrEmpty(sha) || sha.All(c => c == '0');

    public static ChangeKind ParseChangeLetter(char c) => c switch
    {
        'M' => ChangeKind.Modified,
        'T' => ChangeKind.TypeChanged,
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'U' => ChangeKind.Unmerged,
        '?' => ChangeKind.Untracked,
        '!' => ChangeKind.Ignored,
        _ => ChangeKind.None,
    };

    /// <summary>
    /// Git'in C tarzı tırnaklı yol biçimini çözer: <c>"a\tb\303\244"</c>.
    /// core.quotepath=false olsa da sekme, satır sonu ve tırnak içeren yollar tırnaklanır.
    /// </summary>
    public static string UnquotePath(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"') return path;

        var bytes = new List<byte>(path.Length);
        for (var i = 1; i < path.Length - 1; i++)
        {
            var c = path[i];
            if (c != '\\')
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            if (++i >= path.Length - 1) break;
            var e = path[i];
            switch (e)
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 'a': bytes.Add(7); break;
                case 'b': bytes.Add(8); break;
                case 'f': bytes.Add(12); break;
                case 'v': bytes.Add(11); break;
                case >= '0' and <= '7' when i + 2 < path.Length - 1:
                    bytes.Add((byte)Convert.ToInt32(path.Substring(i, 3), 8));
                    i += 2;
                    break;
                default: bytes.Add((byte)e); break;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>NUL ile ayrılmış çıktıyı parçalara ayırır (sondaki boş parça atılır).</summary>
    public static string[] SplitNul(string output)
    {
        var parts = output.Split('\0');
        return parts.Length > 0 && parts[^1].Length == 0 ? parts[..^1] : parts;
    }
}
