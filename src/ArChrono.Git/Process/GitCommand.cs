using System.Text;

namespace ArChrono.Git.Process;

/// <summary>Tek bir git çağrısının tanımı. Argümanlar shell'den geçmez.</summary>
public sealed record GitCommand(string WorkingDirectory, IReadOnlyList<string> Arguments)
{
    /// <summary>Durum değiştirmeyen okuma komutu: GIT_OPTIONAL_LOCKS=0 ile index.lock alınmaz.</summary>
    public bool ReadOnly { get; init; }

    public byte[]? StandardInput { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>stderr satırlarını (clone/fetch ilerlemesi) canlı iletir.</summary>
    public IProgress<string>? Progress { get; init; }

    /// <summary>Git Console'da gizlenecek iç komutlar (snapshot, pin vb.).</summary>
    public bool IsInternal { get; init; }

    public static GitCommand Create(string workingDirectory, params string[] arguments) =>
        new(workingDirectory, arguments);

    public GitCommand WithInput(string text) => this with { StandardInput = Encoding.UTF8.GetBytes(text) };

    public string DisplayText => "git " + string.Join(' ', Arguments.Select(QuoteForDisplay));

    internal static string QuoteForDisplay(string argument)
    {
        var masked = SecretMasker.Mask(argument);
        if (masked.Length == 0) return "\"\"";
        return masked.IndexOfAny([' ', '"', '\'', '\t']) >= 0 ? "\"" + masked.Replace("\"", "\\\"") + "\"" : masked;
    }
}

public sealed record GitCommandResult(GitCommand Command, int ExitCode, byte[] StandardOutputBytes, string StandardError, TimeSpan Duration)
{
    private string? _stdout;

    public string StandardOutput => _stdout ??= Encoding.UTF8.GetString(StandardOutputBytes);

    public bool Success => ExitCode == 0;

    public string CombinedOutput => string.IsNullOrEmpty(StandardError) ? StandardOutput : StandardOutput + StandardError;

    /// <summary>Başarısızsa kullanıcı dostu <see cref="Errors.GitException"/> fırlatır.</summary>
    public GitCommandResult EnsureSuccess()
    {
        if (!Success) throw new Errors.GitException(Errors.GitErrorTranslator.Translate(this));
        return this;
    }
}

public sealed class GitCommandEventArgs(GitCommand command, GitCommandResult? result) : EventArgs
{
    public GitCommand Command { get; } = command;
    public GitCommandResult? Result { get; } = result;
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
}

public interface IGitRunner
{
    GitExecutable Executable { get; }

    event EventHandler<GitCommandEventArgs>? CommandStarted;
    event EventHandler<GitCommandEventArgs>? CommandCompleted;

    Task<GitCommandResult> RunAsync(GitCommand command, CancellationToken cancellationToken = default);
}
