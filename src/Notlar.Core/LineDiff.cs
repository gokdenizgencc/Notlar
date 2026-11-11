namespace Notlar.Core;

public enum LineOpKind { Keep, Update, Insert, Delete }

/// <summary>Tek bir satır eşleştirme işlemi. Keep/Update ikisi de dolu; Insert yalnızca
/// <see cref="NewIndex"/>, Delete yalnızca <see cref="OldIndex"/> taşır.</summary>
public readonly record struct LineOp(LineOpKind Kind, int OldIndex = -1, int NewIndex = -1);

/// <summary>Notepad++ tarzı tam ekran editörde, satır satır düzenlenen bir günün metnini
/// eski (kaydedilmiş) notlarla eşleştirir. Her satır bir nota karşılık gelir; eşleştirme
/// kırpılmış (trim) satır içeriği üzerinden en uzun ortak alt dizi (LCS) ile yapılır.</summary>
public static class LineDiff
{
    /// <summary><paramref name="oldLines"/> (eski notların metinleri) ile <paramref name="newLines"/>
    /// (editördeki güncel satırlar) arasındaki farkı işlemler listesi olarak döndürür.
    /// Boş (veya yalnızca boşluk) yeni satırlar yok sayılır — ne eşleştirmeye ne de
    /// Insert/Update üretimine dahil edilir.</summary>
    public static IReadOnlyList<LineOp> Map(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var oldTrim = oldLines.Select(l => l.Trim()).ToArray();
        var newTrim = newLines.Select(l => l.Trim()).ToArray();
        var n = oldTrim.Length;
        var m = newTrim.Length;

        // dp[i,j] = oldTrim[i..] ile newTrim[j..] arasındaki LCS uzunluğu.
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = Matches(oldTrim[i], newTrim[j])
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        // Geri iz sürerek eşleşen (oldIdx, newIdx) çapalarını (Keep adayları) çıkar.
        var anchors = new List<(int OldIdx, int NewIdx)>();
        var ii = 0;
        var jj = 0;
        while (ii < n && jj < m)
        {
            if (Matches(oldTrim[ii], newTrim[jj]) && dp[ii, jj] == dp[ii + 1, jj + 1] + 1)
            {
                anchors.Add((ii, jj));
                ii++;
                jj++;
            }
            else if (dp[ii + 1, jj] >= dp[ii, jj + 1])
            {
                ii++;
            }
            else
            {
                jj++;
            }
        }

        var ops = new List<LineOp>();
        var prevOld = 0;
        var prevNew = 0;
        foreach (var (oldIdx, newIdx) in anchors)
        {
            EmitGap(ops, oldTrim, newTrim, prevOld, oldIdx, prevNew, newIdx);
            ops.Add(new LineOp(LineOpKind.Keep, oldIdx, newIdx));
            prevOld = oldIdx + 1;
            prevNew = newIdx + 1;
        }
        EmitGap(ops, oldTrim, newTrim, prevOld, n, prevNew, m);

        return ops;
    }

    private static bool Matches(string oldTrimmed, string newTrimmed) =>
        oldTrimmed.Length > 0 && oldTrimmed == newTrimmed;

    private static void EmitGap(
        List<LineOp> ops, string[] oldTrim, string[] newTrim,
        int oldStart, int oldEnd, int newStart, int newEnd)
    {
        var oldGap = new List<int>();
        for (var i = oldStart; i < oldEnd; i++)
            oldGap.Add(i);

        var newGap = new List<int>();
        for (var j = newStart; j < newEnd; j++)
        {
            if (newTrim[j].Length > 0)
                newGap.Add(j);
        }

        var pairCount = Math.Min(oldGap.Count, newGap.Count);
        for (var k = 0; k < pairCount; k++)
            ops.Add(new LineOp(LineOpKind.Update, oldGap[k], newGap[k]));
        for (var k = pairCount; k < oldGap.Count; k++)
            ops.Add(new LineOp(LineOpKind.Delete, oldGap[k]));
        for (var k = pairCount; k < newGap.Count; k++)
            ops.Add(new LineOp(LineOpKind.Insert, NewIndex: newGap[k]));
    }
}
