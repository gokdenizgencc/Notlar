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

    /// <summary>İki çapa arasındaki boşluğu (eşleşmeyen eski/yeni satırlar) işlemlere çevirir.
    /// Sayılar eşitse satırlar sırayla eşleştirilir (Update). Farklıysa kısa taraftaki her satır,
    /// sırayı koruyarak uzun taraftaki en benzer satırla eşleştirilir; böylece satır ortasında
    /// Enter (bölme) ya da satır başında Backspace (birleştirme) asıl notu ilk parçada tutar ve
    /// düzenlenen bir satırın üstüne yazılan yepyeni satır o notun kimliğini (yazar/saat) çalmaz.
    /// İşlemler yeni satır sırasıyla üretilir; aynı noktada silmeler eklemelerden önce gelir.</summary>
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

        // pairs[k] = (oldGap konumu, newGap konumu); her iki bileşende de artan sırada.
        var pairs = new List<(int O, int N)>();
        if (oldGap.Count == newGap.Count)
        {
            for (var k = 0; k < oldGap.Count; k++)
                pairs.Add((k, k));
        }
        else if (oldGap.Count < newGap.Count)
        {
            var match = Align(oldGap.Count, newGap.Count, (s, l) => Similarity(oldTrim[oldGap[s]], newTrim[newGap[l]]));
            for (var k = 0; k < match.Length; k++)
                pairs.Add((k, match[k]));
        }
        else
        {
            var match = Align(newGap.Count, oldGap.Count, (s, l) => Similarity(oldTrim[oldGap[l]], newTrim[newGap[s]]));
            for (var k = 0; k < match.Length; k++)
                pairs.Add((match[k], k));
        }

        var o = 0;
        var nn = 0;
        foreach (var (po, pn) in pairs)
        {
            for (; o < po; o++)
                ops.Add(new LineOp(LineOpKind.Delete, oldGap[o]));
            for (; nn < pn; nn++)
                ops.Add(new LineOp(LineOpKind.Insert, NewIndex: newGap[nn]));
            ops.Add(new LineOp(LineOpKind.Update, oldGap[po], newGap[pn]));
            o = po + 1;
            nn = pn + 1;
        }
        for (; o < oldGap.Count; o++)
            ops.Add(new LineOp(LineOpKind.Delete, oldGap[o]));
        for (; nn < newGap.Count; nn++)
            ops.Add(new LineOp(LineOpKind.Insert, NewIndex: newGap[nn]));
    }

    /// <summary>Kısa dizinin her öğesini (<paramref name="shortCount"/>), sırayı koruyarak uzun dizideki
    /// bir öğeyle eşleştirir ve toplam benzerliği en büyükler. Eşitlikte en erken eşleşme seçilir.
    /// Dönüş: kısa öğe başına uzun dizideki konum.</summary>
    private static int[] Align(int shortCount, int longCount, Func<int, int, int> score)
    {
        // best[s, l] = kısa[s..] öğelerinin uzun[l..] içine yerleştirilmesiyle elde edilebilecek en iyi puan.
        var best = new long[shortCount + 1, longCount + 1];
        for (var s = shortCount - 1; s >= 0; s--)
        {
            for (var l = longCount - 1; l >= 0; l--)
            {
                if (longCount - l < shortCount - s)
                {
                    best[s, l] = long.MinValue / 2;
                    continue;
                }
                var pair = score(s, l) + best[s + 1, l + 1];
                var skip = longCount - l - 1 >= shortCount - s ? best[s, l + 1] : long.MinValue / 2;
                best[s, l] = Math.Max(pair, skip);
            }
        }

        var result = new int[shortCount];
        var li = 0;
        for (var s = 0; s < shortCount; s++)
        {
            while (true)
            {
                var canSkip = longCount - li - 1 >= shortCount - s;
                var pair = score(s, li) + best[s + 1, li + 1];
                if (!canSkip || pair >= best[s, li + 1])
                    break;
                li++;
            }
            result[s] = li;
            li++;
        }
        return result;
    }

    /// <summary>İki satırın benzerliği (0..2000): ortak önek iki kat, ortak sonek bir kat ağırlıkla,
    /// uzun olanın boyuna oranlanır. Önek ağırlığı, satır bölünürken/birleşirken asıl notun ilk
    /// parçada kalmasını sağlar.</summary>
    private static int Similarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 0;
        var min = Math.Min(a.Length, b.Length);

        var prefix = 0;
        while (prefix < min && a[prefix] == b[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < min - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
            suffix++;

        return (int)((2L * prefix + suffix) * 1000 / max);
    }
}
