using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Notlar.Core;

namespace Notlar.App;

/// <summary>⚙ Ayarlar → Kısayollar: global kısayolların aç/kapa + tuş kaydedici satırları ve uygulama içi kısayolların
/// salt okunur listesi. Her değişiklik <see cref="AppSettings.Update"/> ile anında kaydedilir; <see cref="GlobalHotkeys"/>
/// ayar değişikliğini görüp kısayolları yeniden kaydeder, sonucu (ör. başka uygulama kullanıyor) satırda gösterir.</summary>
public partial class ShortcutSettings : UserControl
{
    private const string CapturePrompt = "Tuşlara basın…";

    private static readonly (GlobalHotkeyAction Action, string Title, string Hint)[] Actions =
    [
        (GlobalHotkeyAction.TogglePanel, "Paneli aç / kapat",
            "Panel öndeyse gizler, arkada kaldıysa öne getirir, kapalıysa açar."),
        (GlobalHotkeyAction.QuickNote, "Hızlı not",
            "Paneli Notlar bölümünde açar ve imleci not kutusuna koyar."),
        (GlobalHotkeyAction.ToggleRecording, "Kaydı başlat / durdur",
            "Toplantı kaydını başlatır; kayıt sürüyorsa durdurur."),
    ];

    // Kodda tanımlı uygulama içi kısayollar (PanelWindow, DayWindow, NoteEditorWindow, DayLineEditor, PlanStepsEditor,
    // RecordingsView ve iletişim pencereleri). Yeni bir kısayol eklenince buraya da yazılmalı.
    private static readonly (string Group, (string Keys, string Description)[] Items)[] InAppShortcuts =
    [
        ("Panel", [
            ("Ctrl+1…5", "Notlar, Hafıza, Planlar, Kayıtlar, Yetenekler bölümüne geç"),
            ("Enter", "Not / görev ekle; düzenlemeyi kaydet"),
            ("Ctrl+Enter", "Hafıza içeriğini düzenlerken kaydet"),
            ("Esc", "Düzenlemeden vazgeç, açılır kutuyu ya da seçimi kapat, geri dön; en sonda paneli gizle"),
            ("Delete", "Seçili notu, görevi, hafıza kaydını ya da plan adımını sil"),
        ]),
        ("Gün ve not düzenleyici", [
            ("Ctrl+S", "Kaydet"),
            ("Ctrl+D", "İmlecin olduğu notu yapıldı / yapılmadı yap"),
            ("Alt+Z", "Satır kaydırmayı aç / kapat"),
            ("Ctrl+tekerlek", "Yazı boyutunu büyüt / küçült (Ctrl + / Ctrl − de olur)"),
            ("Ctrl+0", "Yazı boyutunu varsayılana döndür"),
            ("Ctrl+W", "Not editöründe sekmeyi kapat"),
            ("Ctrl+Tab", "Not editöründe sonraki sekme"),
            ("Esc", "Gün penceresini kapat"),
        ]),
        ("Plan adımları", [
            ("Enter", "Altına yeni adım ekle (Shift+Enter: satır içinde alt satır)"),
            ("Ctrl+↑ / Ctrl+↓", "Adımı yukarı / aşağı taşı"),
            ("Backspace", "Boş adımı sil"),
        ]),
        ("Kayıtlar", [
            ("Enter", "Seçili kaydı aç; başlık düzenlenirken kaydet"),
            ("Esc", "Başlık düzenlemesinden vazgeç"),
        ]),
        ("İletişim pencereleri", [
            ("Esc", "Plana ekle, Görünüm ve plan çalıştırma onayı pencerelerini kapat"),
        ]),
    ];

    private sealed class Row
    {
        public required GlobalHotkeyAction Action { get; init; }
        public required CheckBox Check { get; init; }
        public required TextBox Box { get; init; }
        public required TextBlock Error { get; init; }
        public string? LocalError { get; set; }
        public bool Capturing { get; set; }
    }

    private readonly List<Row> _rows = new();
    private bool _syncing;
    private GlobalHotkeys? _hotkeys;

    public ShortcutSettings()
    {
        InitializeComponent();
        foreach (var (action, title, hint) in Actions)
            _rows.Add(BuildRow(action, title, hint));
        BuildInAppList();

        Loaded += (_, _) =>
        {
            AppSettings.Changed += OnSettingsChanged;
            _hotkeys = GlobalHotkeys.Instance;
            if (_hotkeys is not null) _hotkeys.StatusChanged += OnStatusChanged;
            Reload();
        };
        Unloaded += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            if (_hotkeys is not null) _hotkeys.StatusChanged -= OnStatusChanged;
            foreach (var row in _rows) EndCapture(row);
            _hotkeys = null;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Reload();
        };
    }

    private Row BuildRow(GlobalHotkeyAction action, string title, string hint)
    {
        var check = new CheckBox { Content = title, Margin = new Thickness(0, 0, 0, 4) };
        check.SetResourceReference(StyleProperty, "SettingCheck");
        AutomationProperties_SetName(check, title);

        var box = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsUndoEnabled = false,
            MinWidth = 150,
            Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Tıklayıp yeni tuş birleşimine basın. Esc: vazgeç.",
        };
        box.SetResourceReference(BackgroundProperty, "Surface");
        box.SetResourceReference(ForegroundProperty, "Fg");
        box.SetResourceReference(BorderBrushProperty, "Border");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        AutomationProperties_SetName(box, title + " kısayolu");
        InputMethod.SetIsInputMethodEnabled(box, false);

        var reset = new Button { Content = "Varsayılan", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Önerilen kısayol: " + AppSettings.DefaultHotkey(action) };
        reset.SetResourceReference(StyleProperty, "LinkButton");

        var line = new DockPanel { Margin = new Thickness(20, 0, 0, 0), LastChildFill = false };
        line.Children.Add(box);
        line.Children.Add(reset);

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 4, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        error.FontSize = 12;

        var hintText = new TextBlock { Text = hint };
        hintText.SetResourceReference(StyleProperty, "CheckHint");
        hintText.Margin = new Thickness(20, 4, 0, 12);

        var container = new StackPanel();
        container.Children.Add(check);
        container.Children.Add(line);
        container.Children.Add(error);
        container.Children.Add(hintText);
        RowsPanel.Children.Add(container);

        var row = new Row { Action = action, Check = check, Box = box, Error = error };
        check.Checked += (_, _) => EnabledChanged(row);
        check.Unchecked += (_, _) => EnabledChanged(row);
        box.GotKeyboardFocus += (_, _) => BeginCapture(row);
        box.LostKeyboardFocus += (_, _) => { EndCapture(row); Reload(); };
        box.PreviewKeyDown += (_, e) => Box_PreviewKeyDown(row, e);
        box.PreviewTextInput += (_, e) => e.Handled = true;
        reset.Click += (_, _) => ResetToDefault(row);
        return row;
    }

    private static void AutomationProperties_SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private void BuildInAppList()
    {
        var rowIndex = 0;
        foreach (var (group, items) in InAppShortcuts)
        {
            InAppGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new TextBlock { Text = group, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, rowIndex == 0 ? 0 : 8, 0, 2) };
            Grid.SetRow(header, rowIndex);
            Grid.SetColumnSpan(header, 2);
            InAppGrid.Children.Add(header);
            rowIndex++;

            foreach (var (keys, description) in items)
            {
                InAppGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var keyText = new TextBlock { Text = keys, FontSize = 12, FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI"),
                    Margin = new Thickness(0, 1, 12, 1), VerticalAlignment = VerticalAlignment.Top };
                var descText = new TextBlock { Text = description, Margin = new Thickness(0, 1, 0, 1) };
                descText.SetResourceReference(StyleProperty, "Hint");
                Grid.SetRow(keyText, rowIndex);
                Grid.SetRow(descText, rowIndex);
                Grid.SetColumn(descText, 1);
                InAppGrid.Children.Add(keyText);
                InAppGrid.Children.Add(descText);
                rowIndex++;
            }
        }
    }

    // ── Yükleme / durum ───────────────────────────────────────────────────

    /// <summary>Güncel ayarları ve kayıt durumlarını satırlara yansıtır (kaydedilmekte olan kutuya dokunmaz).</summary>
    public void Reload()
    {
        AppSettings settings;
        try
        {
            settings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return;
        }

        _syncing = true;
        try
        {
            foreach (var row in _rows)
            {
                row.Check.IsChecked = settings.GetHotkeyEnabled(row.Action);
                if (!row.Capturing) row.Box.Text = settings.GetHotkey(row.Action);
                row.Box.Opacity = row.Check.IsChecked == true || row.Capturing ? 1.0 : 0.6;
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateErrors();
    }

    private void OnSettingsChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (IsVisible) Reload();
    });

    private void OnStatusChanged() => Dispatcher.InvokeAsync(UpdateErrors);

    private void UpdateErrors()
    {
        foreach (var row in _rows)
        {
            var message = row.LocalError;
            if (message is null && row.Check.IsChecked == true && !row.Capturing
                && GlobalHotkeys.Instance?.StatusOf(row.Action) is { Registered: false, Error: { } error })
                message = "⚠ " + error;
            row.Error.Text = message ?? "";
            row.Error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ── Aç/kapa, varsayılan ───────────────────────────────────────────────

    private void EnabledChanged(Row row)
    {
        if (_syncing) return;
        var enabled = row.Check.IsChecked == true;
        row.LocalError = null;
        Save(s => s.SetHotkeyEnabled(row.Action, enabled));
    }

    private void ResetToDefault(Row row)
    {
        var combo = AppSettings.DefaultHotkey(row.Action);
        if (ConflictMessage(row.Action, combo) is { } conflict)
        {
            row.LocalError = conflict;
            UpdateErrors();
            return;
        }
        row.LocalError = null;
        Save(s => s.SetHotkey(row.Action, combo));
    }

    private void Save(Action<AppSettings> mutate)
    {
        try
        {
            AppSettings.Update(mutate);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            foreach (var row in _rows) row.LocalError = null;
            _rows[0].LocalError = "Ayar kaydedilemedi: " + ex.Message;
        }
        Reload();
    }

    /// <summary>Kombinasyon başka bir eylemde (açık ya da kapalı) kullanılıyorsa açıklama döner; kapalı eylem sonradan
    /// açılınca çakışmasın diye kapalılar da sayılır.</summary>
    private static string? ConflictMessage(GlobalHotkeyAction action, string combo)
    {
        var settings = AppSettings.Load();
        var conflicts = HotkeyCombo.FindConflicts(Enum.GetValues<GlobalHotkeyAction>()
            .Select(a => (a, true, (string?)(a == action ? combo : settings.GetHotkey(a)))));
        if (!conflicts.TryGetValue(action, out var others) || others.Count == 0) return null;
        var other = Actions.First(a => a.Action == others[0]).Title;
        return $"{HotkeyCombo.Normalize(combo)} zaten \"{other}\" için atanmış.";
    }

    // ── Tuş kaydedici ─────────────────────────────────────────────────────

    private void BeginCapture(Row row)
    {
        if (row.Capturing) return;
        row.Capturing = true;
        row.LocalError = null;
        // Kayıtlı kısayollar askıya alınır; yoksa mevcut kombinasyona basmak kutuya hiç ulaşmaz.
        GlobalHotkeys.Instance?.Suspend();
        row.Box.Text = CapturePrompt;
        row.Box.Opacity = 1.0;
        row.Box.SetResourceReference(BorderBrushProperty, "Accent");
        UpdateErrors();
    }

    private void EndCapture(Row row)
    {
        if (!row.Capturing) return;
        row.Capturing = false;
        row.Box.SetResourceReference(BorderBrushProperty, "Border");
        GlobalHotkeys.Instance?.Resume();
    }

    private void Box_PreviewKeyDown(Row row, KeyEventArgs e)
    {
        e.Handled = true; // Alt menüyü açmasın, Tab/Enter vb. başka yere gitmesin.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        var modifiers = CurrentModifiers();
        if (key == Key.Escape && modifiers == HotkeyModifiers.None)
        {
            row.LocalError = null;
            FinishCapture(row);
            return;
        }
        if (key == Key.Tab && modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift)
        {
            // Klavyeyle gezinme bozulmasın: Tab / Shift+Tab odağı taşır.
            row.Box.MoveFocus(new TraversalRequest(modifiers == HotkeyModifiers.Shift
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        if (HotkeyCombo.IsModifierKey(vk))
        {
            // Yalnız değiştiricilere basılı: canlı önizleme ("Ctrl+Alt+…").
            row.Box.Text = modifiers == HotkeyModifiers.None
                ? CapturePrompt
                : new HotkeyCombo(modifiers, 0x41).ToString()[..^1] + "…";
            return;
        }

        var combo = new HotkeyCombo(modifiers, vk);
        if (!combo.IsValid)
        {
            row.Box.Text = combo.ToString();
            row.LocalError = "En az bir Ctrl, Alt ya da Win tuşu gerekir (yalnız Shift yazmayı bozar).";
            UpdateErrors();
            return;
        }

        var text = combo.ToString();
        if (ConflictMessage(row.Action, text) is { } conflict)
        {
            row.Box.Text = text;
            row.LocalError = conflict;
            UpdateErrors();
            return;
        }

        row.LocalError = null;
        try
        {
            // Kombinasyon kaydedilince eylem de açılır: tuş atamak "kullanmak istiyorum" demektir.
            AppSettings.Update(s =>
            {
                s.SetHotkey(row.Action, text);
                s.SetHotkeyEnabled(row.Action, true);
            });
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            row.LocalError = "Ayar kaydedilemedi: " + ex.Message;
        }
        FinishCapture(row);
    }

    /// <summary>Kaydı bitirir: odağı satırın onay kutusuna verir (LostKeyboardFocus → kısayollar yeniden kaydedilir).</summary>
    private void FinishCapture(Row row)
    {
        if (!row.Check.Focus())
        {
            EndCapture(row);
            Reload();
        }
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var wpf = Keyboard.Modifiers;
        var mods = HotkeyModifiers.None;
        if ((wpf & ModifierKeys.Control) != 0) mods |= HotkeyModifiers.Control;
        if ((wpf & ModifierKeys.Alt) != 0) mods |= HotkeyModifiers.Alt;
        if ((wpf & ModifierKeys.Shift) != 0) mods |= HotkeyModifiers.Shift;
        if ((wpf & ModifierKeys.Windows) != 0 || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            mods |= HotkeyModifiers.Win;
        return mods;
    }
}
