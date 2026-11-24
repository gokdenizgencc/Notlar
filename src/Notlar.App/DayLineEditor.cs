using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Notlar.Core;

namespace Notlar.App;

/// <summary>Bir günün notlarını Notepad tarzı satır satır düzenlenen tek metin olarak gösterir (her satır = bir not).
/// Solda satır numaraları, imlecin bulunduğu satırda sert bir vurgu bandı; bir satırın (ya da numarasının)
/// üzerine gelince o notun yazıldığı saat/yazar ipucu olarak çıkar. Düzenlemeler kısa bir duraksamadan sonra,
/// odak kaybında ve Ctrl+S ile <see cref="DayTextSync"/> üzerinden kendiliğinden kaydedilir. Ctrl+D imlecin
/// satırındaki notu tamamlandı/tamamlanmadı yapar; Alt+Z satır kaydırmayı açar/kapatır.</summary>
public sealed class DayLineEditor : UserControl
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(250);

    private readonly TextBox _box;
    private readonly LineGutter _gutter;
    private readonly Rectangle _currentLine;
    private readonly ToolTip _tip;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hoverTimer;

    private NoteStore? _store;
    private List<Note> _notes = new();
    private string _originalText = "";
    private Note?[] _lineNotes = [];
    private bool _suppress;
    private bool _renderQueued;
    private bool _saving;
    private int _hoverLine = -1;
    private int _pendingHoverLine = -1;
    private bool _autoGrow;

    public DayLineEditor()
    {
        Focusable = false;

        _box = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 2, 8, 2),
        };
        _box.SetResourceReference(ForegroundProperty, "Fg");
        _box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Fg");
        _box.TextChanged += Box_TextChanged;
        _box.SelectionChanged += (_, _) => { QueueRender(); RaiseStateChanged(); BringCaretIntoView(); };
        _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => QueueRender()));
        _box.SizeChanged += (_, _) => QueueRender();
        _box.PreviewKeyDown += Box_PreviewKeyDown;
        _box.LostKeyboardFocus += (_, _) => Flush();
        _box.MouseMove += (_, e) => HoverAt(LineAtPoint(e.GetPosition(_box)));
        _box.MouseLeave += (_, _) => HoverAt(-1);

        _currentLine = new Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
        _currentLine.SetResourceReference(Shape.FillProperty, "EditorLine");

        _gutter = new LineGutter(this);
        _gutter.SetResourceReference(BackgroundProperty, "EditorGutter");
        _gutter.MouseMove += (_, e) => HoverAt(_gutter.LineAt(e.GetPosition(_gutter).Y));
        _gutter.MouseLeave += (_, _) => HoverAt(-1);
        _gutter.MouseLeftButtonDown += (_, e) =>
        {
            var line = _gutter.LineAt(e.GetPosition(_gutter).Y);
            if (line < 0) return;
            SelectLine(line);
            e.Handled = true;
        };

        var textHost = new Grid { ClipToBounds = true };
        textHost.SetResourceReference(Panel.BackgroundProperty, "EditorBg");
        textHost.Children.Add(_currentLine);
        textHost.Children.Add(_box);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_gutter, 0);
        Grid.SetColumn(textHost, 1);
        root.Children.Add(_gutter);
        root.Children.Add(textHost);
        Content = root;

        _tip = new ToolTip { PlacementTarget = _box, Placement = PlacementMode.Mouse, HasDropShadow = false };

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = AutoSaveDelay };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Flush(); };
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = HoverDelay };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ShowTip(_pendingHoverLine); };

        Unloaded += (_, _) => { Flush(); CloseTip(); };
        FontSizeChangedHook();
    }

    /// <summary>İmleç/metin/kayıt durumu değişti (durum çubuğu tazelemesi için).</summary>
    public event Action? StateChanged;

    /// <summary>Gösterilen gün; <see cref="Load"/> çağrılmadıysa null.</summary>
    public DateOnly? Day { get; private set; }

    public IReadOnlyList<Note> Notes => _notes;

    /// <summary>Kaydedildiğinde notlarda değişiklik yaratacak bir düzenleme var mı? (Boş satırlar sayılmaz.)</summary>
    public bool Dirty => DayTextSync.HasChanges(_notes, _box.Text);

    public string Text => _box.Text;
    public int CaretIndex => _box.CaretIndex;

    /// <summary>true: kendi kaydırma çubuğu olmaz, içeriğe göre uzar (dıştaki ScrollViewer kaydırır — panel).</summary>
    public bool AutoGrow
    {
        get => _autoGrow;
        set
        {
            _autoGrow = value;
            _box.VerticalScrollBarVisibility = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }
    }

    public bool WordWrap
    {
        get => _box.TextWrapping != TextWrapping.NoWrap;
        set
        {
            _box.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _box.HorizontalScrollBarVisibility = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            QueueRender();
        }
    }

    /// <summary>Verilen günü yükler. Başka bir gün açıksa önce onun düzenlemeleri kaydedilir.</summary>
    public void Load(NoteStore store, DateOnly day)
    {
        if (Day == day && ReferenceEquals(_store, store))
        {
            Reload();
            return;
        }

        Flush();
        _store = store;
        Day = day;
        _notes = ReadNotes();
        _originalText = DayTextSync.ToText(_notes);
        _suppress = true;
        _box.Text = _originalText;
        _box.CaretIndex = 0;
        _box.ScrollToHome();
        _suppress = false;
        _box.IsUndoEnabled = false;
        _box.IsUndoEnabled = true; // farklı günün geri al geçmişi taşınmasın
        RemapLines();
        QueueRender();
        RaiseStateChanged();
    }

    /// <summary>Depo dışarıdan değiştiğinde çağrılır. Kendi kaydımızın yankısıysa hiçbir şey yapmaz. Kaydedilmemiş
    /// düzenleme varsa önce onları birleştirerek kaydeder (yalnızca kullanıcının değiştirdiği satırlar yazılır);
    /// yoksa metni sessizce tazeler. İmleç, kaydırma ve geri al geçmişi mümkün olduğunca korunur.</summary>
    public void Reload()
    {
        if (_store is null || Day is null || _saving) return;

        if (Dirty)
        {
            Flush();
            return;
        }

        List<Note> fresh;
        try
        {
            fresh = ReadNotes();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return;
        }

        if (fresh.SequenceEqual(_notes)) return;
        _notes = fresh;
        _originalText = DayTextSync.ToText(_notes);
        SyncTextToNotes();
        RemapLines();
        QueueRender();
        RaiseStateChanged();
    }

    /// <summary>Kaydedilmemiş düzenleme varsa depoya yazar. Başarılıysa (ya da yazılacak bir şey yoksa) true.</summary>
    public bool Flush()
    {
        _saveTimer.Stop();
        if (_store is null || Day is null || _saving || !Dirty) return true;

        _saving = true;
        try
        {
            var plan = DayTextSync.Plan(_notes, _box.Text, Day.Value, DateTimeOffset.Now);
            var byId = _notes.ToDictionary(n => n.Id);
            foreach (var edit in plan)
            {
                switch (edit.Kind)
                {
                    case DayEditKind.Update:
                        try
                        {
                            _store.Update(edit.NoteId, edit.Text);
                        }
                        catch (NoteNotFoundException)
                        {
                            // Not bu arada dışarıda silinmiş: düzenleme kaybolmasın, aynı saat ve yazarla yeniden ekle.
                            var original = byId[edit.NoteId];
                            _store.AddAt(edit.Text, original.CreatedAt, original.Author);
                        }
                        break;
                    case DayEditKind.Delete:
                        try
                        {
                            _store.Delete(edit.NoteId);
                        }
                        catch (NoteNotFoundException)
                        {
                        }
                        break;
                    case DayEditKind.Insert:
                        _store.AddAt(edit.Text, edit.CreatedAt, NoteAuthor.Ben);
                        break;
                }
            }

            _notes = ReadNotes();
            _originalText = DayTextSync.ToText(_notes);
            SyncTextToNotes();
            RemapLines();
            QueueRender();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show(Window.GetWindow(this)!, $"Kaydetme başarısız: {ex.Message}", "Notlar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _saving = false;
            RaiseStateChanged();
        }
    }

    public void FocusEditor()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _box.Focus();
            Keyboard.Focus(_box);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>İmleci verilen notun satırının sonuna koyar ve satırı görünür yapar.</summary>
    public void MoveCaretToNote(long noteId)
    {
        var line = DayTextSync.LineOfNote(_notes, _box.Text, noteId);
        if (line < 0) return;
        _box.CaretIndex = DayTextSync.IndexOf(_box.Text, line, int.MaxValue);
        Dispatcher.InvokeAsync(() => { _box.ScrollToLine(_box.GetLineIndexFromCharacterIndex(_box.CaretIndex)); BringCaretIntoView(); }, DispatcherPriority.Loaded);
    }

    /// <summary>İmlecin mantıksal satırı ve sütunu (1 tabanlı).</summary>
    public (int Line, int Column) CaretPosition()
    {
        var (line, col) = DayTextSync.LineColumnOf(_box.Text, _box.CaretIndex);
        return (line + 1, col + 1);
    }

    public int LineCount => Math.Max(1, DayTextSync.SplitLines(_box.Text).Length);

    private List<Note> ReadNotes()
    {
        var day = Day!.Value;
        var notes = _store!.List(new NoteFilter(From: day, To: day));
        var ordered = DayGrouper.Group(notes).FirstOrDefault(g => g.Date == day)?.Notes ?? Array.Empty<Note>();
        return ordered.ToList();
    }

    /// <summary>Metin notlarla zaten örtüşüyorsa (boş satırlar/kenar boşlukları hariç) dokunmaz — imleç ve
    /// Ctrl+Z geçmişi korunur. Örtüşmüyorsa notlardan yeniden kurar, imleci aynı satıra taşır.</summary>
    private void SyncTextToNotes()
    {
        if (!DayTextSync.HasChanges(_notes, _box.Text)) return;

        var oldText = _box.Text;
        var (line, col) = DayTextSync.LineColumnOf(oldText, _box.CaretIndex);
        var newLine = DayTextSync.MapLine(oldText, _originalText, line);
        var vertical = _box.VerticalOffset;

        _suppress = true;
        _box.Text = _originalText;
        _box.CaretIndex = DayTextSync.IndexOf(_originalText, newLine, col);
        _suppress = false;
        Dispatcher.InvokeAsync(() => _box.ScrollToVerticalOffset(vertical), DispatcherPriority.Loaded);
    }

    /// <summary>Metnin her satırını eşleştiği nota bağlar (ipucu ve ✓ işareti için).</summary>
    private void RemapLines()
    {
        var newLines = DayTextSync.SplitLines(_box.Text);
        var map = new Note?[Math.Max(1, newLines.Length)];
        foreach (var op in LineDiff.Map(DayTextSync.SplitLines(_originalText), newLines))
        {
            if (op.Kind is LineOpKind.Keep or LineOpKind.Update && op.OldIndex >= 0 && op.OldIndex < _notes.Count && op.NewIndex < map.Length)
                map[op.NewIndex] = _notes[op.OldIndex];
        }
        _lineNotes = map;
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        RemapLines();
        QueueRender();
        RaiseStateChanged();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.S)
        {
            Flush();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.D)
        {
            ToggleDoneAtCaret();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Alt && e.SystemKey == Key.Z)
        {
            WordWrap = !WordWrap;
            e.Handled = true;
        }
    }

    private void ToggleDoneAtCaret()
    {
        if (_store is null) return;
        if (!Flush()) return;
        var (line, _) = DayTextSync.LineColumnOf(_box.Text, _box.CaretIndex);
        if (line >= _lineNotes.Length || _lineNotes[line] is not { } note) return;
        try
        {
            _store.SetDone(note.Id, !note.Done);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            return;
        }
        Reload();
    }

    private void SelectLine(int line)
    {
        var text = _box.Text;
        var start = DayTextSync.IndexOf(text, line, 0);
        var end = DayTextSync.IndexOf(text, line, int.MaxValue);
        _box.Focus();
        _box.Select(start, end - start);
    }

    private void BringCaretIntoView()
    {
        if (!_autoGrow || !_box.IsKeyboardFocused) return;
        var rect = _box.GetRectFromCharacterIndex(_box.CaretIndex);
        if (rect.IsEmpty) return;
        rect.Inflate(0, 6);
        _box.BringIntoView(rect);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private void FontSizeChangedHook()
    {
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FontSizeProperty, typeof(DayLineEditor));
        descriptor.AddValueChanged(this, (_, _) => QueueRender());
    }

    // ---- Çizim: satır numaraları + imleç satırı bandı ----

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.InvokeAsync(Render, DispatcherPriority.Loaded);
    }

    private void Render()
    {
        _renderQueued = false;
        var text = _box.Text;
        var starts = LineStarts(text);
        var (caretLine, _) = DayTextSync.LineColumnOf(text, _box.CaretIndex);

        var entries = new List<GutterEntry>();
        var height = _box.ActualHeight;
        for (var i = 0; i < starts.Count; i++)
        {
            var rect = _box.GetRectFromCharacterIndex(starts[i]);
            if (rect.IsEmpty) continue;
            if (rect.Bottom < 0) continue;
            if (rect.Top > height) break;
            var note = i < _lineNotes.Length ? _lineNotes[i] : null;
            entries.Add(new GutterEntry(i, rect.Top, rect.Height, i == caretLine, note?.Done == true));
        }
        _gutter.SetEntries(entries, starts.Count);

        // İmleç satırı bandı: satırın ilk karakterinin üstünden son karakterinin altına (kaydırılmış satırlar dahil).
        if (caretLine < starts.Count)
        {
            var startRect = _box.GetRectFromCharacterIndex(starts[caretLine]);
            var endIndex = caretLine + 1 < starts.Count ? starts[caretLine + 1] - 1 : text.Length;
            var endRect = _box.GetRectFromCharacterIndex(endIndex);
            if (!startRect.IsEmpty && !endRect.IsEmpty)
            {
                _currentLine.Margin = new Thickness(0, startRect.Top, 0, 0);
                _currentLine.Height = Math.Max(startRect.Height, endRect.Bottom - startRect.Top);
                _currentLine.Visibility = _box.IsKeyboardFocusWithin || !_autoGrow ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                _currentLine.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return starts;
    }

    // ---- Üzerine gelince saat ipucu ----

    private int LineAtPoint(Point p)
    {
        var index = _box.GetCharacterIndexFromPoint(p, snapToText: false);
        if (index < 0)
        {
            // Satır sonundaki boş alan: aynı yükseklikteki en yakın karaktere bak.
            index = _box.GetCharacterIndexFromPoint(p, snapToText: true);
            if (index < 0) return -1;
            var rect = _box.GetRectFromCharacterIndex(index);
            if (rect.IsEmpty || p.Y < rect.Top || p.Y > rect.Bottom) return -1;
        }
        return DayTextSync.LineColumnOf(_box.Text, index).Line;
    }

    private void HoverAt(int line)
    {
        if (line == _hoverLine && (_tip.IsOpen || _hoverTimer.IsEnabled)) return;
        _hoverLine = line;
        CloseTip();
        if (line < 0) return;
        _pendingHoverLine = line;
        _hoverTimer.Start();
    }

    private void CloseTip()
    {
        _hoverTimer.Stop();
        _tip.IsOpen = false;
    }

    private void ShowTip(int line)
    {
        if (line < 0 || line != _hoverLine) return;
        var content = TipFor(line);
        if (content is null) return;
        _tip.Content = content;
        _tip.IsOpen = true;
    }

    private string? TipFor(int line)
    {
        var lines = DayTextSync.SplitLines(_box.Text);
        if (line >= lines.Length || lines[line].Trim().Length == 0) return null;
        if (line >= _lineNotes.Length || _lineNotes[line] is not { } note)
            return "Yeni satır — henüz kaydedilmedi";

        var text = $"{note.CreatedAt.ToString("HH:mm", Tr)} · {note.Author}";
        if (note.CreatedAt.Date != DateTime.Today)
            text = note.CreatedAt.ToString("d MMMM dddd", Tr) + ", " + text;
        if (note.UpdatedAt is { } updated && updated - note.CreatedAt > TimeSpan.FromMinutes(1))
            text += $"\nDüzenlendi: {updated.ToString(updated.Date == note.CreatedAt.Date ? "HH:mm" : "d MMMM HH:mm", Tr)}";
        if (note.Done)
            text += "\n✓ Tamamlandı (Ctrl+D geri al)";
        return text;
    }

    internal readonly record struct GutterEntry(int Line, double Top, double Height, bool Current, bool Done);

    /// <summary>Satır numarası şeridi: numaraları metin kutusundaki gerçek satır konumlarına çizer
    /// (satır kaydırma açıkken uzun bir not birden çok görsel satıra yayılsa da numara ilk satırında durur).</summary>
    private sealed class LineGutter : FrameworkElement
    {
        private readonly DayLineEditor _owner;
        private IReadOnlyList<GutterEntry> _entries = [];
        private int _lineCount = 1;

        public static readonly DependencyProperty BackgroundProperty =
            Panel.BackgroundProperty.AddOwner(typeof(LineGutter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush? Background
        {
            get => (Brush?)GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        public LineGutter(DayLineEditor owner)
        {
            _owner = owner;
            Cursor = Cursors.Arrow;
        }

        public void SetEntries(IReadOnlyList<GutterEntry> entries, int lineCount)
        {
            var widthChanged = lineCount.ToString(Tr).Length != _lineCount.ToString(Tr).Length;
            _entries = entries;
            _lineCount = lineCount;
            if (widthChanged) InvalidateMeasure();
            InvalidateVisual();
        }

        public int LineAt(double y)
        {
            foreach (var e in _entries)
            {
                if (y >= e.Top && y < e.Top + e.Height)
                    return e.Line;
            }
            return -1;
        }

        private Typeface Face => new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private double DigitWidth()
        {
            var ft = new FormattedText("0", Tr, FlowDirection.LeftToRight, Face, _owner.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return ft.WidthIncludingTrailingWhitespace;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var digits = Math.Max(2, _lineCount.ToString(Tr).Length);
            // numara + "✓" için bir hane + iki yanda boşluk
            return new Size(DigitWidth() * (digits + 2) + 16, 0);
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(RenderSize));
            var muted = (Brush)_owner.FindResource("Muted");
            var fg = (Brush)_owner.FindResource("Fg");
            var success = (Brush)_owner.FindResource("Success");
            var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var digit = DigitWidth();
            var numberRight = ActualWidth - 8 - digit * 1.5;

            foreach (var e in _entries)
            {
                var number = new FormattedText((e.Line + 1).ToString(Tr), Tr, FlowDirection.LeftToRight, Face, _owner.FontSize, e.Current ? fg : muted, dip);
                dc.DrawText(number, new Point(numberRight - number.Width, e.Top));
                if (e.Done)
                {
                    var check = new FormattedText("✓", Tr, FlowDirection.LeftToRight, Face, _owner.FontSize * 0.9, success, dip);
                    dc.DrawText(check, new Point(numberRight + digit * 0.4, e.Top));
                }
            }
        }
    }
}
