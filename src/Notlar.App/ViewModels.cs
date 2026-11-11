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
/// kurulum sırasında "Kuruluyor…" durumu, popülerlik/güncellenme/bağlam yükü meta verisi ve (düz liste
/// görünümünde) marketplace rozeti.</summary>
public sealed class MarketplacePluginVm(
    string marketplace, string name, string description, IReadOnlyList<string> skills,
    string? metaLine, string? contextWeight, Brush? contextBrush, bool isTopPopular, bool showMarketplaceChip)
    : INotifyPropertyChanged
{
    private bool _installed;
    private bool _busy;

    public string Marketplace { get; } = marketplace;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public IReadOnlyList<string> Skills { get; } = skills;
    public string PluginId => $"{Name}@{Marketplace}";

    /// <summary>"1,6 bin kurulum · güncellendi: 3 Temmuz 2026" gibi, eksik parçalar atlanarak
    /// birleştirilmiş meta satırı; hiçbiri yoksa null.</summary>
    public string? MetaLine { get; } = string.IsNullOrEmpty(metaLine) ? null : metaLine;
    public bool HasMeta => MetaLine is not null;

    /// <summary>"hafif"/"orta"/"ağır"; veri yoksa null.</summary>
    public string? ContextWeight { get; } = contextWeight;
    public bool HasContext => ContextWeight is not null;
    public string ContextLabel => $"bağlam: {ContextWeight}";
    public Brush ContextBrush { get; } = contextBrush ?? Brushes.Transparent;

    /// <summary>Meta satırı ve bağlam rozeti aynı anda varsa aralarına "·" ayırıcı konur.</summary>
    public bool ShowContextSeparator => HasMeta && HasContext;

    /// <summary>Meta satırı (kurulum/güncellenme) ya da bağlam rozetinden en az biri varsa satır gösterilir.</summary>
    public bool HasMetaRow => HasMeta || HasContext;

    /// <summary>Kurulum sayısına göre ilk 10 içindeyse "🔥" ile vurgulanır.</summary>
    public bool IsTopPopular { get; } = isTopPopular;

    /// <summary>Düz liste görünümünde (Popüler/Yeni güncellenen) satırda küçük bir marketplace rozeti
    /// gösterilir; Ada göre gruplanmış görünümde marketplace zaten grup başlığında olduğundan gizlenir.</summary>
    public bool ShowMarketplaceChip { get; } = showMarketplaceChip;

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

/// <summary>Planlar sekmesinde liste görünümündeki bir plan kartının görünüm modeli. ProjectFull tam
/// metin (proje adı ya da klasör yolu ya da "(klasör yok)") ToolTip'te, ProjectShort kısaltılmış/adıyla
/// satırda gösterilir.</summary>
public sealed record PlanListItemVm(
    long Id, string Title, string ProjectFull, string ProjectShort, string StatusLabel, Brush StatusBrush, string ProgressLabel);

/// <summary>Projeler sekmesinde bir proje kartının görünüm modeli (ad/klasör satır içi düzenleme desteğiyle).</summary>
public sealed class ProjectItemVm(Project project) : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _editName = "";
    private string _editPath = "";

    public Project Project { get; } = project;
    public long Id => Project.Id;
    public string Name => Project.Name;
    public string Path => Project.Path;

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

    public string EditName
    {
        get => _editName;
        set { _editName = value; OnChanged(); }
    }

    public string EditPath
    {
        get => _editPath;
        set { _editPath = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Plan oluşturma/düzenleme ComboBox'ında bir proje seçeneği ("(Proje yok)" için Id null'dur).</summary>
public sealed record ProjectComboItem(long? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Plan detay görünümünde bir adımın görünüm modeli (satır içi düzenleme desteğiyle).</summary>
public sealed class PlanStepItemVm(PlanStep step) : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _editText = "";

    public PlanStep Step { get; } = step;
    public long Id => Step.Id;
    public int Order => Step.Order;
    public string Text => Step.Text;
    public string? Result => Step.Result;
    public bool HasResult => !string.IsNullOrWhiteSpace(Step.Result);

    public string StatusIcon => Step.Status switch
    {
        StepStatus.Bekliyor => "⏳",
        StepStatus.Yapiliyor => "🔄",
        StepStatus.Bitti => "✅",
        StepStatus.Hata => "❌",
        StepStatus.Atlandi => "⏭",
        _ => "⏳",
    };

    public string StatusLabel => Step.Status switch
    {
        StepStatus.Bekliyor => "bekliyor",
        StepStatus.Yapiliyor => "yapılıyor",
        StepStatus.Bitti => "bitti",
        StepStatus.Hata => "hata",
        StepStatus.Atlandi => "atlandı",
        _ => "",
    };

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
