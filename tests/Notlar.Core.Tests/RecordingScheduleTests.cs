using Notlar.Core;

namespace Notlar.Core.Tests;

public class RecordingScheduleTests
{
    private static readonly TimeOnly Start = new(9, 30);
    private static readonly TimeOnly End = new(10, 30);

    // 23 Kasım 2026 Pazartesi; 28 Kasım 2026 Cumartesi.
    private static readonly DateOnly MondayDate = new(2026, 11, 23);

    private static DateTime Monday(int hour, int minute) => new(2026, 11, 23, hour, minute, 0);

    private static DateTime Saturday(int hour, int minute) => new(2026, 11, 28, hour, minute, 0);

    [Theory]
    [InlineData(9, 30)]
    [InlineData(9, 45)]   // uygulama pencere içinde açıldı
    [InlineData(10, 29)]
    public void Starts_inside_window_on_weekdays(int hour, int minute) =>
        Assert.Equal(ScheduleAction.Start, RecordingSchedule.Decide(Monday(hour, minute), Start, End, false, false, null));

    [Theory]
    [InlineData(9, 29)]
    [InlineData(10, 30)]
    [InlineData(15, 0)]
    public void Does_nothing_outside_window(int hour, int minute) =>
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(hour, minute), Start, End, false, false, null));

    [Fact]
    public void Does_not_start_on_weekend() =>
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Saturday(9, 45), Start, End, false, false, null));

    [Fact]
    public void Starts_only_once_per_day()
    {
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(9, 50), Start, End, false, false, MondayDate));
        Assert.Equal(ScheduleAction.Start,
            RecordingSchedule.Decide(Monday(9, 50), Start, End, false, false, MondayDate.AddDays(-3)));
    }

    [Fact]
    public void Stops_automatic_recording_when_window_ends()
    {
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(10, 29), Start, End, true, true, MondayDate));
        Assert.Equal(ScheduleAction.Stop, RecordingSchedule.Decide(Monday(10, 30), Start, End, true, true, MondayDate));
    }

    [Fact]
    public void Never_touches_manual_recording()
    {
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(9, 45), Start, End, true, false, null));
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(11, 0), Start, End, true, false, null));
    }

    [Fact]
    public void Invalid_window_never_starts()
    {
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(9, 45), End, Start, false, false, null));
        Assert.Equal(ScheduleAction.None, RecordingSchedule.Decide(Monday(9, 45), Start, Start, false, false, null));
    }

    [Theory]
    [InlineData("09:30", 9, 30)]
    [InlineData("9:05", 9, 5)]
    [InlineData(" 10:30 ", 10, 30)]
    public void TryParseTime_accepts_hour_minute(string text, int hour, int minute)
    {
        Assert.True(RecordingSchedule.TryParseTime(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("25:00")]
    [InlineData("dokuz")]
    public void TryParseTime_rejects_garbage(string? text) => Assert.False(RecordingSchedule.TryParseTime(text, out _));
}
