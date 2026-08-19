using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace CodeBoxX.Models;

public sealed class EditorDocument : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isDirty;
    private string _displayName;

    public EditorDocument(string? filePath = null, string? initialText = null, string? displayName = null)
    {
        FilePath = filePath;
        _displayName = string.IsNullOrWhiteSpace(filePath) ? (string.IsNullOrWhiteSpace(displayName) ? "Untitled" : displayName) : Path.GetFileName(filePath);
        _text = initialText ?? string.Empty;
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    public string? FilePath { get; private set; }
    public Encoding Encoding { get; set; }
    public string LanguageId => Services.LanguageService.GetLanguageId(FilePath ?? _displayName);
    public string FileNameHint => _displayName;
    public string DisplayName => IsDirty ? $"{_displayName} •" : _displayName;
    public string Tooltip => FilePath ?? $"Unsaved document: {_displayName}";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            IsDirty = true;
            OnPropertyChanged();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public void SetPath(string path)
    {
        FilePath = path;
        _displayName = Path.GetFileName(path);
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(LanguageId));
    }

    public void SetUnsavedName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        _displayName = displayName;
        OnPropertyChanged(nameof(FileNameHint));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(LanguageId));
    }

    public void MarkSaved()
    {
        IsDirty = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
