using ArChrono.AI.Context;
using ArChrono.AI.Providers;

namespace ArChrono.AI;

/// <summary>AI çıktısı. UI her zaman "AI generated" rozetiyle ve kaynak commit'lerle birlikte gösterir.</summary>
public sealed record AiResult(
    AiFeature Feature,
    string Subject,
    string Text,
    AiProviderKind Provider,
    string Model,
    TimeSpan Duration,
    bool FromCache,
    IReadOnlyList<string> SourceCommits)
{
    public const string Badge = "AI generated";
}
