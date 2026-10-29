using System;
using System.Collections.Generic;
using System.Linq;
using SkimDownForWindows.Application.Models;

namespace SkimDownForWindows.Application.Markdown;

/// <summary>Myers line comparison, with a bounded trace for heavily rewritten documents.</summary>
public static class LineDiff
{
    public static string[] Lines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.StartsWith('\uFEFF')) normalized = normalized[1..];
        if (normalized.Length == 0) return [];
        var lines = normalized.Split('\n');
        return normalized.EndsWith('\n') ? lines[..^1] : lines;
    }

    public static IReadOnlyList<LineDiffHunk> Hunks(string oldText, string newText)
    {
        var oldLines = Lines(oldText);
        var newLines = Lines(newText);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length &&
               oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        var n = oldLines.Length - prefix - suffix;
        var m = newLines.Length - prefix - suffix;
        if (n == 0 && m == 0) return [];
        if (n == 0 || m == 0) return [Hunk(prefix, m, prefix, n, oldLines)];

        var trace = new List<Dictionary<int, int>>();
        var frontier = new Dictionary<int, int> { [1] = 0 };
        var stored = 0;
        for (var d = 0; d <= n + m; d++)
        {
            // A whole-middle replacement remains correct when an adversarial diff exceeds the budget.
            stored += frontier.Count;
            if (stored > 1_000_000) return [Hunk(prefix, m, prefix, n, oldLines)];
            trace.Add(new(frontier));
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && Get(frontier, k - 1) < Get(frontier, k + 1))
                    ? Get(frontier, k + 1) : Get(frontier, k - 1) + 1;
                var y = x - k;
                while (x < n && y < m && oldLines[prefix + x] == newLines[prefix + y])
                {
                    x++;
                    y++;
                }
                frontier[k] = x;
                if (x >= n && y >= m)
                    return BuildHunks(trace, n, m, prefix, oldLines);
            }
        }
        throw new InvalidOperationException("Line comparison did not converge.");
    }

    private static int Get(Dictionary<int, int> frontier, int k) => frontier.GetValueOrDefault(k);

    private static IReadOnlyList<LineDiffHunk> BuildHunks(
        List<Dictionary<int, int>> trace, int x, int y, int prefix, string[] oldLines)
    {
        var endOld = prefix + x;
        var endNew = prefix + y;
        var matches = new List<(int Old, int New)>();
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var frontier = trace[d];
            var k = x - y;
            var previousK = k == -d || (k != d && Get(frontier, k - 1) < Get(frontier, k + 1))
                ? k + 1 : k - 1;
            var previousX = Get(frontier, previousK);
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY) matches.Add((--x + prefix, --y + prefix));
            x = previousX;
            y = previousY;
        }
        matches.Reverse();
        var oldIndex = prefix;
        var newIndex = prefix;
        var hunks = new List<LineDiffHunk>();
        foreach (var match in matches.Append((Old: endOld, New: endNew)))
        {
            if (oldIndex != match.Old || newIndex != match.New)
                hunks.Add(Hunk(newIndex, match.New - newIndex, oldIndex, match.Old - oldIndex, oldLines));
            oldIndex = match.Old + 1;
            newIndex = match.New + 1;
        }
        return hunks;
    }

    private static LineDiffHunk Hunk(int newStart, int newCount, int oldStart, int oldCount, string[] oldLines)
        => new(newStart, newCount, oldStart, oldCount, oldLines[oldStart..(oldStart + oldCount)]);
}
