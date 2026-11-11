using System.Globalization;

namespace Notlar.Core;

public sealed record DayGroup(DateOnly Date, IReadOnlyList<Note> Notes);

public static class DayGrouper
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static IReadOnlyList<DayGroup> Group(IEnumerable<Note> notes) =>
        notes.GroupBy(n => n.Day)
             .OrderByDescending(g => g.Key)
             .Select(g => new DayGroup(g.Key, g.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToList()))
             .ToList();

    public static string FormatDate(DateOnly date) => date.ToString("d MMMM yyyy, dddd", Tr);

    public static string Header(DateOnly date, DateOnly today)
    {
        if (date == today) return $"BUGÜN · {FormatDate(date)}";
        if (date == today.AddDays(-1)) return $"DÜN · {FormatDate(date)}";
        return FormatDate(date);
    }
}
