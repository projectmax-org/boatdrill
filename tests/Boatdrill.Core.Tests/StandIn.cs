namespace Boatdrill.Core.Tests;

/// <summary>
/// A stand-in for restic, kopia or rclone: a script (a .cmd on Windows, a sh script elsewhere) that answers each
/// command the way a test says, so that what Boatdrill makes of a tool's failure can be tested without the tool
/// and without the fault. What it prints comes from files beside it, written here as they are.
/// </summary>
internal static class StandIn
{
    /// <summary>What the stand-in does when it is given <paramref name="Command"/>.</summary>
    /// <param name="Output">What it writes to standard output.</param>
    /// <param name="Error">What it writes to standard error.</param>
    internal sealed record Answer(string Command, int ExitCode = 0, string Output = "", string Error = "")
    {
        /// <summary>Lines of a .cmd to run first, for an answer that leaves files behind as the tool would.</summary>
        public string[] Windows { get; init; } = [];

        /// <summary>The same in sh, for everywhere else.</summary>
        public string[] Unix { get; init; } = [];
    }

    /// <summary>Writes the script into <paramref name="folder"/> and gives its path, to name as a destination's tool.</summary>
    /// <param name="word">
    /// Which argument is the command, counted from 1 as the script counts them: restic's third, after --no-lock
    /// --quiet, and kopia's first. A .cmd splits an argument at an equals sign, so --ask-password=false is two there.
    /// </param>
    public static string Write(TempTree folder, string tool, int word, Answer[] answers)
    {
        for (var i = 0; i < answers.Length; i++)
        {
            folder.Write($"{tool}-{i}.out", answers[i].Output);
            folder.Write($"{tool}-{i}.err", answers[i].Error);
        }

        if (OperatingSystem.IsWindows()) return folder.Write(tool + ".cmd", Script(tool, word, answers, windows: true));

        var path = folder.Write(tool, Script(tool, word, answers, windows: false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The script itself, its lines ended as its interpreter wants them whatever this file's own line endings are.</summary>
    internal static string Script(string tool, int word, Answer[] answers, bool windows)
    {
        List<string> lines;
        if (windows)
        {
            lines = ["@echo off"];
            for (var i = 0; i < answers.Length; i++) lines.Add($"if \"%~{word}\"==\"{answers[i].Command}\" goto answer{i}");
            lines.AddRange([">&2 echo The stand-in has no answer for this command.", "exit /b 64"]);
            for (var i = 0; i < answers.Length; i++)
            {
                lines.Add($":answer{i}");
                lines.AddRange(answers[i].Windows);
                lines.AddRange([$"type \"%~dp0{tool}-{i}.out\"", $">&2 type \"%~dp0{tool}-{i}.err\"", $"exit /b {answers[i].ExitCode}"]);
            }

            return string.Join("\r\n", lines) + "\r\n";
        }

        lines = ["#!/bin/sh", "here=$(dirname \"$0\")", $"case \"${word}\" in"];
        for (var i = 0; i < answers.Length; i++)
        {
            lines.Add($"  {answers[i].Command})");
            lines.AddRange(answers[i].Unix.Select(line => "    " + line));
            lines.AddRange([$"    cat \"$here/{tool}-{i}.out\"", $"    cat \"$here/{tool}-{i}.err\" >&2", $"    exit {answers[i].ExitCode}", "    ;;"]);
        }

        lines.AddRange(["esac", "echo 'The stand-in has no answer for this command.' >&2", "exit 64"]);
        return string.Join("\n", lines) + "\n";
    }
}
