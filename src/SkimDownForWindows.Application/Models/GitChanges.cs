using System.Collections.Generic;

namespace SkimDownForWindows.Application.Models;

/// <summary>HEAD comparison result; null BaseText excludes an ignored or unreadable selection.</summary>
public sealed record GitChanges(IReadOnlyDictionary<string, string> Statuses, string? BaseText);

/// <summary>A contiguous change. All line offsets are zero-based in the full Markdown source.</summary>
public sealed record LineDiffHunk(int NewStart, int NewCount, int OldStart, int OldCount, string[] OldLines);
