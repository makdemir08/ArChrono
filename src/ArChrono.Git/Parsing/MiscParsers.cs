using System.Text.RegularExpressions;
using ArChrono.Git.Models;
using ArChrono.Localization;

namespace ArChrono.Git.Parsing;

public static partial class ReflogParser
{
    /// <summary><c>git reflog show --date=unix --format=</c> için format.</summary>
    public const string Format = "%H%x1f%gd%x1f%gs%x1e";

    [GeneratedRegex(@"@\{(\d+)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex UnixSelector();

    public static IReadOnlyList<ReflogEntry> Parse(string output, string refName)
    {
        var raw = new List<(string Sha, DateTimeOffset Time, string Message)>();
        foreach (var record in output.Split(ParsingHelpers.RecordSeparator))
        {
            var line = record.Trim('\n', '\r');
            if (line.Length == 0) continue;
            var f = line.Split(ParsingHelpers.FieldSeparator, 3);
            if (f.Length < 3) continue;
            var match = UnixSelector().Match(f[1]);
            var time = match.Success ? ParsingHelpers.FromUnixSeconds(match.Groups[1].Value) : DateTimeOffset.MinValue;
            raw.Add((f[0], time, f[2]));
        }

        var entries = new List<ReflogEntry>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (sha, time, message) = raw[i];
            var previous = i + 1 < raw.Count ? raw[i + 1].Sha : null;
            var colon = message.IndexOf(':');
            var action = colon > 0 ? message[..colon] : message;
            entries.Add(new ReflogEntry(refName, i, sha, previous, action.Trim(), message, time));
        }
        return entries;
    }
}

public static class StashParser
{
    public const string Format = "%H%x1f%gs%x1f%ct%x1e";

    public static IReadOnlyList<StashEntry> Parse(string output)
    {
        var entries = new List<StashEntry>();
        var index = 0;
        foreach (var record in output.Split(ParsingHelpers.RecordSeparator))
        {
            var line = record.Trim('\n', '\r');
            if (line.Length == 0) continue;
            var f = line.Split(ParsingHelpers.FieldSeparator, 3);
            if (f.Length < 3) continue;
            entries.Add(new StashEntry(index++, f[0], f[1], ParsingHelpers.FromUnixSeconds(f[2])));
        }
        return entries;
    }
}

public static class BlameParser
{
    /// <summary><c>git blame --line-porcelain</c> çıktısı.</summary>
    public static IReadOnlyList<BlameLine> Parse(string output)
    {
        var lines = new List<BlameLine>();
        string sha = string.Empty, author = string.Empty, summary = string.Empty, filename = string.Empty;
        long authorTime = 0;
        int originalLine = 0, finalLine = 0;
        var boundary = false;

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                lines.Add(new BlameLine(finalLine, sha, author, DateTimeOffset.FromUnixTimeSeconds(authorTime).ToLocalTime(),
                    summary, originalLine, filename, line[1..], boundary));
                boundary = false;
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];

            if (key.Length >= 40 && key.All(Uri.IsHexDigit))
            {
                sha = key;
                var numbers = value.Split(' ');
                if (numbers.Length >= 2)
                {
                    int.TryParse(numbers[0], out originalLine);
                    int.TryParse(numbers[1], out finalLine);
                }
                continue;
            }

            switch (key)
            {
                case "author": author = value; break;
                case "author-time": long.TryParse(value, out authorTime); break;
                case "summary": summary = value; break;
                case "filename": filename = value; break;
                case "boundary": boundary = true; break;
            }
        }
        return lines;
    }
}

public static partial class MergeTreeParser
{
    [GeneratedRegex(@"^CONFLICT \((?<kind>[^)]+)\): (?<text>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConflictLine();

    [GeneratedRegex(@"^Auto-merging (?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AutoMergingLine();

    /// <summary>
    /// <c>git merge-tree --write-tree --name-only</c> çıktısı: ilk satır ağaç id'si, ardından conflict'li dosyalar,
    /// boş satır, bilgilendirme mesajları.
    /// </summary>
    public static ConflictPrediction Parse(string output, int exitCode)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var tree = lines.Length > 0 && lines[0].Length >= 40 ? lines[0].Trim() : null;
        var hasConflicts = exitCode == 1;

        var conflicted = new List<string>();
        var messages = new List<string>();
        var index = 1;
        if (hasConflicts)
        {
            for (; index < lines.Length && lines[index].Length > 0; index++)
                conflicted.Add(lines[index]);
        }
        for (; index < lines.Length; index++)
        {
            if (lines[index].Length > 0) messages.Add(lines[index]);
        }

        var files = new Dictionary<string, PredictedConflict>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var auto = AutoMergingLine().Match(message);
            if (auto.Success)
            {
                var path = auto.Groups["path"].Value;
                files.TryAdd(path, new PredictedConflict(path, ConflictSeverity.AutoMerged,
                    Loc.T("Changed on both sides and merged automatically. Review recommended.", "İki tarafta da değişti ve otomatik birleştirildi. İncelemeniz önerilir.")));
                continue;
            }

            var conflict = ConflictLine().Match(message);
            if (!conflict.Success) continue;
            var kind = conflict.Groups["kind"].Value;
            var severity = kind == "content" ? ConflictSeverity.Content : ConflictSeverity.Structural;
            var target = conflicted.FirstOrDefault(p => message.Contains(p, StringComparison.Ordinal));
            if (target is null) continue;
            files[target] = new PredictedConflict(target, severity, DescribeConflict(kind));
        }

        foreach (var path in conflicted)
        {
            if (!files.TryGetValue(path, out var existing) || existing.Severity == ConflictSeverity.AutoMerged)
                files[path] = new PredictedConflict(path, ConflictSeverity.Content, DescribeConflict("content"));
        }

        return new ConflictPrediction(hasConflicts, tree, files.Values.OrderByDescending(f => f.Severity).ThenBy(f => f.Path).ToList(), messages);
    }

    private static string DescribeConflict(string kind) => kind switch
    {
        "content" => Loc.T("The same lines were changed differently on both sides.", "Aynı satırlar iki tarafta farklı şekilde değiştirildi."),
        "modify/delete" => Loc.T("One side changed the file, the other deleted it.", "Bir taraf dosyayı değiştirdi, diğeri sildi."),
        "add/add" => Loc.T("Both sides added a file with the same name but different content.", "İki taraf da aynı adda ama farklı içerikte bir dosya ekledi."),
        "rename/delete" => Loc.T("One side renamed the file, the other deleted it.", "Bir taraf dosyayı yeniden adlandırdı, diğeri sildi."),
        "rename/rename" => Loc.T("Both sides renamed the file differently.", "İki taraf dosyayı farklı adlarla yeniden adlandırdı."),
        "file/directory" or "directory/file" => Loc.T("One side has a file where the other has a folder.", "Bir tarafta dosya olan yerde diğer tarafta klasör var."),
        "binary" => Loc.T("A binary file was changed on both sides.", "Bir ikili dosya iki tarafta da değiştirildi."),
        "submodule" => Loc.T("A submodule points to different commits on both sides.", "Bir submodule iki tarafta farklı commit'leri gösteriyor."),
        _ => Loc.T($"Conflict ({kind}).", $"Çakışma ({kind})."),
    };
}

public static class TreeParser
{
    /// <summary><c>git ls-tree -r -z</c>: "&lt;mode&gt; SP &lt;type&gt; SP &lt;object&gt; TAB &lt;path&gt;".</summary>
    public static IReadOnlyList<TreeEntry> ParseLsTree(string output)
    {
        var entries = new List<TreeEntry>();
        foreach (var record in ParsingHelpers.SplitNul(output))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record[..tab].Split(' ');
            if (meta.Length < 3) continue;
            entries.Add(new TreeEntry(ParsingHelpers.ParseMode(meta[0]), meta[1], meta[2], record[(tab + 1)..]));
        }
        return entries;
    }

    /// <summary><c>git ls-files -s -z</c>: "&lt;mode&gt; SP &lt;object&gt; SP &lt;stage&gt; TAB &lt;path&gt;".</summary>
    public static IReadOnlyList<IndexEntry> ParseLsFilesStage(string output)
    {
        var entries = new List<IndexEntry>();
        foreach (var record in ParsingHelpers.SplitNul(output))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record[..tab].Split(' ');
            if (meta.Length < 3) continue;
            entries.Add(new IndexEntry(ParsingHelpers.ParseMode(meta[0]), meta[1], int.Parse(meta[2]), record[(tab + 1)..]));
        }
        return entries;
    }
}

public static class WorktreeParser
{
    /// <summary><c>git worktree list --porcelain -z</c>.</summary>
    public static IReadOnlyList<WorktreeInfo> Parse(string output)
    {
        var result = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null;
        bool bare = false, detached = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is not null) result.Add(new WorktreeInfo(path, head, branch, bare, detached, locked, prunable));
            path = head = branch = null;
            bare = detached = locked = prunable = false;
        }

        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                Flush();
                continue;
            }
            var space = field.IndexOf(' ');
            var key = space < 0 ? field : field[..space];
            var value = space < 0 ? string.Empty : field[(space + 1)..];
            switch (key)
            {
                case "worktree": Flush(); path = value; break;
                case "HEAD": head = value; break;
                case "branch": branch = value; break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "locked": locked = true; break;
                case "prunable": prunable = true; break;
            }
        }
        Flush();
        return result;
    }
}
