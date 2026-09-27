using System.Text.RegularExpressions;
using ArChrono.Git.Models;

namespace ArChrono.Git.Parsing;

/// <summary>Unified diff (<c>git diff --no-color</c>) çıktısını dosya/hunk/satır modeline çevirir.</summary>
public static partial class DiffParser
{
    public const int DefaultMaxLinesPerFile = 20_000;

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();

    public static IReadOnlyList<FileDiff> Parse(string diff, int maxLinesPerFile = DefaultMaxLinesPerFile)
    {
        var files = new List<FileDiff>();
        var builder = (FileBuilder?)null;

        using var reader = new StringReader(diff);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("diff --cc ", StringComparison.Ordinal))
            {
                if (builder is not null) files.Add(builder.Build());
                builder = new FileBuilder(maxLinesPerFile);
                builder.ParseDiffGitLine(line);
                continue;
            }
            builder?.Consume(line);
        }
        if (builder is not null) files.Add(builder.Build());
        return files;
    }

    private sealed class FileBuilder(int maxLines)
    {
        private string? _oldPath, _newPath;
        private int? _oldMode, _newMode;
        private ChangeKind _change = ChangeKind.Modified;
        private bool _binary, _truncated, _inHunks;
        private readonly List<DiffHunk> _hunks = [];
        private List<DiffLine>? _lines;
        private int _oldStart, _oldCount, _newStart, _newCount, _oldLine, _newLine, _totalLines;
        private string _header = string.Empty;

        public void ParseDiffGitLine(string line)
        {
            // "diff --git a/x b/y" — boşluk içeren yollarda belirsizdir; ---/+++ ve rename satırları önceliklidir.
            var rest = line["diff --git ".Length..];
            if (rest.StartsWith("\"", StringComparison.Ordinal) || rest.Contains(" \"b/", StringComparison.Ordinal))
                return;
            var middle = rest.IndexOf(" b/", StringComparison.Ordinal);
            if (rest.StartsWith("a/", StringComparison.Ordinal) && middle > 0)
            {
                _oldPath = rest[2..middle];
                _newPath = rest[(middle + 3)..];
            }
        }

        public void Consume(string line)
        {
            if (_inHunks)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    StartHunk(line);
                    return;
                }
                ConsumeHunkLine(line);
                return;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                _inHunks = true;
                StartHunk(line);
            }
            else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                _change = ChangeKind.Added;
                _newMode = ParsingHelpers.ParseMode(line.AsSpan("new file mode ".Length));
                _oldPath = null;
            }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                _change = ChangeKind.Deleted;
                _oldMode = ParsingHelpers.ParseMode(line.AsSpan("deleted file mode ".Length));
                _newPath = null;
            }
            else if (line.StartsWith("old mode ", StringComparison.Ordinal))
                _oldMode = ParsingHelpers.ParseMode(line.AsSpan("old mode ".Length));
            else if (line.StartsWith("new mode ", StringComparison.Ordinal))
                _newMode = ParsingHelpers.ParseMode(line.AsSpan("new mode ".Length));
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                _change = ChangeKind.Renamed;
                _oldPath = ParsingHelpers.UnquotePath(line["rename from ".Length..]);
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                _newPath = ParsingHelpers.UnquotePath(line["rename to ".Length..]);
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                _change = ChangeKind.Copied;
                _oldPath = ParsingHelpers.UnquotePath(line["copy from ".Length..]);
            }
            else if (line.StartsWith("copy to ", StringComparison.Ordinal))
                _newPath = ParsingHelpers.UnquotePath(line["copy to ".Length..]);
            else if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                var space = line.LastIndexOf(' ');
                if (space > "index ".Length && _oldMode is null && _newMode is null)
                {
                    var mode = ParsingHelpers.ParseMode(line.AsSpan(space + 1));
                    if (mode != 0) _oldMode = _newMode = mode;
                }
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
                _binary = true;
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = StripPrefix(line[4..]);
                if (path is not null) _oldPath = path;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = StripPrefix(line[4..]);
                if (path is not null) _newPath = path;
            }
        }

        private static string? StripPrefix(string raw)
        {
            var path = ParsingHelpers.UnquotePath(raw.TrimEnd('\t'));
            if (path == "/dev/null") return null;
            // Diff komutları her zaman --src-prefix=a/ --dst-prefix=b/ ile çalıştırılır (diff.noprefix ayarından bağımsız).
            return path.Length > 2 && path[1] == '/' && path[0] is 'a' or 'b' ? path[2..] : path;
        }

        private void StartHunk(string line)
        {
            FlushHunk();
            var match = HunkHeader().Match(line);
            if (!match.Success)
            {
                _lines = null;
                return;
            }
            _oldStart = int.Parse(match.Groups[1].Value);
            _oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
            _newStart = int.Parse(match.Groups[3].Value);
            _newCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
            _header = match.Groups[5].Value;
            _oldLine = _oldStart;
            _newLine = _newStart;
            _lines = [];
        }

        private void ConsumeHunkLine(string line)
        {
            if (_lines is null) return;
            if (_totalLines >= maxLines)
            {
                _truncated = true;
                return;
            }
            _totalLines++;

            if (line.Length == 0)
            {
                _lines.Add(new DiffLine(DiffLineKind.Context, string.Empty, _oldLine++, _newLine++));
                return;
            }
            switch (line[0])
            {
                case '+': _lines.Add(new DiffLine(DiffLineKind.Added, line[1..], null, _newLine++)); break;
                case '-': _lines.Add(new DiffLine(DiffLineKind.Removed, line[1..], _oldLine++, null)); break;
                case ' ': _lines.Add(new DiffLine(DiffLineKind.Context, line[1..], _oldLine++, _newLine++)); break;
                case '\\': _lines.Add(new DiffLine(DiffLineKind.NoNewlineMarker, line[1..].Trim(), null, null)); break;
            }
        }

        private void FlushHunk()
        {
            if (_lines is null) return;
            _hunks.Add(new DiffHunk(_oldStart, _oldCount, _newStart, _newCount, _header, _lines));
            _lines = null;
        }

        public FileDiff Build()
        {
            FlushHunk();
            if (_change == ChangeKind.Modified && _oldPath is null && _newPath is not null) _change = ChangeKind.Added;
            if (_change == ChangeKind.Modified && _newPath is null && _oldPath is not null) _change = ChangeKind.Deleted;
            if (_change == ChangeKind.Modified && _oldMode is not null && _newMode is not null && ((_oldMode ^ _newMode) & 0xF000) != 0)
                _change = ChangeKind.TypeChanged;
            return new FileDiff(_oldPath, _newPath, _change, _binary, _oldMode, _newMode, _hunks, _truncated);
        }
    }
}
