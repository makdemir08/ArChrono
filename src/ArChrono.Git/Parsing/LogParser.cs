using ArChrono.Git.Models;

namespace ArChrono.Git.Parsing;

/// <summary>Commit listesi, name-status ve numstat çıktıları.</summary>
public static class LogParser
{
    /// <summary>
    /// <c>git log</c> için format: alanlar %x1f, kayıtlar %x1e ile ayrılır.
    /// Sıra: sha, parents, author name, author email, author time, committer name, committer email, commit time, subject.
    /// </summary>
    public const string CommitFormat = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%cn%x1f%ce%x1f%ct%x1f%s%x1e";

    public static IReadOnlyList<CommitInfo> ParseCommits(string output)
    {
        var commits = new List<CommitInfo>();
        foreach (var record in output.Split(ParsingHelpers.RecordSeparator))
        {
            var trimmed = record.TrimStart('\n', '\r');
            if (trimmed.Length == 0) continue;
            if (ParseCommit(trimmed) is { } commit) commits.Add(commit);
        }
        return commits;
    }

    public static CommitInfo? ParseCommit(string record)
    {
        var f = record.Split(ParsingHelpers.FieldSeparator);
        if (f.Length < 9 || f[0].Length < 7) return null;
        return new CommitInfo(
            f[0],
            f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            f[2], f[3], ParsingHelpers.FromUnixSeconds(f[4]),
            f[5], f[6], ParsingHelpers.FromUnixSeconds(f[7]),
            f[8].TrimEnd('\n', '\r'));
    }

    /// <summary><c>--name-status -z</c>: "M\0path\0", "R100\0old\0new\0".</summary>
    public static IReadOnlyList<(ChangeKind Change, string Path, string? OldPath)> ParseNameStatusZ(string output)
    {
        var result = new List<(ChangeKind, string, string?)>();
        var parts = ParsingHelpers.SplitNul(output);
        for (var i = 0; i < parts.Length; i++)
        {
            var status = parts[i].Trim('\n');
            if (status.Length == 0) continue;
            var change = ParsingHelpers.ParseChangeLetter(status[0]);
            if (change is ChangeKind.Renamed or ChangeKind.Copied)
            {
                if (i + 2 >= parts.Length) break;
                var oldPath = parts[++i];
                var newPath = parts[++i];
                result.Add((change, newPath, oldPath));
            }
            else
            {
                if (i + 1 >= parts.Length) break;
                result.Add((change, parts[++i], null));
            }
        }
        return result;
    }

    /// <summary>
    /// <c>--numstat -z</c>: "adds\tdels\tpath\0" veya rename için "adds\tdels\t\0old\0new\0".
    /// Binary dosyalarda adds/dels "-".
    /// </summary>
    public static IReadOnlyDictionary<string, (int Additions, int Deletions, bool IsBinary)> ParseNumstatZ(string output)
    {
        var result = new Dictionary<string, (int, int, bool)>(StringComparer.Ordinal);
        var parts = ParsingHelpers.SplitNul(output);
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i].TrimStart('\n');
            if (entry.Length == 0) continue;
            var fields = entry.Split('\t', 3);
            if (fields.Length < 3) continue;
            var isBinary = fields[0] == "-";
            int.TryParse(fields[0], out var additions);
            int.TryParse(fields[1], out var deletions);
            var path = fields[2];
            if (path.Length == 0 && i + 2 < parts.Length)
            {
                i++; // eski yol
                path = parts[++i];
            }
            result[path] = (additions, deletions, isBinary);
        }
        return result;
    }

    /// <summary>
    /// <c>git log --follow --format=%x1e&lt;CommitFormat alanları&gt; --name-status</c> çıktısı (‑z olmadan).
    /// </summary>
    public static IReadOnlyList<FileHistoryEntry> ParseFileHistory(string output, string requestedPath)
    {
        var entries = new List<FileHistoryEntry>();
        foreach (var record in output.Split(ParsingHelpers.RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n');
            var commit = ParseCommit(lines[0]);
            if (commit is null) continue;

            string path = requestedPath;
            string? oldPath = null;
            var change = ChangeKind.Modified;
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                var fields = line.Split('\t');
                if (fields.Length < 2) continue;
                change = ParsingHelpers.ParseChangeLetter(fields[0][0]);
                if (change is ChangeKind.Renamed or ChangeKind.Copied && fields.Length >= 3)
                {
                    oldPath = ParsingHelpers.UnquotePath(fields[1]);
                    path = ParsingHelpers.UnquotePath(fields[2]);
                }
                else
                {
                    path = ParsingHelpers.UnquotePath(fields[1]);
                }
                break;
            }
            entries.Add(new FileHistoryEntry(commit, path, oldPath, change));
        }
        return entries;
    }
}
