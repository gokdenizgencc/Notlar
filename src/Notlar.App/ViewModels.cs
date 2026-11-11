using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Notlar.Core;

namespace Notlar.App;

public sealed record DayListItem(DateOnly Date, string Header, int Count);

public sealed record TagChip(string? Tag, string Label, bool IsSelected);

public sealed record AuthorChip(string? Author, string Label, bool IsSelected, Brush Color);

public sealed class NoteItem(Note note, Brush authorColor, Brush authorTint) : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _editText = "";

    public Note Note { get; } = note;
    public long Id => Note.Id;
    public bool Done => Note.Done;
    public string Time => Note.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string DisplayText => Note.DisplayText;
    public IReadOnlyList<string> Tags => Note.Tags;
    public string Author => Note.Author;
    public string AuthorLabel => Note.Author;
    public Brush AuthorColor { get; } = authorColor;
    public Brush AuthorTint { get; } = authorTint;

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnChanged();
            OnChanged(nameof(IsViewing));
        }
    }

    public bool IsViewing => !_isEditing;

    public string EditText
    {
        get => _editText;
        set { _editText = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Hafıza listesinde bir kategori başlığı altındaki kartlar.</summary>
public sealed record MemoryGroupVm(string Header, IReadOnlyList<MemoryItemVm> Items);

/// <summary>Bir hafıza kartının görünüm modeli (başlık/içerik satır içi düzenleme desteğiyle).</summary>
public sealed class MemoryItemVm(MemoryItem item) : INotifyPropertyChanged
{
    private static readonly CultureInfo TrTr = CultureInfo.GetCultureInfo("tr-TR");
    private bool _isEditing;
    private string _editTitle = "";
    private string _editContent = "";

    public MemoryItem Item { get; } = item;
    public long Id => Item.Id;
    public string Title => Item.Title;
    public string Content => Item.Content;
    public string Category => Item.Category;
    public string UpdatedLabel => $"güncellendi: {Item.UpdatedAt.ToString("d MMMM HH:mm", TrTr)}";

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnChanged();
            OnChanged(nameof(IsViewing));
        }
    }

    public bool IsViewing => !_isEditing;

    public string EditTitle
    {
        get => _editTitle;
        set { _editTitle = value; OnChanged(); }
    }

    public string EditContent
    {
        get => _editContent;
        set { _editContent = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Yetenekler sekmesinde bir AI başlığı altındaki yetenek satırları.</summary>
public sealed record SkillGroupVm(string Ai, Brush AiColor, string SummaryLabel, IReadOnlyList<SkillItemVm> Items);

/// <summary>Yetenekler sekmesinde tek bir yetenek satırının görünüm modeli. Description panelde
/// gösterilecek metindir (Türkçe çeviri varsa o, yoksa orijinal); OriginalDescription her zaman
/// orijinal (İngilizce) metindir ve ToolTip'te gösterilir.</summary>
public sealed record SkillItemVm(
    string EntryId, string Ai, string Name, string Description, string OriginalDescription, string SourceLabel,
    string UsageLabel, string LastUsedLabel, bool IsUnused, bool CanDelete, long? RegistryId);

/// <summary>"Keşfet" sekmesinde bir marketplace başlığı altındaki plugin'ler.</summary>
public sealed record MarketplaceGroupVm(string Marketplace, IReadOnlyList<MarketplacePluginVm> Items);

/// <summary>"Keşfet" sekmesinde tek bir marketplace plugin'inin görünüm modeli: kurulu/kurulu değil,
/// kurulum sırasında "Kuruluyor…" durumu.</summary>
public sealed class MarketplacePluginVm(string marketplace, string name, string description, IReadOnlyList<string> skills) : INotifyPropertyChanged
{
    private bool _installed;
    private bool _busy;

    public string Marketplace { get; } = marketplace;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public IReadOnlyList<string> Skills { get; } = skills;
    public string PluginId => $"{Name}@{Marketplace}";

    public bool Installed
    {
        get => _installed;
        set { if (_installed == value) return; _installed = value; OnChanged(); OnChanged(nameof(ActionLabel)); }
    }

    public bool Busy
    {
        get => _busy;
        set { if (_busy == value) return; _busy = value; OnChanged(); OnChanged(nameof(ActionLabel)); }
    }

    public string ActionLabel => Busy ? (Installed ? "Kaldırılıyor…" : "Kuruluyor…") : (Installed ? "Kaldır" : "Kur");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Gizli kişisel görünümü için bir kayıt satırının görünüm modeli.</summary>
public sealed class TaskItemVm(TaskItem task, Brush authorColor, Brush authorTint) : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _editText = "";

    public TaskItem Task { get; } = task;
    public long Id => Task.Id;
    public bool Done => Task.Done;
    public string Title => Task.Title;
    public string Author => Task.Author;
    public Brush AuthorColor { get; } = authorColor;
    public Brush AuthorTint { get; } = authorTint;

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnChanged();
            OnChanged(nameof(IsViewing));
        }
    }

    public bool IsViewing => !_isEditing;

    public string EditText
    {
        get => _editText;
        set { _editText = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
