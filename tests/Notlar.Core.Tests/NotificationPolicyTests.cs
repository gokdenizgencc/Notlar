using Notlar.Core;

namespace Notlar.Core.Tests;

public class NotificationPolicyTests
{
    private static readonly TimeOnly Noon = new(12, 0);

    private static NotificationPreferences Quiet(int startHour, int endHour) =>
        new(QuietHoursEnabled: true, QuietStart: new TimeOnly(startHour, 0), QuietEnd: new TimeOnly(endHour, 0));

    [Theory]
    [InlineData(NotificationKind.RecordingManual)]
    [InlineData(NotificationKind.RecordingScheduled)]
    [InlineData(NotificationKind.RecordingTeams)]
    [InlineData(NotificationKind.TranscriptReady)]
    [InlineData(NotificationKind.SummaryReady)]
    [InlineData(NotificationKind.Info)]
    [InlineData(NotificationKind.Error)]
    [InlineData(NotificationKind.Test)]
    public void Defaults_show_everything(NotificationKind kind) =>
        Assert.True(NotificationPolicy.ShouldShow(kind, new NotificationPreferences(), Noon));

    [Theory]
    [InlineData(NotificationKind.RecordingManual)]
    [InlineData(NotificationKind.TranscriptReady)]
    [InlineData(NotificationKind.Info)]
    [InlineData(NotificationKind.Error)]
    public void Master_switch_off_hides_all(NotificationKind kind) =>
        Assert.False(NotificationPolicy.ShouldShow(kind, new NotificationPreferences(Enabled: false), Noon));

    [Fact]
    public void Test_notification_ignores_settings() =>
        Assert.True(NotificationPolicy.ShouldShow(NotificationKind.Test,
            Quiet(0, 23) with { Enabled = false }, Noon));

    [Fact]
    public void Per_kind_switches_apply_only_to_their_kind()
    {
        var prefs = new NotificationPreferences(RecordingTeams: false, SummaryReady: false);
        Assert.False(NotificationPolicy.ShouldShow(NotificationKind.RecordingTeams, prefs, Noon));
        Assert.False(NotificationPolicy.ShouldShow(NotificationKind.SummaryReady, prefs, Noon));
        Assert.True(NotificationPolicy.ShouldShow(NotificationKind.RecordingScheduled, prefs, Noon));
        Assert.True(NotificationPolicy.ShouldShow(NotificationKind.TranscriptReady, prefs, Noon));
    }

    [Fact]
    public void Error_switch_off_hides_errors() =>
        Assert.False(NotificationPolicy.ShouldShow(NotificationKind.Error, new NotificationPreferences(Errors: false), Noon));

    [Fact]
    public void Quiet_hours_mute_everything_but_errors()
    {
        var prefs = Quiet(11, 13);
        Assert.False(NotificationPolicy.ShouldShow(NotificationKind.TranscriptReady, prefs, Noon));
        Assert.False(NotificationPolicy.ShouldShow(NotificationKind.Info, prefs, Noon));
        Assert.True(NotificationPolicy.ShouldShow(NotificationKind.Error, prefs, Noon));
    }

    [Fact]
    public void Quiet_hours_disabled_does_not_mute() =>
        Assert.True(NotificationPolicy.ShouldShow(NotificationKind.TranscriptReady,
            Quiet(11, 13) with { QuietHoursEnabled = false }, Noon));

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(21, 59, false)]
    public void Overnight_range_wraps_midnight(int hour, int minute, bool expected) =>
        Assert.Equal(expected, NotificationPolicy.IsInQuietHours(new TimeOnly(hour, minute), new(22, 0), new(8, 0)));

    [Theory]
    [InlineData(12, 59, false)]
    [InlineData(13, 0, true)]
    [InlineData(17, 59, true)]
    [InlineData(18, 0, false)]
    public void Same_day_range_is_half_open(int hour, int minute, bool expected) =>
        Assert.Equal(expected, NotificationPolicy.IsInQuietHours(new TimeOnly(hour, minute), new(13, 0), new(18, 0)));

    [Fact]
    public void Equal_start_and_end_is_empty() =>
        Assert.False(NotificationPolicy.IsInQuietHours(Noon, Noon, Noon));
}
