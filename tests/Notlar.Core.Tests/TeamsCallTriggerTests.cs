using Notlar.Core;

namespace Notlar.Core.Tests;

public class TeamsCallTriggerTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 14, 0, 0);

    private static TeamsCallTrigger Create() => new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    private static DateTime At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>Aramayı başlatır (mikrofon 0. ve 3. saniyede kullanımda) ve 3. saniyedeki kararı döndürür.</summary>
    private static ScheduleAction BeginCall(TeamsCallTrigger trigger, bool isRecording = false)
    {
        Assert.Equal(ScheduleAction.None, trigger.Update(At(0), true, isRecording, false));
        return trigger.Update(At(3), true, isRecording, false);
    }

    [Fact]
    public void Starts_after_microphone_stays_in_use_for_start_delay()
    {
        var trigger = Create();
        Assert.Equal(ScheduleAction.None, trigger.Update(At(0), true, false, false));
        Assert.False(trigger.InCall);
        Assert.Equal(ScheduleAction.None, trigger.Update(At(1), true, false, false));
        Assert.Equal(ScheduleAction.Start, trigger.Update(At(2), true, false, false));
        Assert.True(trigger.InCall);
    }

    [Fact]
    public void Brief_microphone_use_does_not_start()
    {
        var trigger = Create();
        Assert.Equal(ScheduleAction.None, trigger.Update(At(0), true, false, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(1), false, false, false));
        // Süre baştan sayılır.
        Assert.Equal(ScheduleAction.None, trigger.Update(At(3), true, false, false));
        Assert.False(trigger.InCall);
        Assert.Equal(ScheduleAction.Start, trigger.Update(At(5), true, false, false));
    }

    [Fact]
    public void Starts_only_once_per_call()
    {
        var trigger = Create();
        Assert.Equal(ScheduleAction.Start, BeginCall(trigger));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(6), true, true, true));
        // Kullanıcı kaydı arama sürerken durdurdu (ya da başlatma başarısız oldu): yeniden başlatılmaz.
        Assert.Equal(ScheduleAction.None, trigger.Update(At(9), true, false, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(600), true, false, false));
    }

    [Fact]
    public void Does_not_start_when_already_recording()
    {
        var trigger = Create();
        Assert.Equal(ScheduleAction.None, BeginCall(trigger, isRecording: true));
        Assert.True(trigger.InCall);
        // O kayıt arama sürerken bitse de bu arama için yeni kayıt başlatılmaz.
        Assert.Equal(ScheduleAction.None, trigger.Update(At(6), true, false, false));
    }

    [Fact]
    public void Stops_own_recording_after_end_delay()
    {
        var trigger = Create();
        BeginCall(trigger);
        Assert.Equal(ScheduleAction.None, trigger.Update(At(60), false, true, true));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(69), false, true, true));
        Assert.True(trigger.InCall);
        Assert.Equal(ScheduleAction.Stop, trigger.Update(At(70), false, true, true));
        Assert.False(trigger.InCall);
    }

    [Fact]
    public void Short_dropout_does_not_stop_and_resets_end_delay()
    {
        var trigger = Create();
        BeginCall(trigger);
        Assert.Equal(ScheduleAction.None, trigger.Update(At(60), false, true, true));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(66), true, true, true));
        // Yeni kopma: süre baştan sayılır (60'tan değil 100'den).
        Assert.Equal(ScheduleAction.None, trigger.Update(At(100), false, true, true));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(109), false, true, true));
        Assert.Equal(ScheduleAction.Stop, trigger.Update(At(110), false, true, true));
    }

    [Fact]
    public void Never_stops_recording_started_by_someone_else()
    {
        var trigger = Create();
        BeginCall(trigger, isRecording: true);
        Assert.Equal(ScheduleAction.None, trigger.Update(At(60), false, true, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(70), false, true, false));
        Assert.False(trigger.InCall);
    }

    [Fact]
    public void Does_not_stop_manual_recording_started_after_user_stopped_teams_recording()
    {
        var trigger = Create();
        BeginCall(trigger);
        // Kullanıcı Teams kaydını durdurup elle yenisini başlattı; arama bitince ona dokunulmaz.
        Assert.Equal(ScheduleAction.None, trigger.Update(At(30), true, true, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(60), false, true, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(70), false, true, false));
    }

    [Fact]
    public void Call_end_without_recording_does_nothing_and_next_call_starts_again()
    {
        var trigger = Create();
        BeginCall(trigger);
        Assert.Equal(ScheduleAction.None, trigger.Update(At(60), false, false, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(70), false, false, false));
        Assert.False(trigger.InCall);

        Assert.Equal(ScheduleAction.None, trigger.Update(At(100), true, false, false));
        Assert.Equal(ScheduleAction.Start, trigger.Update(At(102), true, false, false));
    }

    [Fact]
    public void Idle_microphone_does_nothing()
    {
        var trigger = Create();
        Assert.Equal(ScheduleAction.None, trigger.Update(At(0), false, false, false));
        Assert.Equal(ScheduleAction.None, trigger.Update(At(100), false, true, true));
        Assert.False(trigger.InCall);
    }

    [Fact]
    public void Reset_forgets_call_so_ongoing_call_is_detected_again()
    {
        var trigger = Create();
        BeginCall(trigger);
        trigger.Reset();
        Assert.False(trigger.InCall);
        Assert.Equal(ScheduleAction.None, trigger.Update(At(10), true, false, false));
        Assert.Equal(ScheduleAction.Start, trigger.Update(At(12), true, false, false));
    }

    [Theory]
    [InlineData("MSTeams_8wekyb3d8bbwe")]
    [InlineData("MicrosoftTeams_8wekyb3d8bbwe")]
    [InlineData("msteams_8wekyb3d8bbwe")]
    public void IsTeamsKey_matches_packaged_teams(string name) => Assert.True(TeamsCallTrigger.IsTeamsKey(name, false));

    [Theory]
    [InlineData("C:#Users#biri#AppData#Local#Microsoft#Teams#current#Teams.exe")]
    [InlineData("C:#Program Files#WindowsApps#MSTeams_1.0#ms-teams.exe")]
    [InlineData("C:#X#TEAMS.EXE")]
    public void IsTeamsKey_matches_non_packaged_teams(string name) => Assert.True(TeamsCallTrigger.IsTeamsKey(name, true));

    [Theory]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", false)]
    [InlineData("NonPackaged", false)]
    [InlineData("Teams.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("C:#Users#biri#AppData#Local#Programs#Notlar#app#NotlarApp.exe", true)]
    [InlineData("C:#Araclar#NotTeams.exe", true)]
    [InlineData("C:#Microsoft#Teams#current#Squirrel.exe", true)]
    [InlineData("MSTeams_8wekyb3d8bbwe", true)]
    public void IsTeamsKey_rejects_other_apps(string? name, bool nonPackaged) =>
        Assert.False(TeamsCallTrigger.IsTeamsKey(name, nonPackaged));
}
