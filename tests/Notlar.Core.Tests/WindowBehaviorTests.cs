using Notlar.Core;

namespace Notlar.Core.Tests;

public class WindowBehaviorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tray")]
    [InlineData("bilinmeyen")]
    public void Close_mode_defaults_to_tray(string? value) =>
        Assert.Equal(WindowCloseMode.HideToTray, WindowBehavior.ParseCloseMode(value));

    [Theory]
    [InlineData("exit")]
    [InlineData("EXIT")]
    [InlineData(" exit ")]
    public void Close_mode_exit_is_parsed(string value) =>
        Assert.Equal(WindowCloseMode.Exit, WindowBehavior.ParseCloseMode(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("panel")]
    [InlineData("pencere")]
    public void Tray_click_defaults_to_toggle_panel(string? value) =>
        Assert.Equal(TrayClickMode.TogglePanel, WindowBehavior.ParseTrayClick(value));

    [Theory]
    [InlineData("day")]
    [InlineData("Day")]
    public void Tray_click_day_is_parsed(string value) =>
        Assert.Equal(TrayClickMode.OpenTodayWindow, WindowBehavior.ParseTrayClick(value));

    [Theory]
    [InlineData(WindowCloseMode.HideToTray)]
    [InlineData(WindowCloseMode.Exit)]
    public void Close_mode_round_trips(WindowCloseMode mode) =>
        Assert.Equal(mode, WindowBehavior.ParseCloseMode(WindowBehavior.Format(mode)));

    [Theory]
    [InlineData(TrayClickMode.TogglePanel)]
    [InlineData(TrayClickMode.OpenTodayWindow)]
    public void Tray_click_round_trips(TrayClickMode mode) =>
        Assert.Equal(mode, WindowBehavior.ParseTrayClick(WindowBehavior.Format(mode)));

    [Fact]
    public void Exit_message_mentions_recording_only_while_recording()
    {
        Assert.Contains("Kayıt sürüyor", WindowBehavior.ExitConfirmMessage(isRecording: true));
        Assert.DoesNotContain("Kayıt sürüyor", WindowBehavior.ExitConfirmMessage(isRecording: false));
    }
}
