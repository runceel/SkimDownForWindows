using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>Absolute executable resolution and bounded, noninteractive plumbing commands.</summary>
internal sealed class GitCommandRunner(string executable)
{
    internal static string? ResolveExecutable(string root, string? searchPath = null)
    {
        foreach (var entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var full = Path.GetFullPath(directory);
            if (PathHelpers.IsInsideFolder(root, full) ||
                PathHelpers.IsInsideFolder(Environment.CurrentDirectory, full)) continue;
            var candidate = Path.Combine(full, "git.exe");
            if (File.Exists(candidate) && !HasReparsePoint(candidate)) return candidate;
        }
        return null;
    }

    private static bool HasReparsePoint(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    internal static Dictionary<string, string> ChildEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LC_ALL"] = "C",
            ["GIT_NO_LAZY_FETCH"] = "1",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE" })
        {
            if (Environment.GetEnvironmentVariable(key) is { } value) result[key] = value;
        }
        // No inherited PATH, GIT_*, or shell variables can select helper programs.
        result["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return result;
    }

    internal async Task<byte[]> RunAsync(string root, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, byte[]? input = null, int[]? acceptedExitCodes = null,
        TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.Environment.Clear();
        foreach (var (key, value) in ChildEnvironment()) process.StartInfo.Environment[key] = value;
        foreach (var arg in new[] { "--no-pager", "-c", "core.fsmonitor=false" }.Concat(arguments))
            process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            using var registration = limit.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            });
            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, limit.Token);
            var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, limit.Token);
            var inputTask = WriteInputAsync(process, input, limit.Token);
            await Task.WhenAll(outputTask, errorTask, inputTask, process.WaitForExitAsync(limit.Token));
            limit.Token.ThrowIfCancellationRequested();
            if (!(acceptedExitCodes ?? [0]).Contains(process.ExitCode))
                throw new IOException($"Git {arguments[0]} exited with code {process.ExitCode}.");
            return await outputTask;
        }
        catch
        {
            // Also stop the process on an output-limit or pipe failure, not only on timeout.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            throw;
        }
    }

    private static async Task WriteInputAsync(Process process, byte[]? input, CancellationToken token)
    {
        if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, token);
        process.StandardInput.Close();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + count > 32 * 1024 * 1024)
                throw new IOException("Git output exceeded 32 MiB.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
