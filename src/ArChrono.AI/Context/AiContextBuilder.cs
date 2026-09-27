using System.Security.Cryptography;
using System.Text;
using ArChrono.AI.Privacy;
using ArChrono.AI.Providers;
using ArChrono.Git.Models;
using ArChrono.Localization;

namespace ArChrono.AI.Context;

public enum AiFeature
{
    CommitMessage,
    ExplainCommit,
    ExplainBranch,
    ExplainConflict,
}

public enum CommitMessageStyle
{
    Short,
    Conventional,
    Detailed,
}

/// <summary>AI'ye gönderilecek her şey. UI bunu önizler; kullanıcı onaylamadan gönderilmez (ayar açıksa).</summary>
public sealed record AiPayload(
    AiFeature Feature,
    string Subject,
    AiPrompt Prompt,
    IReadOnlyList<string> IncludedPaths,
    IReadOnlyList<(string Path, string Reason)> ExcludedPaths,
    IReadOnlyList<string> TruncatedPaths,
    int RedactionCount)
{
    public int ByteCount => Encoding.UTF8.GetByteCount(Prompt.System) + Encoding.UTF8.GetByteCount(Prompt.User);

    public string PromptSha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Prompt.System + "\n\n" + Prompt.User)));

    public string FeatureName => Feature switch
    {
        AiFeature.CommitMessage => "commit-message",
        AiFeature.ExplainCommit => "explain-commit",
        AiFeature.ExplainBranch => "explain-branch",
        _ => "explain-conflict",
    };
}

/// <summary>
/// Diff'lerden AI bağlamı üretir: hassas dosyaları çıkarır, sırları maskeler, boyut bütçesini uygular.
/// </summary>
public sealed class AiContextBuilder(SensitivePathFilter filter, int maxPatchCharacters = 40_000)
{
    private const string Grounding =
        "Only describe what the diff actually shows. Never invent ticket numbers, authors, dates or motivations that are not visible. " +
        "If something is unclear, say so briefly. Parts of files may be replaced with [REDACTED] for privacy; do not speculate about them.";

    /// <summary>Açıklamalar arayüz dilinde istenir. Commit mesajları ise repository'nin kendi dilini izler.</summary>
    private static string ResponseLanguage => Loc.IsTurkish ? " Write the whole answer in Turkish, including the section headings." : "";

    public AiPayload BuildCommitMessage(string stagedPatch, CommitMessageStyle style, string? branchName, IReadOnlyList<string> recentSubjects)
    {
        var (patch, included, excluded, truncated, redactions) = PreparePatch(stagedPatch);
        var styleRule = style switch
        {
            CommitMessageStyle.Short => "Write a single line in the imperative mood, at most 72 characters. No body.",
            CommitMessageStyle.Conventional =>
                "Use the Conventional Commits format: type(optional scope): summary. Types: feat, fix, refactor, perf, docs, test, build, ci, chore, style. " +
                "Summary in imperative mood, at most 72 characters. Add a short body only if the change is not obvious from the summary.",
            _ => "Write a summary line (imperative mood, at most 72 characters), a blank line, then a body wrapped at 72 characters that explains what changed and why. Bullet points are fine.",
        };

        var system = "You write git commit messages for the staged changes. Output only the commit message: no quotes, no markdown code fences, no preamble. " +
                     styleRule + " " + Grounding;

        var user = new StringBuilder();
        if (branchName is not null) user.Append("Branch: ").Append(branchName).Append('\n');
        if (recentSubjects.Count > 0)
        {
            user.Append("Recent commit subjects in this repository (match their language and tone):\n");
            foreach (var subject in recentSubjects.Take(8)) user.Append("- ").Append(subject).Append('\n');
        }
        AppendExclusions(user, excluded, truncated);
        user.Append("\nStaged diff:\n").Append(patch);

        return new AiPayload(AiFeature.CommitMessage, branchName ?? "staged", new AiPrompt(system, user.ToString(), 600), included, excluded, truncated, redactions);
    }

    public AiPayload BuildExplainCommit(CommitDetails details, string patch)
    {
        var (prepared, included, excluded, truncated, redactions) = PreparePatch(patch);
        var system =
            "You explain git commits to developers who did not write them. Respond in plain text with exactly these sections:\n" +
            "WHAT CHANGED?\n<one or two sentences>\n\nChanges:\n- <bullet per meaningful change>\n\nPotential impact:\n- <areas that could be affected>\n\n" +
            "Keep it under 180 words. " + Grounding + ResponseLanguage;

        var user = new StringBuilder();
        user.Append("Commit message:\n").Append(details.Message).Append("\n\n");
        user.Append("Files: ").Append(string.Join(", ", details.Files.Select(f => f.Path).Where(p => !filter.IsSensitive(p)).Take(60))).Append('\n');
        AppendExclusions(user, excluded, truncated);
        user.Append("\nDiff:\n").Append(prepared);

        return new AiPayload(AiFeature.ExplainCommit, details.Commit.Sha, new AiPrompt(system, user.ToString(), 700), included, excluded, truncated, redactions);
    }

    public AiPayload BuildExplainBranch(string branch, string baseBranch, IReadOnlyList<CommitInfo> commits, string patch)
    {
        var (prepared, included, excluded, truncated, redactions) = PreparePatch(patch);
        var system =
            "You summarize what a git branch changes compared to its base branch. Respond in plain text with sections:\n" +
            "SUMMARY\n<two or three sentences>\n\nMain changes:\n- <bullets>\n\nRisks to review before merging:\n- <bullets>\n\n" +
            "Keep it under 220 words. " + Grounding + ResponseLanguage;
        var user = new StringBuilder();
        user.Append($"Branch {branch} compared to {baseBranch}. Commits (oldest first):\n");
        foreach (var commit in commits.Take(40)) user.Append("- ").Append(commit.Subject).Append('\n');
        AppendExclusions(user, excluded, truncated);
        user.Append("\nCombined diff:\n").Append(prepared);
        return new AiPayload(AiFeature.ExplainBranch, branch, new AiPrompt(system, user.ToString(), 800), included, excluded, truncated, redactions);
    }

    public AiPayload BuildExplainConflict(string path, string? baseText, string? currentText, string? incomingText)
    {
        if (filter.MatchingPattern(path) is { } pattern)
            return new AiPayload(AiFeature.ExplainConflict, path, new AiPrompt("", "", 1), [], [(path, Loc.T($"matches '{pattern}'", $"'{pattern}' kalıbıyla eşleşiyor"))], [], 0);

        var redactions = 0;
        string Prepare(string? text)
        {
            if (text is null) return "(file does not exist on this side)";
            var (redacted, count) = SecretRedactor.Redact(text.Length > 12_000 ? text[..12_000] + "\n… (truncated)" : text);
            redactions += count;
            return redacted;
        }

        var system =
            "You help resolve a git merge conflict. Respond in plain text with sections:\n" +
            "WHY THIS CONFLICT HAPPENED\n<two sentences>\n\nSuggested resolution\n<explanation>\n\nConfidence: Low | Medium | High\n\n" +
            "Never claim certainty. The user reviews and applies any resolution themselves. " + Grounding + ResponseLanguage;
        var user = $"File: {path}\n\n=== BASE (common ancestor) ===\n{Prepare(baseText)}\n\n=== CURRENT (ours) ===\n{Prepare(currentText)}\n\n=== INCOMING (theirs) ===\n{Prepare(incomingText)}";
        return new AiPayload(AiFeature.ExplainConflict, path, new AiPrompt(system, user, 900), [path], [], [], redactions);
    }

    /// <summary>Model çıktısındaki kod bloğu/tırnak sarmalayıcılarını temizler.</summary>
    public static string CleanCommitMessage(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = cleaned.IndexOf('\n');
            cleaned = firstNewline >= 0 ? cleaned[(firstNewline + 1)..] : cleaned[3..];
            if (cleaned.EndsWith("```", StringComparison.Ordinal)) cleaned = cleaned[..^3];
        }
        cleaned = cleaned.Trim();
        if (cleaned.Length > 1 && cleaned[0] is '"' or '\'' or '`' && cleaned[^1] == cleaned[0]) cleaned = cleaned[1..^1];
        return cleaned.Trim();
    }

    private (string Patch, List<string> Included, List<(string, string)> Excluded, List<string> Truncated, int Redactions) PreparePatch(string patch)
    {
        var included = new List<string>();
        var excluded = new List<(string, string)>();
        var truncated = new List<string>();
        var output = new StringBuilder();
        var redactions = 0;

        foreach (var (path, section) in SplitByFile(patch))
        {
            if (filter.MatchingPattern(path) is { } pattern)
            {
                excluded.Add((path, Loc.T($"matches '{pattern}'", $"'{pattern}' kalıbıyla eşleşiyor")));
                continue;
            }
            if (section.Contains("\nGIT binary patch", StringComparison.Ordinal) || section.Contains("\nBinary files ", StringComparison.Ordinal))
            {
                excluded.Add((path, Loc.T("binary file", "ikili dosya")));
                continue;
            }

            var (redacted, count) = SecretRedactor.Redact(section);
            redactions += count;
            var remaining = maxPatchCharacters - output.Length;
            if (remaining <= 200)
            {
                truncated.Add(path);
                continue;
            }
            if (redacted.Length > remaining)
            {
                output.Append(redacted.AsSpan(0, remaining)).Append("\n… (diff truncated)\n");
                truncated.Add(path);
            }
            else
            {
                output.Append(redacted);
            }
            included.Add(path);
        }
        return (output.ToString(), included, excluded, truncated, redactions);
    }

    internal static IEnumerable<(string Path, string Section)> SplitByFile(string patch)
    {
        var sections = patch.Split("\ndiff --git ");
        for (var i = 0; i < sections.Length; i++)
        {
            var section = i == 0 ? sections[i] : "diff --git " + sections[i];
            if (!section.StartsWith("diff --git ", StringComparison.Ordinal)) continue;
            if (!section.EndsWith('\n')) section += "\n";
            var firstLine = section[..section.IndexOf('\n')];
            var marker = firstLine.LastIndexOf(" b/", StringComparison.Ordinal);
            var path = marker > 0 ? firstLine[(marker + 3)..] : firstLine;
            yield return (path.Trim('"'), section);
        }
    }

    private static void AppendExclusions(StringBuilder user, IReadOnlyList<(string Path, string Reason)> excluded, IReadOnlyList<string> truncated)
    {
        if (excluded.Count > 0) user.Append($"({excluded.Count} file(s) were not shared for privacy or because they are binary.)\n");
        if (truncated.Count > 0) user.Append($"({truncated.Count} file(s) were shortened to fit the size limit.)\n");
    }
}
