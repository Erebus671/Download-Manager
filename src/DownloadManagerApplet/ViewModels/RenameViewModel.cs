using DownloadManagerApplet.Mvvm;

namespace DownloadManagerApplet.ViewModels;

/// <summary>Rename dialog; <c>apply</c> returns null on success or a message to show.</summary>
public sealed class RenameViewModel : ObservableObject
{
    private readonly Func<string, string?> _apply;
    private string _name;
    private string? _errorText;

    public RenameViewModel(string currentName, Func<string, string?> apply)
    {
        _name = currentName;
        _apply = apply;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                ErrorText = null;
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public bool TrySave()
    {
        ErrorText = _apply(Name ?? string.Empty);
        return ErrorText is null;
    }
}
