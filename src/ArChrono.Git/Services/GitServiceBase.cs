using System.Text;
using ArChrono.Git.Models;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

/// <summary>Alan servislerinin ortak yardımcıları. Servisler yalnızca <see cref="IGitRunner"/> üzerinden git çalıştırır.</summary>
public abstract class GitServiceBase(IGitRunner runner, RepositoryInfo repository)
{
    protected static readonly IReadOnlyDictionary<string, string> LiteralPathspecs =
        new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "1" };

    protected IGitRunner Runner { get; } = runner;

    public RepositoryInfo Repository { get; } = repository;

    protected Task<GitCommandResult> ExecuteAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool readOnly = false,
        byte[]? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IProgress<string>? progress = null,
        bool isInternal = false,
        TimeSpan? timeout = null)
    {
        var command = new GitCommand(Repository.RootPath, arguments.ToArray())
        {
            ReadOnly = readOnly,
            StandardInput = standardInput,
            Environment = environment,
            Progress = progress,
            IsInternal = isInternal,
            Timeout = timeout,
        };
        return Runner.RunAsync(command, cancellationToken);
    }

    protected async Task<string> ReadAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ExecuteAsync(arguments, cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.EnsureSuccess().StandardOutput;
    }

    protected async Task<GitCommandResult> WriteAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        return result.EnsureSuccess();
    }

    /// <summary>Çok sayıda yolu argv sınırına takılmadan iletmek için NUL ile birleştirir.</summary>
    protected static byte[] NulJoined(IEnumerable<string> paths) =>
        Encoding.UTF8.GetBytes(string.Join('\0', paths) + "\0");

    protected static string[] Args(params IEnumerable<string>[] groups) => groups.SelectMany(g => g).ToArray();
}
