using System;
using System.IO;
using SkimDownForWindows.Utilities;

namespace SkimDownForWindows.Core;

/// <summary>
/// Parses the process command line and decides which folder the first window
/// should open. Mirrors the macOS upstream's <c>skimdown ./mydocs</c> behavior:
/// passing a directory path as the first positional argument opens that
/// folder instead of restoring the persisted <c>LastFolderPath</c>.
///
/// If the first positional argument is a Markdown file (<c>.md</c> /
/// <c>.markdown</c>) the file's containing folder is opened — initial file
/// selection itself is not yet plumbed, but this still gives a sensible
/// "open the surrounding folder" result for <c>skimdown ./note.md</c>.
///
/// All inputs are explicit parameters so this can be unit-tested without
/// touching <see cref="Environment"/>.
/// </summary>
public static class CommandLineLauncher
{
    /// <summary>
    /// Inspect the process command line and resolve a folder to open.
    /// </summary>
    /// <param name="args">
    /// Process command-line arguments. Production callers pass
    /// <see cref="Environment.GetCommandLineArgs"/>; the convention is that
    /// <c>args[0]</c> is the executable path and is ignored.
    /// </param>
    /// <param name="currentDirectory">
    /// Base directory used to resolve relative paths. Production callers pass
    /// <see cref="Environment.CurrentDirectory"/>.
    /// </param>
    /// <returns>
    /// An absolute, existing folder path, or <c>null</c> when no usable folder
    /// argument was provided. Switches such as <c>--help</c> are skipped so a
    /// future flag rollout doesn't accidentally treat them as folder names.
    /// </returns>
    public static string? TryGetInitialFolderPath(string[] args, string currentDirectory)
    {
        if (args is null || args.Length < 2)
        {
            return null;
        }

        // args[0] is the executable path. Walk positional args and use the
        // first one that resolves to a folder (or to a markdown file's parent).
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }
            // Skip "-x" / "--flag" style switches so the parser stays
            // forward-compatible with future options.
            if (arg.StartsWith('-'))
            {
                continue;
            }

            var resolved = ResolveFolder(arg, currentDirectory);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveFolder(string arg, string currentDirectory)
    {
        string full;
        try
        {
            full = Path.IsPathFullyQualified(arg)
                ? Path.GetFullPath(arg)
                : Path.GetFullPath(arg, currentDirectory);
        }
        catch
        {
            // Invalid characters / malformed path — silently skip.
            return null;
        }

        if (Directory.Exists(full))
        {
            return full;
        }

        // Accept a markdown file path and fall back to its parent directory,
        // matching the upstream's "skimdown ./note.md" entry point at the
        // folder level. Per-file initial selection is a separate concern.
        if (File.Exists(full) && PathHelpers.IsMarkdownFile(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                return parent;
            }
        }

        return null;
    }
}
