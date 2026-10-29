using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Application.Abstractions;

/// <summary>Read-only HEAD comparison and reference monitoring for one window.</summary>
public interface IGitChangeSource : IDisposable
{
    /// <summary>Raised on the UI dispatcher after HEAD, index, or refs change.</summary>
    event Action? RepositoryChanged;

    /// <summary>
    /// Reads statuses and the selected file's HEAD text only when root is a working-tree root.
    /// Returns null when Git is unavailable, the folder is ineligible, or a logged lookup fails.
    /// Cancellation stops outstanding commands. Paths are forward-slash relative to root.
    /// </summary>
    Task<GitChanges?> ReadAsync(string root, IReadOnlyList<string> relativePaths,
        string? selectedPath, CancellationToken cancellationToken);

    /// <summary>Stops reference monitoring when the option is disabled or the folder changes.</summary>
    void Stop();
}
