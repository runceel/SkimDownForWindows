using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Application.Models;
using SkimDownForWindows.Application.Utilities;

namespace SkimDownForWindows.Infrastructure.IO;

/// <summary>Read-only working-tree root comparisons; no status, diff, filters, or hooks.</summary>
internal sealed class LocalGitChangeSource(IUiDispatcher dispatcher, IAppLogger logger) : IGitChangeSource
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private Timer? _debounce;
    private string? _watchedRoot;
    private bool _disposed;
    private int _watchGeneration;
    public event Action? RepositoryChanged;

    public async Task<GitChanges?> ReadAsync(string root, IReadOnlyList<string> relativePaths,
        string? selectedPath, CancellationToken cancellationToken)
    {
        int watchGeneration;
        lock (_gate) watchGeneration = _watchGeneration;
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || (!Directory.Exists(Path.Combine(root, ".git")) &&
                              !File.Exists(Path.Combine(root, ".git")))) return null;
            var executable = GitCommandRunner.ResolveExecutable(root);
            if (executable is null)
            {
                logger.LogWarning("Git changes unavailable: git.exe was not found on a safe absolute PATH entry.");
                return null;
            }
            var runner = new GitCommandRunner(executable);
            var info = Text(await runner.RunAsync(root,
                ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-dir", "--git-common-dir",
                    "--show-object-format"], cancellationToken)).TrimEnd('\n').Split('\n');
            if (info.Length != 4 ||
                !string.Equals(PathHelpers.Canonicalize(info[0]), PathHelpers.Canonicalize(root),
                    StringComparison.OrdinalIgnoreCase) || info[3] is not ("sha1" or "sha256")) return null;
            cancellationToken.ThrowIfCancellationRequested();
            Watch(root, info[1], info[2], watchGeneration);

            var head = new Dictionary<string, string>(StringComparer.Ordinal);
            var headId = await runner.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"],
                cancellationToken, acceptedExitCodes: [0, 1]);
            if (headId.Length > 0)
            {
                // Pin the tree to one commit even if HEAD changes during this read.
                var commit = Text(headId).Trim();
                if (!IsObjectId(commit)) throw new IOException("Invalid Git HEAD object ID.");
                var tree = await runner.RunAsync(root, ["ls-tree", "-r", "-z", commit], cancellationToken);
                foreach (var record in Records(tree))
                {
                    var tab = record.IndexOf('\t');
                    if (tab < 0) throw new IOException("Invalid Git tree record.");
                    var fields = record[..tab].Split(' ');
                    if (fields.Length != 3 || fields[1] != "blob") continue;
                    if (!IsObjectId(fields[2])) throw new IOException("Invalid Git blob object ID.");
                    head[record[(tab + 1)..]] = fields[2];
                }
            }
            var index = Records(await runner.RunAsync(root, ["ls-files", "-z", "--cached"], cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            var candidates = relativePaths.Append(selectedPath).OfType<string>()
                .Distinct(StringComparer.Ordinal).Where(p => !head.ContainsKey(p) && !index.Contains(p)).ToArray();
            var ignored = new HashSet<string>(StringComparer.Ordinal);
            if (candidates.Length > 0)
            {
                var bytes = Encoding.UTF8.GetBytes(string.Join('\0', candidates) + '\0');
                ignored = Records(await runner.RunAsync(root, ["check-ignore", "-z", "--stdin"],
                    cancellationToken, bytes, [0, 1])).ToHashSet(StringComparer.Ordinal);
            }

            var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = SafeFilePath(root, path);
                if (full is null) continue;
                if (head.TryGetValue(path, out var blob))
                {
                    var data = await ReadFileAsync(full, cancellationToken);
                    if (BlobId(data, info[3]) != blob &&
                        BlobId(Encoding.UTF8.GetBytes(Text(data)), info[3]) != blob)
                        statuses[path] = "M";
                }
                else if (index.Contains(path)) statuses[path] = "A";
                else if (!ignored.Contains(path)) statuses[path] = "U";
            }
            string? baseText = null;
            if (selectedPath is not null && SafeFilePath(root, selectedPath) is not null)
            {
                if (head.TryGetValue(selectedPath, out var blob))
                    baseText = Text(await runner.RunAsync(root, ["cat-file", "blob", blob], cancellationToken));
                else if (!ignored.Contains(selectedPath)) baseText = "";
            }
            return new GitChanges(statuses, baseText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                   or OperationCanceledException or ArgumentException)
        {
            logger.LogWarning($"Git changes lookup failed: {ex.Message}");
            return null;
        }
        finally { _serial.Release(); }
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, useAsync: true);
        if (stream.Length > 32 * 1024 * 1024) throw new IOException("Git comparison file exceeded 32 MiB.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 32 * 1024 * 1024)
                throw new IOException("Git comparison file exceeded 32 MiB.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static string? SafeFilePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!PathHelpers.IsInsideFolder(root, full) || !File.Exists(full)) return null;
        // Do not follow document symlinks or junctions outside the opened folder.
        for (var current = full; !string.Equals(PathHelpers.Canonicalize(current),
                 PathHelpers.Canonicalize(root), StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
        }
        return full;
    }

    internal static string BlobId(byte[] data, string format)
    {
        using var hash = IncrementalHash.CreateHash(format == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {data.Length}\0"));
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsObjectId(string value)
        => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    private static string Text(byte[] data) => Encoding.UTF8.GetString(data).Replace("\r\n", "\n");
    private static IEnumerable<string> Records(byte[] data) => Encoding.UTF8.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private void Watch(string root, string gitDirectory, string commonDirectory, int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _watchGeneration ||
                string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase)) return;
            StopCore();
            _watchedRoot = root;
            try
            {
                foreach (var directory in new[] { gitDirectory, commonDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var watcher = new FileSystemWatcher(directory)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Changed += OnReferenceChanged;
                    watcher.Created += OnReferenceChanged;
                    watcher.Deleted += OnReferenceChanged;
                    watcher.Renamed += OnReferenceRenamed;
                    watcher.Error += OnWatchError;
                    _watchers.Add(watcher);
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch { StopCore(); throw; }
        }
    }

    internal static bool IsReferencePath(string relative)
    {
        relative = relative.Replace('\\', '/');
        return !relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) &&
            (relative is "HEAD" or "index" or "packed-refs" or "refs" || relative.StartsWith("refs/", StringComparison.Ordinal));
    }

    private void OnReferenceChanged(object sender, FileSystemEventArgs e)
    {
        if (e.Name is not null && IsReferencePath(e.Name)) ScheduleChanged();
    }

    private void OnReferenceRenamed(object sender, RenamedEventArgs e)
    {
        if ((e.Name is not null && IsReferencePath(e.Name)) ||
            (e.OldName is not null && IsReferencePath(e.OldName))) ScheduleChanged();
    }

    private void OnWatchError(object sender, ErrorEventArgs e)
    {
        logger.LogWarning($"Git reference watcher failed: {e.GetException().Message}");
        ScheduleChanged();
    }

    private void ScheduleChanged()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            var generation = _watchGeneration;
            _debounce = new Timer(_ => dispatcher.TryEnqueue(() =>
            {
                lock (_gate)
                {
                    if (!_disposed && generation == _watchGeneration) RepositoryChanged?.Invoke();
                }
            }), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        }
    }

    private void StopCore()
    {
        _watchGeneration++;
        _debounce?.Dispose();
        _debounce = null;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        _watchedRoot = null;
    }

    public void Stop() { lock (_gate) StopCore(); }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; StopCore(); }
        // Reads may still be unwinding cancellation, so the semaphore is not disposed here.
    }
}
