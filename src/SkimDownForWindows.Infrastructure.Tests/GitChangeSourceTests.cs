using System.Diagnostics;
using System.Text;
using SkimDownForWindows.Application.Abstractions;
using SkimDownForWindows.Infrastructure.IO;
using SkimDownForWindows.Infrastructure.Tests.TestHelpers;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace SkimDownForWindows.Infrastructure.Tests;

[TestClass]
public sealed class GitChangeSourceTests
{
    private string _root = null!;
    private string _git = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "skim-git-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _git = GitCommandRunner.ResolveExecutable(_root)!;
        if (_git is null) Assert.Inconclusive("Git for Windows is not installed on a safe PATH entry.");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private async Task Git(params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new(_git)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-c", "user.name=Skim Test", "-c", "user.email=skim@example.invalid" }.Concat(args))
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await stderr);
        await stdout;
    }

    [TestMethod]
    public async Task Read_RootOnly_TracksModifiedAddedUntrackedAndIgnoredWithoutExecutingHooks()
    {
        await Git("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked.md"), "old\n");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "ignored.md\n");
        await Git("add", ".");
        await Git("commit", "-qm", "initial");
        var sentinel = Path.Combine(_root, "EXECUTED");
        await Git("config", "core.fsmonitor", "!echo bad > EXECUTED");
        await Git("config", "diff.external", "echo bad");
        await Git("config", "filter.evil.clean", "echo bad > EXECUTED");
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitattributes"), "*.md filter=evil diff=evil\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "tracked.md"), "new\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "added.md"), "added\n");
        await Git("-c", "core.fsmonitor=false", "-c", "filter.evil.clean=", "add", "added.md");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.md"), "new\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.md"), "ignored\n");
        var logger = new RecordingAppLogger();
        using var source = new LocalGitChangeSource(new ImmediateUiDispatcher(), logger);
        var result = await source.ReadAsync(_root, ["tracked.md", "added.md", "new.md", "ignored.md"],
            "tracked.md", CancellationToken.None);
        Assert.IsNotNull(result, string.Join("; ", logger.Warnings));
        Assert.AreEqual("M", result.Statuses["tracked.md"]);
        Assert.AreEqual("A", result.Statuses["added.md"]);
        Assert.AreEqual("U", result.Statuses["new.md"]);
        Assert.IsFalse(result.Statuses.ContainsKey("ignored.md"));
        Assert.AreEqual("old\n", result.BaseText);
        Assert.IsFalse(File.Exists(sentinel));
        var ignored = await source.ReadAsync(_root, ["ignored.md"], "ignored.md", CancellationToken.None);
        Assert.IsNotNull(ignored);
        Assert.IsNull(ignored.BaseText);
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        Assert.IsNull(await source.ReadAsync(child, [], null, CancellationToken.None));
    }

    [TestMethod]
    public async Task Read_UnbornHead_UsesEmptyBaseline()
    {
        await Git("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(_root, "new.md"), "# New");
        using var source = new LocalGitChangeSource(new ImmediateUiDispatcher(), new RecordingAppLogger());
        var result = await source.ReadAsync(_root, ["new.md"], "new.md", CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.AreEqual("U", result.Statuses["new.md"]);
        Assert.AreEqual("", result.BaseText);
    }

    [TestMethod]
    public async Task Read_LinkedWorktreeAndCrLf_UsesHeadAndReferenceNotifications()
    {
        await Git("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "same\n");
        await Git("add", ".");
        await Git("commit", "-qm", "initial");
        var worktree = Path.Combine(_root, "linked");
        await Git("worktree", "add", "-qb", "test-linked", worktree);
        await File.WriteAllTextAsync(Path.Combine(worktree, "readme.md"), "same\r\n");
        using var source = new LocalGitChangeSource(new ImmediateUiDispatcher(), new RecordingAppLogger());
        var result = await source.ReadAsync(worktree, ["readme.md"], "readme.md", CancellationToken.None);
        Assert.IsNotNull(result);
        Assert.HasCount(0, result.Statuses);
        Assert.AreEqual("same\n", result.BaseText);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.RepositoryChanged += () => changed.TrySetResult();
        await File.AppendAllTextAsync(Path.Combine(_root, ".git", "worktrees", "linked", "HEAD"), "\n");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void EnvironmentAndExecutable_ExcludeInheritedGitVariablesAndUntrustedSearchEntries()
    {
        var environment = GitCommandRunner.ChildEnvironment();
        Assert.IsFalse(environment.ContainsKey("GIT_DIR"));
        Assert.IsFalse(environment.ContainsKey("GIT_CONFIG_COUNT"));
        Assert.AreEqual("0", environment["GIT_OPTIONAL_LOCKS"]);
        Assert.AreEqual("1", environment["GIT_NO_LAZY_FETCH"]);
        File.WriteAllText(Path.Combine(_root, "git.exe"), "not executable");
        Assert.IsNull(GitCommandRunner.ResolveExecutable(_root, $".;{_root};relative"));
    }

    [TestMethod]
    public async Task Runner_CancellationTerminatesProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new GitCommandRunner(_git);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            runner.RunAsync(_root, ["rev-parse", "--show-toplevel"], cancellation.Token));
    }

    [TestMethod]
    public async Task Runner_TimeoutTerminatesCommand()
    {
        // Only this test invokes a shell alias to keep a child alive; production uses plumbing only.
        var runner = new GitCommandRunner(_git);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(_root,
            ["-c", "alias.skim-wait=!sleep 30", "skim-wait"], CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(150)));
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed);
    }

    [TestMethod]
    public void BlobHashAndReferenceFilter_MatchGitFormat()
    {
        Assert.AreEqual("ce013625030ba8dba906f756967f9e9ca394464a",
            LocalGitChangeSource.BlobId(Encoding.UTF8.GetBytes("hello\n"), "sha1"));
        Assert.AreEqual(64, LocalGitChangeSource.BlobId([], "sha256").Length);
        foreach (var path in new[] { "HEAD", "index", "packed-refs", "refs\\heads\\main" })
            Assert.IsTrue(LocalGitChangeSource.IsReferencePath(path));
        foreach (var path in new[] { "HEAD.lock", "objects\\12\\345", "logs\\HEAD", "refs\\heads\\main.lock" })
            Assert.IsFalse(LocalGitChangeSource.IsReferencePath(path));
    }
}
