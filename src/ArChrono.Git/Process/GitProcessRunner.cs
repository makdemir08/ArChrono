using System.Diagnostics;
using System.Text;

namespace ArChrono.Git.Process;

/// <summary>
/// Git adapter'ın tek giriş noktası. Tüm git çağrıları buradan geçer: sabit ortam,
/// eşzamanlı stdout/stderr okuma, iptal/zaman aşımı ve komut olayları.
/// </summary>
public sealed class GitProcessRunner(GitExecutable executable) : IGitRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public GitExecutable Executable { get; } = executable;

    public event EventHandler<GitCommandEventArgs>? CommandStarted;
    public event EventHandler<GitCommandEventArgs>? CommandCompleted;

    public async Task<GitCommandResult> RunAsync(GitCommand command, CancellationToken cancellationToken = default)
    {
        using var process = new System.Diagnostics.Process { StartInfo = CreateStartInfo(command) };

        using var timeout = new CancellationTokenSource(command.Timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        CommandStarted?.Invoke(this, new GitCommandEventArgs(command, null));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var failed = new GitCommandResult(command, -1, [], $"Could not start git: {ex.Message}", stopwatch.Elapsed);
            CommandCompleted?.Invoke(this, new GitCommandEventArgs(command, failed));
            return failed;
        }

        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, linked.Token);
        var stderrTask = ReadStandardErrorAsync(process.StandardError, command.Progress, linked.Token);

        try
        {
            try
            {
                if (command.StandardInput is { } input)
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, linked.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
                }
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Süreç stdin'i okumadan çıktıysa (broken pipe) sonucu yine de çıkış koduyla değerlendir.
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var result = new GitCommandResult(command, process.ExitCode, stdout, stderr, stopwatch.Elapsed);
            CommandCompleted?.Invoke(this, new GitCommandEventArgs(command, result));
            return result;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            var message = timedOut ? "The git command timed out." : "The git command was cancelled.";
            var result = new GitCommandResult(command, timedOut ? -2 : -3, [], message, stopwatch.Elapsed);
            CommandCompleted?.Invoke(this, new GitCommandEventArgs(command, result));
            if (!timedOut) cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private ProcessStartInfo CreateStartInfo(GitCommand command)
    {
        var info = new ProcessStartInfo(Executable.Path)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Makine tarafından okunabilir ve yerelleştirilmemiş çıktı.
        info.ArgumentList.Add("--no-pager");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.quotepath=false");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("color.ui=never");
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);

        var env = info.Environment;
        env["LC_ALL"] = "C";
        env["LANGUAGE"] = "C";
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_PAGER"] = "cat";
        env["PAGER"] = "cat";
        // GUI hiçbir zaman etkileşimli editör açmaz; varsayılan mesaj kabul edilir.
        env["GIT_EDITOR"] = "true";
        env["GIT_MERGE_AUTOEDIT"] = "no";
        if (command.ReadOnly) env["GIT_OPTIONAL_LOCKS"] = "0";

        if (command.Environment is not null)
        {
            foreach (var (key, value) in command.Environment) env[key] = value;
        }
        return info;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, 81_920, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<string> ReadStandardErrorAsync(StreamReader reader, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (progress is null) return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // İlerleme satırları '\r' ile güncellenir; her parçayı ayrı rapor et.
        var all = new StringBuilder();
        var line = new StringBuilder();
        var chunk = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            all.Append(chunk, 0, read);
            for (var i = 0; i < read; i++)
            {
                var c = chunk[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0) progress.Report(line.ToString());
                    line.Clear();
                }
                else
                {
                    line.Append(c);
                }
            }
        }
        if (line.Length > 0) progress.Report(line.ToString());
        return all.ToString();
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
