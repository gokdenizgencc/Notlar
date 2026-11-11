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
