using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Boatdrill.Core.Text;

namespace Boatdrill.Core.Tools;

/// <summary>
/// Runs a command-line tool such as restic: arguments passed one by one (never through a shell), extra
/// environment variables for secrets (never on the command line, where other programs can read them), a time
/// limit so a stalled network cannot hold a drill forever, and the process tree killed when the drill stops.
/// </summary>
internal static class ToolRunner
{
    public sealed record Result(int ExitCode, string Output, string Error)
    {
        /// <summary>The tool could not be started at all.</summary>
        public bool NotStarted => ExitCode == NotStartedCode;

        /// <summary>The tool was stopped because it ran past its time limit.</summary>
        public bool TimedOut => ExitCode == TimedOutCode;
    }

    private const int NotStartedCode = int.MinValue;
    private const int TimedOutCode = int.MinValue + 1;

    /// <param name="output">When given, standard output is copied into it raw, as `restic dump` writes a file.</param>
    /// <param name="line">When given, standard output is handed over line by line, as `restic ls --json` streams nodes.</param>
    public static async Task<Result> RunAsync(string tool, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeLimit, CancellationToken ct, Stream? output = null, Action<string>? line = null)
    {
        var info = new ProcessStartInfo(tool)
        {
            // Standard input is closed at once, so a tool that asks for a password fails instead of waiting for an answer.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Tools like restic write UTF-8; the console code page (437, 949, ...) would garble file names.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Win32Exception ex)
        {
            return new Result(NotStartedCode, string.Empty, ex.Message);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeLimit);
        using var stop = limit.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }
        });

        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var text = string.Empty;
            if (output is not null)
            {
                await process.StandardOutput.BaseStream.CopyToAsync(output, limit.Token).ConfigureAwait(false);
            }
            else if (line is not null)
            {
                string? next;
                while ((next = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false)) is not null) line(next);
            }
            else
            {
                text = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new Result(process.ExitCode, text, await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(TimedOutCode, string.Empty, Language.Pick($"it did not finish within {Humanize.Duration(timeLimit)}", $"{Humanize.Duration(timeLimit)} 안에 끝나지 않았습니다"));
        }
    }
}
