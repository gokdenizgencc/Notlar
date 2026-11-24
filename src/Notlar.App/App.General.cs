using System;
using System.Windows;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Genel davranışlarının uygulama tarafı: başlangıçta paneli açma, tepsi tıklaması, çıkış onayı.</summary>
public partial class App
{
    /// <summary>Uygulama bilinçli olarak kapanıyor (çıkış, kurulumun kapatma sinyali ya da Windows oturumu bitiyor):
    /// panelin Closing işleyicisi kapanmayı engellemez.</summary>
    internal static bool IsExiting { get; private set; }

    /// <summary>Panel ve tepsi kurulduktan sonra bir kez çağrılır.</summary>
    private void InitializeGeneralBehavior()
    {
        if (_panel is not { } panel) return;
        panel.ExitRequested += ExitApp;
        if (AppSettings.Load().StartPanelOpen) panel.ShowNearTray();
    }

    /// <summary>Tepsi simgesine sol tıklama: ayara göre paneli aç/kapat ya da bugünün gün penceresini aç.</summary>
    private void OnTrayClick()
    {
        if (_panel is not { } panel) return;
        if (AppSettings.Load().TrayClickMode == TrayClickMode.OpenTodayWindow) panel.OpenTodayWindow();
        else panel.Toggle();
    }

    /// <summary>Kullanıcının çıkış isteği (tepsi "Çıkış" ya da kapatma davranışı "Uygulamadan çık"): ayar açıksa önce
    /// onay sorulur.</summary>
    private void RequestExit()
    {
        if (AppSettings.Load().ConfirmExit)
        {
            var message = WindowBehavior.ExitConfirmMessage(_recordings?.IsRecording == true);
            // Panel her zaman üstte olabileceğinden, açıksa sahip olarak verilir; yoksa iletişim kutusu arkada kalır.
            var answer = _panel is { IsVisible: true } owner
                ? MessageBox.Show(owner, message, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(message, "Notlar", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        ShutdownNow();
    }

    /// <summary>Onay sormadan kapatır (kurulumun kapatma sinyali vb.).</summary>
    private void ShutdownNow()
    {
        IsExiting = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel) IsExiting = true;
    }
}
