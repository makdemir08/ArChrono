using System.Text.RegularExpressions;

namespace ArChrono.AI.Privacy;

/// <summary>
/// AI bağlamından hariç tutulacak dosyalar (gitignore benzeri kalıplar).
/// "secrets/" her derinlikte klasör, "*.pem" her derinlikte dosya adı, "config/prod.json" kökten yol.
/// </summary>
public sealed class SensitivePathFilter
{
    public static readonly IReadOnlyList<string> DefaultPatterns =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.p12", "*.pfx", "*.keystore", "*.jks",
        "id_rsa*", "id_ed25519*", "id_ecdsa*", ".npmrc", ".pypirc", ".netrc", "*.tfvars", "*.tfstate",
        "secrets/", "credentials/", ".aws/", ".ssh/",
    ];

    private readonly List<(Regex Regex, string Pattern)> _rules;

    public SensitivePathFilter(IEnumerable<string>? patterns = null)
    {
        Patterns = (patterns ?? DefaultPatterns).Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith('#')).Distinct().ToList();
        _rules = Patterns.Select(p => (Compile(p), p)).ToList();
    }

    public IReadOnlyList<string> Patterns { get; }

    public bool IsSensitive(string path) => MatchingPattern(path) is not null;

    public string? MatchingPattern(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        foreach (var (regex, pattern) in _rules)
        {
            if (regex.IsMatch(normalized)) return pattern;
        }
        return null;
    }

    private static Regex Compile(string pattern)
    {
        var directoryOnly = pattern.EndsWith('/');
        var body = pattern.TrimEnd('/');
        var anchored = body.StartsWith('/') || body.Contains('/');
        body = body.TrimStart('/');

        var glob = Regex.Escape(body).Replace(@"\*\*", "").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("", ".*");
        var prefix = anchored ? "^" : "(^|/)";
        var suffix = directoryOnly ? "/" : "($|/)";
        return new Regex(prefix + glob + suffix, RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase));
    }
}

/// <summary>AI'ye gönderilecek metindeki olası sırları maskeler.</summary>
public static partial class SecretRedactor
{
    public const string Mask = "[REDACTED]";

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,}|xox[baprs]-[A-Za-z0-9-]{10,}|sk-(?:ant-)?[A-Za-z0-9_\-]{20,}|AIza[0-9A-Za-z_\-]{35}|glpat-[A-Za-z0-9_\-]{20,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokens();

    [GeneratedRegex(
        @"(?<key>(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key|client[_-]?secret|token|connectionstring|auth)[A-Za-z0-9_]*)(?<sep>\s*[:=]\s*|""\s*:\s*"")(?<quote>[""']?)(?<value>[^""'\s,;]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignments();

    public static (string Text, int Redactions) Redact(string text)
    {
        var count = 0;
        text = PrivateKeyBlock().Replace(text, _ =>
        {
            count++;
            return Mask;
        });
        text = KnownTokens().Replace(text, _ =>
        {
            count++;
            return Mask;
        });
        text = Assignments().Replace(text, m =>
        {
            if (m.Groups["value"].Value == Mask) return m.Value;
            count++;
            return m.Groups["key"].Value + m.Groups["sep"].Value + m.Groups["quote"].Value + Mask;
        });
        return (text, count);
    }
}
