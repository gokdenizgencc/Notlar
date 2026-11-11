using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Notlar.Core;

namespace Notlar.App;

public sealed record DayGroupItem(string Header, IReadOnlyList<NoteItem> Notes);

public sealed record TagChip(string? Tag, string Label, bool IsSelected);

public sealed class NoteItem(Note note) : INotifyPropertyChanged
{
    private bool _isEditing;
    private string _editText = "";

    public Note Note { get; } = note;
    public long Id => Note.Id;
    public bool Done => Note.Done;
    public string Time => Note.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string DisplayText => Note.DisplayText;
    public IReadOnlyList<string> Tags => Note.Tags;

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
