using System.Text;
using ArChrono.Git.Models;

namespace ArChrono.Git.Diffing;

/// <summary>
/// Bellek içi satır diff'i (Myers O(ND)). Git nesne veritabanında olmayan içerikleri
/// (snapshot'lar, untracked dosyalar) karşılaştırmak için kullanılır.
/// </summary>
public static class TextDiff
{
    private const int MaxEditDistance = 2_000;
    private const int BinaryProbeLength = 8_000;

    public static bool LooksBinary(ReadOnlySpan<byte> content) =>
        content[..Math.Min(content.Length, BinaryProbeLength)].IndexOf((byte)0) >= 0;

    public static string Decode(byte[]? content) =>
        content is null ? string.Empty : new UTF8Encoding(false, false).GetString(content).TrimStart('﻿');

    public static FileDiff Compare(string? oldPath, string? newPath, byte[]? oldContent, byte[]? newContent, int contextLines = 3)
    {
        var change = (oldContent, newContent) switch
        {
            (null, not null) => ChangeKind.Added,
            (not null, null) => ChangeKind.Deleted,
            _ => ChangeKind.Modified,
        };

        if ((oldContent is not null && LooksBinary(oldContent)) || (newContent is not null && LooksBinary(newContent)))
            return new FileDiff(oldContent is null ? null : oldPath, newContent is null ? null : newPath, change, true, null, null, []);

        var hunks = ComputeHunks(SplitLines(Decode(oldContent)), SplitLines(Decode(newContent)), contextLines);
        return new FileDiff(oldContent is null ? null : oldPath, newContent is null ? null : newPath, change, false, null, null, hunks);
    }

    internal static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private enum Op { Equal, Insert, Delete }

    public static IReadOnlyList<DiffHunk> ComputeHunks(string[] oldLines, string[] newLines, int contextLines = 3)
    {
        var script = ComputeScript(oldLines, newLines);
        var hunks = new List<DiffHunk>();

        var i = 0;
        while (i < script.Count)
        {
            // Değişiklik bulunana kadar ilerle.
            while (i < script.Count && script[i].Op == Op.Equal) i++;
            if (i >= script.Count) break;

            var start = Math.Max(0, i - contextLines);
            var end = i;
            // Aradaki eşit bölüm 2*context'ten kısa olduğu sürece hunk'ı genişlet.
            while (end < script.Count)
            {
                if (script[end].Op != Op.Equal)
                {
                    end++;
                    continue;
                }
                var run = end;
                while (run < script.Count && script[run].Op == Op.Equal) run++;
                if (run >= script.Count || run - end > contextLines * 2)
                {
                    end = Math.Min(script.Count, end + contextLines);
                    break;
                }
                end = run;
            }

            var lines = new List<DiffLine>();
            int? oldStart = null, newStart = null;
            int oldCount = 0, newCount = 0;
            for (var k = start; k < end; k++)
            {
                var (op, oldIndex, newIndex) = script[k];
                switch (op)
                {
                    case Op.Equal:
                        oldStart ??= oldIndex + 1;
                        newStart ??= newIndex + 1;
                        lines.Add(new DiffLine(DiffLineKind.Context, oldLines[oldIndex], oldIndex + 1, newIndex + 1));
                        oldCount++;
                        newCount++;
                        break;
                    case Op.Delete:
                        oldStart ??= oldIndex + 1;
                        newStart ??= newIndex + 1;
                        lines.Add(new DiffLine(DiffLineKind.Removed, oldLines[oldIndex], oldIndex + 1, null));
                        oldCount++;
                        break;
                    case Op.Insert:
                        oldStart ??= oldIndex + 1;
                        newStart ??= newIndex + 1;
                        lines.Add(new DiffLine(DiffLineKind.Added, newLines[newIndex], null, newIndex + 1));
                        newCount++;
                        break;
                }
            }
            hunks.Add(new DiffHunk(oldCount == 0 ? (oldStart ?? 1) - 1 : oldStart ?? 1, oldCount,
                newCount == 0 ? (newStart ?? 1) - 1 : newStart ?? 1, newCount, string.Empty, lines));
            i = end;
        }
        return hunks;
    }

    /// <summary>Düzenleme betiği: her adım (işlem, eski satır indeksi, yeni satır indeksi).</summary>
    private static List<(Op Op, int OldIndex, int NewIndex)> ComputeScript(string[] a, string[] b)
    {
        // Ortak önek/sonek dışarıda tutularak problem küçültülür.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        var script = new List<(Op, int, int)>(a.Length + b.Length);
        for (var k = 0; k < prefix; k++) script.Add((Op.Equal, k, k));

        var middle = Myers(a, b, prefix, a.Length - suffix, prefix, b.Length - suffix);
        script.AddRange(middle);

        for (var k = 0; k < suffix; k++) script.Add((Op.Equal, a.Length - suffix + k, b.Length - suffix + k));
        return script;
    }

    private static List<(Op, int, int)> Myers(string[] a, string[] b, int aStart, int aEnd, int bStart, int bEnd)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        var result = new List<(Op, int, int)>();
        if (n == 0 && m == 0) return result;

        var max = n + m;
        var limit = Math.Min(max, MaxEditDistance);
        var offset = max;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();

        var found = false;
        for (var d = 0; d <= limit && !found; d++)
        {
            // Yalnızca k ∈ [-d-1, d+1] aralığı saklanır: bellek O(D²), O(D·(N+M)) değil.
            var window = new int[2 * d + 3];
            Array.Copy(v, offset - d - 1 < 0 ? 0 : offset - d - 1, window, offset - d - 1 < 0 ? 1 : 0, offset - d - 1 < 0 ? 2 * d + 2 : 2 * d + 3);
            trace.Add(window);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            // Çok farklı içerik: tamamını sil/ekle olarak göster.
            for (var i = 0; i < n; i++) result.Add((Op.Delete, aStart + i, bStart));
            for (var j = 0; j < m; j++) result.Add((Op.Insert, aStart + n, bStart + j));
            return result;
        }

        // Geri izleme.
        var cx = n;
        var cy = m;
        var reversed = new List<(Op, int, int)>();
        for (var d = trace.Count - 1; d >= 0 && (cx > 0 || cy > 0); d--)
        {
            var vd = trace[d];
            var baseIndex = d + 1; // window[k + d + 1] == v[offset + k]
            var k = cx - cy;
            int prevK;
            if (k == -d || (k != d && vd[baseIndex + k - 1] < vd[baseIndex + k + 1])) prevK = k + 1;
            else prevK = k - 1;
            var prevX = d == 0 ? 0 : vd[baseIndex + prevK];
            var prevY = prevX - prevK;

            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
                reversed.Add((Op.Equal, aStart + cx, bStart + cy));
            }
            if (d == 0) break;
            if (cx == prevX)
            {
                cy--;
                reversed.Add((Op.Insert, aStart + cx, bStart + cy));
            }
            else
            {
                cx--;
                reversed.Add((Op.Delete, aStart + cx, bStart + cy));
            }
        }
        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            reversed.Add((Op.Equal, aStart + cx, bStart + cy));
        }
        reversed.Reverse();
        return reversed;
    }
}
