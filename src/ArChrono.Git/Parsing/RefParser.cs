using ArChrono.Git.Models;

namespace ArChrono.Git.Parsing;

/// <summary><c>git for-each-ref</c> çıktıları.</summary>
public static class RefParser
{
    public const string RefFormat =
        "%(refname)%1f%(objectname)%1f%(*objectname)%1f%(objecttype)%1f%(upstream)%1f%(upstream:track,nobracket)%1f%(HEAD)%1f%(committerdate:unix)%1f%(*committerdate:unix)%1f%(creatordate:unix)%1f%(contents:subject)%1e";

    public static IReadOnlyList<RefInfo> ParseRefs(string output)
    {
        var refs = new List<RefInfo>();
        foreach (var record in output.Split(ParsingHelpers.RecordSeparator))
        {
            var line = record.Trim('\n', '\r');
            if (line.Length == 0) continue;
            var f = line.Split(ParsingHelpers.FieldSeparator);
            if (f.Length < 11) continue;

            var fullName = f[0];
            // origin/HEAD sembolik ref'i listede gösterilmez.
            if (fullName.StartsWith(RefNames.RemotesPrefix, StringComparison.Ordinal) && fullName.EndsWith("/HEAD", StringComparison.Ordinal))
                continue;

            var kind = fullName switch
            {
                _ when fullName.StartsWith(RefNames.HeadsPrefix, StringComparison.Ordinal) => RefKind.LocalBranch,
                _ when fullName.StartsWith(RefNames.RemotesPrefix, StringComparison.Ordinal) => RefKind.RemoteBranch,
                _ when fullName.StartsWith(RefNames.TagsPrefix, StringComparison.Ordinal) => RefKind.Tag,
                RefNames.Stash => RefKind.Stash,
                _ => RefKind.Other,
            };

            var isAnnotatedTag = f[3] == "tag" && f[2].Length > 0;
            var target = isAnnotatedTag ? f[2] : f[1];
            var dateText = isAnnotatedTag ? f[8] : f[7];
            if (string.IsNullOrEmpty(dateText)) dateText = f[9];
            DateTimeOffset? date = long.TryParse(dateText, out _) ? ParsingHelpers.FromUnixSeconds(dateText) : null;

            var (ahead, behind, gone) = ParseTrack(f[5]);
            refs.Add(new RefInfo(
                fullName, kind, target,
                string.IsNullOrEmpty(f[4]) ? null : f[4],
                ahead, behind, gone,
                f[6] == "*",
                date,
                string.Join(ParsingHelpers.FieldSeparator, f.Skip(10)),
                isAnnotatedTag));
        }
        return refs;
    }

    /// <summary>"ahead 2, behind 1" | "gone" | "".</summary>
    internal static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track == "gone") return (0, 0, true);
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = part.Split(' ');
            if (tokens.Length != 2) continue;
            if (tokens[0] == "ahead") int.TryParse(tokens[1], out ahead);
            else if (tokens[0] == "behind") int.TryParse(tokens[1], out behind);
        }
        return (ahead, behind, false);
    }

    /// <summary><c>for-each-ref --format=%(refname)%1f%(objectname)</c>.</summary>
    public static IReadOnlyDictionary<string, string> ParseRefTargets(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(ParsingHelpers.FieldSeparator);
            if (f.Length >= 2 && !(f[0].StartsWith(RefNames.RemotesPrefix, StringComparison.Ordinal) && f[0].EndsWith("/HEAD", StringComparison.Ordinal)))
                result[f[0]] = f[1].Trim();
        }
        return result;
    }
}
