using ArChrono.Git.Models;

namespace ArChrono.Git.Parsing;

/// <summary><c>git status --porcelain=v2 --branch -z</c> çıktısını ayrıştırır.</summary>
public static class StatusParser
{
    public static (BranchStatus Branch, IReadOnlyList<StatusEntry> Entries) Parse(string output)
    {
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0;
        var entries = new List<StatusEntry>();

        var parts = ParsingHelpers.SplitNul(output);
        for (var i = 0; i < parts.Length; i++)
        {
            var line = parts[i];
            if (line.Length == 0) continue;

            switch (line[0])
            {
                case '#':
                    ParseHeader(line, ref oid, ref head, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                    entries.Add(ParseOrdinary(line));
                    break;
                case '2':
                    // -z: yeni yol kayıtta, eski yol bir sonraki NUL parçasında.
                    var original = i + 1 < parts.Length ? parts[++i] : null;
                    entries.Add(ParseRenamed(line, original));
                    break;
                case 'u':
                    entries.Add(ParseUnmerged(line));
                    break;
                case '?':
                    entries.Add(new StatusEntry(line[2..], null, ChangeKind.Untracked, ChangeKind.Untracked, false, 0, 0, 0, null, null));
                    break;
                case '!':
                    entries.Add(new StatusEntry(line[2..], null, ChangeKind.Ignored, ChangeKind.Ignored, false, 0, 0, 0, null, null));
                    break;
            }
        }

        var branch = new BranchStatus(
            oid is null or "(initial)" ? null : oid,
            head is null or "(detached)" ? null : head,
            upstream, ahead, behind);
        return (branch, entries);
    }

    private static void ParseHeader(string line, ref string? oid, ref string? head, ref string? upstream, ref int ahead, ref int behind)
    {
        // "# branch.oid <sha>" gibi
        var body = line.AsSpan(2);
        var space = body.IndexOf(' ');
        if (space < 0) return;
        var key = body[..space].ToString();
        var value = body[(space + 1)..].ToString();
        switch (key)
        {
            case "branch.oid": oid = value; break;
            case "branch.head": head = value; break;
            case "branch.upstream": upstream = value; break;
            case "branch.ab":
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith('+')) int.TryParse(token[1..], out ahead);
                    else if (token.StartsWith('-')) int.TryParse(token[1..], out behind);
                }
                break;
        }
    }

    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
    private static StatusEntry ParseOrdinary(string line)
    {
        var f = line.Split(' ', 9);
        return new StatusEntry(
            f[8], null,
            ParsingHelpers.ParseChangeLetter(f[1][0]), ParsingHelpers.ParseChangeLetter(f[1][1]),
            f[2][0] == 'S',
            ParsingHelpers.ParseMode(f[3]), ParsingHelpers.ParseMode(f[4]), ParsingHelpers.ParseMode(f[5]),
            NullIfZero(f[6]), NullIfZero(f[7]));
    }

    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>
    private static StatusEntry ParseRenamed(string line, string? originalPath)
    {
        var f = line.Split(' ', 10);
        return new StatusEntry(
            f[9], originalPath,
            ParsingHelpers.ParseChangeLetter(f[1][0]), ParsingHelpers.ParseChangeLetter(f[1][1]),
            f[2][0] == 'S',
            ParsingHelpers.ParseMode(f[3]), ParsingHelpers.ParseMode(f[4]), ParsingHelpers.ParseMode(f[5]),
            NullIfZero(f[6]), NullIfZero(f[7]));
    }

    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
    private static StatusEntry ParseUnmerged(string line)
    {
        var f = line.Split(' ', 11);
        return new StatusEntry(
            f[10], null,
            ChangeKind.Unmerged, ChangeKind.Unmerged,
            f[2][0] == 'S',
            ParsingHelpers.ParseMode(f[3]), ParsingHelpers.ParseMode(f[5]), ParsingHelpers.ParseMode(f[6]),
            NullIfZero(f[7]), NullIfZero(f[8]),
            ConflictCode: f[1]);
    }

    private static string? NullIfZero(string sha) => ParsingHelpers.IsNullSha(sha) ? null : sha;
}
