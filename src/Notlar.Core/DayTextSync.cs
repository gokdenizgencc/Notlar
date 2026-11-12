namespace Notlar.Core;

public enum DayEditKind { Update, Delete, Insert }

/// <summary>Bir günün metnini kaydederken uygulanacak tek bir not işlemi. Update: <see cref="NoteId"/> +
/// <see cref="Text"/>; Delete: yalnızca <see cref="NoteId"/>; Insert: <see cref="Text"/> +
/// <see cref="CreatedAt"/> (yeni notun saati; komşu notların arasına sırayı bozmadan yerleştirilir).</summary>
public readonly record struct DayEdit(DayEditKind Kind, long NoteId, string Text, DateTimeOffset CreatedAt);

/// <summary>Not defteri gibi tek metin olarak düzenlenen bir günü (her satır = bir not) depodaki
/// notlarla eşitler. Boş satırlar ve satır başı/sonu boşlukları not içeriğine dahil değildir.
/// Satırlar notlara <see cref="LineDiff"/> ile eşlenir: değişmeyen satırlara dokunulmaz (yazar, saat,
/// tamamlandı işareti korunur), düzenlenen satır notu günceller, silinen satır notu siler, yeni satır
/// komşularının arasına denk gelen saatle yeni not olur.</summary>
public static class DayTextSync
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

    /// <summary>Metni satırlara böler (\r\n ve \n). Boş metin hiç satır içermez.</summary>
    public static string[] SplitLines(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Notları editör metnine çevirir (her not bir satır).</summary>
    public static string ToText(IEnumerable<Note> notes) => string.Join("\n", notes.Select(n => n.Text));

    /// <summary>Metin, kaydedildiğinde notlarda bir değişiklik yaratır mı? Boş satırlar ve
    /// kenar boşlukları sayılmaz.</summary>
    public static bool HasChanges(IReadOnlyList<Note> notes, string text)
    {
        var lines = SplitLines(text).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count != notes.Count) return true;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] != notes[i].Text.Trim())
                return true;
        }
        return false;
    }

    /// <summary>Editör metnini (<paramref name="text"/>) depoya yazmak için gereken işlemleri, editördeki
    /// satır sırasıyla döndürür. <paramref name="notes"/> günün o an bilinen notlarıdır (saat sırasıyla);
    /// <paramref name="now"/> bugüne eklenen notların saatini ve diğer günlerin varsayılan saatini belirler.</summary>
    public static IReadOnlyList<DayEdit> Plan(IReadOnlyList<Note> notes, string text, DateOnly day, DateTimeOffset now)
    {
        var lines = SplitLines(text);
        var ops = LineDiff.Map(notes.Select(n => n.Text).ToList(), lines);
        var edits = new List<DayEdit>();

        for (var k = 0; k < ops.Count; k++)
        {
            var op = ops[k];
            switch (op.Kind)
            {
                case LineOpKind.Update:
                    var updated = lines[op.NewIndex].Trim();
                    if (updated != notes[op.OldIndex].Text)
                        edits.Add(new DayEdit(DayEditKind.Update, notes[op.OldIndex].Id, updated, default));
                    break;

                case LineOpKind.Delete:
                    edits.Add(new DayEdit(DayEditKind.Delete, notes[op.OldIndex].Id, "", default));
                    break;

                case LineOpKind.Insert:
                    // Ardışık eklemeleri (araya giren silmeler hariç) tek seferde, aynı komşular arasına yerleştir.
                    var run = new List<int>();
                    var end = k;
                    for (; end < ops.Count && ops[end].Kind is LineOpKind.Insert or LineOpKind.Delete; end++)
                    {
                        if (ops[end].Kind == LineOpKind.Insert)
                            run.Add(ops[end].NewIndex);
                        else
                            edits.Add(new DayEdit(DayEditKind.Delete, notes[ops[end].OldIndex].Id, "", default));
                    }

                    var prev = Neighbor(ops, notes, k, backwards: true);
                    var next = Neighbor(ops, notes, end - 1, backwards: false);
                    var times = InsertTimes(prev?.CreatedAt, next?.CreatedAt, run.Count, day, now);
                    for (var r = 0; r < run.Count; r++)
                        edits.Add(new DayEdit(DayEditKind.Insert, 0, lines[run[r]].Trim(), times[r]));

                    k = end - 1;
                    break;
            }
        }

        return edits;
    }

    /// <summary>Notun düzenlenmiş metindeki satır numarası (0 tabanlı); bulunamazsa -1.</summary>
    public static int LineOfNote(IReadOnlyList<Note> notes, string text, long noteId)
    {
        var index = -1;
        for (var i = 0; i < notes.Count; i++)
        {
            if (notes[i].Id == noteId)
            {
                index = i;
                break;
            }
        }
        if (index < 0) return -1;

        foreach (var op in LineDiff.Map(notes.Select(n => n.Text).ToList(), SplitLines(text)))
        {
            if (op.Kind is LineOpKind.Keep or LineOpKind.Update && op.OldIndex == index)
                return op.NewIndex;
        }
        return -1;
    }

    /// <summary>Metin dışarıdan yenilendiğinde imlecin bulunduğu satırı yeni metne taşır: satır aynı
    /// içerikle (ya da düzenlenmiş haliyle) duruyorsa onun yeni konumu, yoksa kendisinden önceki son
    /// eşleşen satırın hemen altı. Sonuç her zaman yeni metnin satır aralığındadır.</summary>
    public static int MapLine(string oldText, string newText, int oldLine)
    {
        var newCount = SplitLines(newText).Length;
        if (newCount == 0) return 0;

        var mapped = new Dictionary<int, int>();
        foreach (var op in LineDiff.Map(SplitLines(oldText), SplitLines(newText)))
        {
            if (op.Kind is LineOpKind.Keep or LineOpKind.Update)
                mapped[op.OldIndex] = op.NewIndex;
        }

        int result;
        if (mapped.TryGetValue(oldLine, out var direct))
        {
            result = direct;
        }
        else
        {
            result = 0;
            for (var i = oldLine - 1; i >= 0; i--)
            {
                if (mapped.TryGetValue(i, out var before))
                {
                    result = before + 1;
                    break;
                }
            }
        }
        return Math.Clamp(result, 0, newCount - 1);
    }

    /// <summary>Karakter konumunu (satır, sütun) çiftine çevirir; \r\n ve \n satır sonlarını tanır.</summary>
    public static (int Line, int Column) LineColumnOf(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, index - lineStart);
    }

    /// <summary>(satır, sütun) çiftini karakter konumuna çevirir; satır son satıra, sütun satır
    /// uzunluğuna sıkıştırılır (\r satır sonuna dahildir).</summary>
    public static int IndexOf(string text, int line, int column)
    {
        var lineStart = 0;
        for (var l = 0; l < line; l++)
        {
            var nl = text.IndexOf('\n', lineStart);
            if (nl < 0) break;
            lineStart = nl + 1;
        }

        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r') lineEnd--;

        return lineStart + Math.Clamp(column, 0, lineEnd - lineStart);
    }

    private static Note? Neighbor(IReadOnlyList<LineOp> ops, IReadOnlyList<Note> notes, int index, bool backwards)
    {
        if (backwards)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (ops[i].Kind is LineOpKind.Keep or LineOpKind.Update)
                    return notes[ops[i].OldIndex];
            }
        }
        else
        {
            for (var i = index + 1; i < ops.Count; i++)
            {
                if (ops[i].Kind is LineOpKind.Keep or LineOpKind.Update)
                    return notes[ops[i].OldIndex];
            }
        }
        return null;
    }

    /// <summary><paramref name="count"/> yeni not için, önceki (<paramref name="prev"/>) ve sonraki
    /// (<paramref name="next"/>) notun arasında kalan, artan sıralı ve gün içinde kalan saatler üretir.
    /// Tercihen dakikalık adımlar kullanılır (önceki + 1 dk, + 2 dk…; en üste eklemede sonraki − n dk);
    /// bugünün sonuna eklenen notlar "şimdi" saatini alır. Yer yoksa aralık eşit bölünür.</summary>
    private static DateTimeOffset[] InsertTimes(DateTimeOffset? prev, DateTimeOffset? next, int count, DateOnly day, DateTimeOffset now)
    {
        var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), now.Offset);
        var dayEnd = dayStart.AddDays(1);
        var isToday = day == DateOnly.FromDateTime(now.DateTime);
        var result = new DateTimeOffset[count];
        if (count == 0) return result;

        DateTimeOffset first;
        TimeSpan step;
        DateTimeOffset lower; // dışlayıcı alt sınır (eşit bölmede kullanılır)
        DateTimeOffset upper; // dışlayıcı üst sınır

        if (prev is { } p && next is { } n)
        {
            first = p + Minute;
            step = Minute;
            lower = p;
            upper = n;
        }
        else if (prev is { } p2)
        {
            (first, step) = isToday && now > p2 ? (now, Tick) : (p2 + Minute, Minute);
            lower = p2;
            upper = dayEnd;
        }
        else if (next is { } n2)
        {
            first = n2 - Minute * count;
            step = Minute;
            lower = dayStart - Tick;
            upper = n2;
        }
        else
        {
            first = isToday ? now : dayStart + now.TimeOfDay;
            step = Tick;
            lower = dayStart - Tick;
            upper = dayEnd;
        }

        var last = first + step * (count - 1);
        if (first > lower && first >= dayStart && last < upper)
        {
            for (var i = 0; i < count; i++)
                result[i] = first + step * i;
            return result;
        }

        // Tercih edilen adımlar sığmıyor: aralığı eşit böl (depo milisaniye hassasiyetinde saklar).
        var gapMs = (long)(upper - lower).TotalMilliseconds;
        var stepMs = Math.Max(0, gapMs / (count + 1));
        var floor = lower < dayStart ? dayStart : lower;
        for (var i = 0; i < count; i++)
        {
            var t = lower + TimeSpan.FromMilliseconds(stepMs * (i + 1));
            result[i] = stepMs == 0 || t < floor ? floor : t;
        }
        return result;
    }
}
