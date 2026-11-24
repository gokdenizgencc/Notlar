namespace Notlar.Core;

/// <summary>Teams'in şu an mikrofonu kullanıp kullanmadığını söyler (kayıt defteri erişimi App tarafındadır).</summary>
public interface ITeamsCallProbe
{
    bool IsMicrophoneInUse();
}

/// <summary>Teams araması başlayınca kaydı başlatıp bitince durduran karar mantığı (saf; yoklama zamanlayıcısı App
/// tarafındadır). Her yoklamada <see cref="Update"/> çağrılır.
/// <list type="bullet">
/// <item>Mikrofon <c>startDelay</c> boyunca kesintisiz kullanımdaysa arama başlamış sayılır; o an kayıt yoksa
/// <see cref="ScheduleAction.Start"/>. Kayıt zaten sürüyorsa (elle ya da zamanlamayla) yenisi başlatılmaz.</item>
/// <item>Arama sürerken mikrofon <c>endDelay</c> boyunca kesintisiz boştaysa arama bitmiş sayılır; süren kayıt Teams
/// tetiğiyle başlatılmışsa <see cref="ScheduleAction.Stop"/>. Daha kısa kopmalar kaydı bölmez.</item>
/// <item>Her arama için en çok bir kez başlatma denenir: kullanıcı kaydı arama sürerken durdurursa ya da başlatma
/// başarısız olursa aynı aramada yeniden başlatılmaz.</item>
/// </list></summary>
public sealed class TeamsCallTrigger(TimeSpan startDelay, TimeSpan endDelay)
{
    private DateTime? _activeSince;
    private DateTime? _idleSince;

    /// <summary>Arama sürüyor mu (başlangıcı onaylanmış, bitişi henüz onaylanmamış)?</summary>
    public bool InCall { get; private set; }

    /// <param name="now">Şimdiki zaman (yalnızca farkı kullanılır).</param>
    /// <param name="micInUse">Teams şu an mikrofonu kullanıyor mu?</param>
    /// <param name="isRecording">Şu an bir kayıt sürüyor mu?</param>
    /// <param name="currentIsTeams">Süren kayıt bu tetikle mi başlatıldı?</param>
    public ScheduleAction Update(DateTime now, bool micInUse, bool isRecording, bool currentIsTeams)
    {
        if (micInUse)
        {
            _idleSince = null;
            if (InCall) return ScheduleAction.None;

            _activeSince ??= now;
            if (now - _activeSince < startDelay) return ScheduleAction.None;

            _activeSince = null;
            InCall = true;
            return isRecording ? ScheduleAction.None : ScheduleAction.Start;
        }

        _activeSince = null;
        if (!InCall) return ScheduleAction.None;

        _idleSince ??= now;
        if (now - _idleSince < endDelay) return ScheduleAction.None;

        _idleSince = null;
        InCall = false;
        return isRecording && currentIsTeams ? ScheduleAction.Stop : ScheduleAction.None;
    }

    /// <summary>Durumu sıfırlar (özellik kapatıldığında); yeniden açılınca süren arama baştan algılanır.</summary>
    public void Reset()
    {
        _activeSince = null;
        _idleSince = null;
        InCall = false;
    }

    /// <summary>Windows'un mikrofon kullanım kaydındaki (CapabilityAccessManager\ConsentStore\microphone) bir alt
    /// anahtar Teams'e mi ait? Paketli uygulamalarda ad paket ailesidir (yeni Teams: <c>MSTeams_…</c>, Windows 11
    /// ile gelen kişisel Teams: <c>MicrosoftTeams_…</c>); <c>NonPackaged</c> altında ad, <c>\</c> yerine <c>#</c>
    /// konmuş exe yoludur (klasik Teams: <c>…#Teams.exe</c>, paketsiz yeni Teams: <c>…#ms-teams.exe</c>).</summary>
    public static bool IsTeamsKey(string? keyName, bool nonPackaged)
    {
        if (string.IsNullOrEmpty(keyName)) return false;
        if (!nonPackaged)
        {
            return keyName.StartsWith("MSTeams_", StringComparison.OrdinalIgnoreCase)
                || keyName.StartsWith("MicrosoftTeams_", StringComparison.OrdinalIgnoreCase);
        }

        var exe = keyName[(keyName.LastIndexOf('#') + 1)..];
        return exe.Equals("Teams.exe", StringComparison.OrdinalIgnoreCase)
            || exe.Equals("ms-teams.exe", StringComparison.OrdinalIgnoreCase);
    }
}
