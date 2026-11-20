namespace LandMark.Services;

/// <summary>
/// Three-way merge of text, line by line (the diff3 method that version control uses). Given the text both sides
/// started from and the two edited versions, it keeps every change either side made. Where both sides changed the
/// same lines differently, it keeps both versions, between conflict markers:
/// <code>
/// &lt;&lt;&lt;&lt;&lt;&lt;&lt; Your changes
/// ...
/// =======
/// ...
/// &gt;&gt;&gt;&gt;&gt;&gt;&gt; Version on disk
/// </code>
/// </summary>
public static class TextMerge
{
    public const string MineMarker = "<<<<<<< Your changes";
    public const string SeparatorMarker = "=======";
    public const string TheirsMarker = ">>>>>>> Version on disk";

    /// <summary>Beyond this many differing lines, the changed middle is treated as one block instead of matched line by line.</summary>
    private const int MaxEditDistance = 4000;

    /// <param name="Text">The merged text.</param>
    /// <param name="Conflicts">How many places both sides changed differently.</param>
    /// <param name="FirstConflictStart">Character offset of the first conflict's opening marker, or -1.</param>
    /// <param name="FirstConflictLength">Length of the first conflict, markers included.</param>
    public sealed record Result(string Text, int Conflicts, int FirstConflictStart, int FirstConflictLength);

    /// <param name="original">The text both versions started from.</param>
    /// <param name="mine">The version whose line endings the result uses (the editor's).</param>
    /// <param name="theirs">The other version (the file on disk).</param>
    public static Result Merge(string original, string mine, string theirs)
    {
        string newline = mine.Contains("\r\n") || (!mine.Contains('\n') && theirs.Contains("\r\n")) ? "\r\n" : "\n";

        // Compare lines as numbers: each distinct line gets one id, shared by all three texts.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] originalLines = SplitLines(original), mineLines = SplitLines(mine), theirLines = SplitLines(theirs);
        int[] o = ToIds(originalLines, ids), a = ToIds(mineLines, ids), b = ToIds(theirLines, ids);
        int[] matchA = MatchLines(o, a), matchB = MatchLines(o, b);

        var output = new List<string>(Math.Max(mineLines.Length, theirLines.Length) + 8);
        int conflicts = 0, firstConflictLine = -1, firstConflictLines = 0;
        int io = 0, ia = 0, ib = 0;

        while (true)
        {
            // Lines unchanged on both sides.
            int same = 0;
            while (io + same < o.Length && matchA[io + same] == ia + same && matchB[io + same] == ib + same) same++;
            if (same > 0)
            {
                output.AddRange(originalLines.AsSpan(io, same));
                io += same;
                ia += same;
                ib += same;
                continue;
            }

            // A changed block runs up to the next original line that both sides still have (or to the end).
            int next = io;
            while (next < o.Length && (matchA[next] < 0 || matchB[next] < 0)) next++;
            int endA = next < o.Length ? matchA[next] : a.Length;
            int endB = next < o.Length ? matchB[next] : b.Length;
            if (next == io && endA == ia && endB == ib) break;   // everything's merged

            var originalBlock = o.AsSpan(io, next - io);
            var blockA = a.AsSpan(ia, endA - ia);
            var blockB = b.AsSpan(ib, endB - ib);
            if (blockA.SequenceEqual(originalBlock))
                output.AddRange(theirLines.AsSpan(ib, endB - ib));          // only they changed it
            else if (blockB.SequenceEqual(originalBlock) || blockA.SequenceEqual(blockB))
                output.AddRange(mineLines.AsSpan(ia, endA - ia));           // only you changed it, or you both made the same change
            else
            {
                conflicts++;
                if (firstConflictLine < 0)
                {
                    firstConflictLine = output.Count;
                    firstConflictLines = blockA.Length + blockB.Length + 3;
                }
                output.Add(MineMarker);
                output.AddRange(mineLines.AsSpan(ia, endA - ia));
                output.Add(SeparatorMarker);
                output.AddRange(theirLines.AsSpan(ib, endB - ib));
                output.Add(TheirsMarker);
            }
            io = next;
            ia = endA;
            ib = endB;
        }

        int start = -1, length = 0;
        if (firstConflictLine >= 0)
        {
            start = 0;
            for (int i = 0; i < firstConflictLine; i++) start += output[i].Length + newline.Length;
            for (int i = firstConflictLine; i < firstConflictLine + firstConflictLines; i++) length += output[i].Length + newline.Length;
            length -= newline.Length;   // not the line break after the closing marker
        }
        return new Result(string.Join(newline, output), conflicts, start, length);
    }

    /// <summary>
    /// Finds conflicts that <see cref="Merge"/> marked and that are still in <paramref name="text"/>. A conflict
    /// runs from a "&lt;&lt;&lt;&lt;&lt;&lt;&lt; Your changes" line to the next "&gt;&gt;&gt;&gt;&gt;&gt;&gt; Version on disk"
    /// line; either marker left on its own also counts. A bare "=======" doesn't, since it's also a heading underline.
    /// </summary>
    /// <returns>How many, and the character range of the first (-1 if none).</returns>
    public static (int Count, int FirstStart, int FirstLength) FindConflicts(string text)
    {
        int count = 0, firstStart = -1, firstLength = 0;
        int openStart = -1, openEnd = 0;   // a "Your changes" marker line waiting for its closing marker

        void Found(int start, int end)
        {
            if (count++ == 0)
            {
                firstStart = start;
                firstLength = end - start;
            }
        }

        int lineStart = 0;
        while (true)
        {
            int lineEnd = text.AsSpan(lineStart).IndexOfAny('\r', '\n');
            lineEnd = lineEnd < 0 ? text.Length : lineStart + lineEnd;
            var line = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd();

            if (line.SequenceEqual(MineMarker.AsSpan()))
            {
                if (openStart >= 0) Found(openStart, openEnd);
                openStart = lineStart;
                openEnd = lineEnd;
            }
            else if (line.SequenceEqual(TheirsMarker.AsSpan()))
            {
                Found(openStart >= 0 ? openStart : lineStart, lineEnd);
                openStart = -1;
            }

            if (lineEnd == text.Length) break;
            lineStart = lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
        }
        if (openStart >= 0) Found(openStart, openEnd);
        return (count, firstStart, firstLength);
    }

    /// <summary>Splits on any line ending (CRLF, LF or CR). A final line ending gives a final empty line, so it survives the merge.</summary>
    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static int[] ToIds(string[] lines, Dictionary<string, int> ids)
    {
        var result = new int[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (!ids.TryGetValue(lines[i], out int id))
            {
                id = ids.Count;
                ids[lines[i]] = id;
            }
            result[i] = id;
        }
        return result;
    }

    /// <summary>
    /// For each line of <paramref name="from"/>, the index of the same line in <paramref name="to"/> in a longest
    /// common subsequence, or -1 if it was removed or changed. Matched indices only ever increase.
    /// </summary>
    private static int[] MatchLines(int[] from, int[] to)
    {
        var match = new int[from.Length];
        Array.Fill(match, -1);

        // Usually most of the text is untouched at the start and end; only the middle needs the diff.
        int start = 0;
        while (start < from.Length && start < to.Length && from[start] == to[start])
        {
            match[start] = start;
            start++;
        }
        int endFrom = from.Length, endTo = to.Length;
        while (endFrom > start && endTo > start && from[endFrom - 1] == to[endTo - 1])
        {
            endFrom--;
            endTo--;
            match[endFrom] = endTo;
        }

        MatchMiddle(from.AsSpan(start, endFrom - start), to.AsSpan(start, endTo - start), match.AsSpan(start), start);
        return match;
    }

    /// <summary>
    /// Myers' O((N+M)·D) diff, keeping the frontier of each step to trace the path back (O(D²) memory). Gives up
    /// (leaving the block unmatched) past <see cref="MaxEditDistance"/>, where texts barely resemble each other.
    /// </summary>
    private static void MatchMiddle(ReadOnlySpan<int> a, ReadOnlySpan<int> b, Span<int> match, int bOffset)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return;

        int max = n + m, offset = max;
        var v = new int[2 * max + 2];
        var frontiers = new List<int[]>();   // frontiers[d][k + d] = furthest x on diagonal k after step d
        int found = -1;

        for (int d = 0; d <= max && d <= MaxEditDistance; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])
                    ? v[offset + k + 1]         // down: a line only in b
                    : v[offset + k - 1] + 1;    // right: a line only in a
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m) found = d;
            }
            frontiers.Add(v.AsSpan(offset - d, 2 * d + 1).ToArray());
            if (found >= 0) break;
        }
        if (found < 0) return;

        // Walk back from the end, recording the diagonal runs (matching lines).
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] previous = frontiers[d - 1];
            int k = cx - cy;
            bool down = k == -d || (k != d && previous[k - 1 + (d - 1)] < previous[k + 1 + (d - 1)]);
            int previousK = down ? k + 1 : k - 1;
            int previousX = previous[previousK + (d - 1)];
            int runStartX = down ? previousX : previousX + 1;
            while (cx > runStartX)
            {
                cx--;
                cy--;
                match[cx] = cy + bOffset;
            }
            cx = previousX;
            cy = previousX - previousK;
        }
        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            match[cx] = cy + bOffset;
        }
    }
}
