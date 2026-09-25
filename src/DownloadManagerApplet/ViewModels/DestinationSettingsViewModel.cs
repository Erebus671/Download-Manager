using System.Collections.ObjectModel;
using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;
using Microsoft.Win32;

namespace DownloadManagerApplet.ViewModels;

public enum RuleEditorResult
{
    Cancel,
    Save,
    Delete
}

/// <summary>Settings &gt; General: file-type folders and custom destination rules. Edits apply to the live settings.</summary>
public sealed class DestinationSettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _persist;

    public ObservableCollection<CategoryRowViewModel> Categories { get; } = new();
    public ObservableCollection<RuleRowViewModel> Rules { get; } = new();

    public RelayCommand EditFileTypesCommand { get; }
    public RelayCommand ResetCategoriesCommand { get; }
    public RelayCommand AddRuleCommand { get; }

    /// <summary>Set by the window: shows the rule editor and returns the button pressed.</summary>
    public Func<RuleEditorViewModel, RuleEditorResult>? ShowRuleEditor { get; set; }

    /// <summary>Set by the window: shows the file-types editor; true when saved.</summary>
    public Func<FileTypesEditorViewModel, bool>? ShowFileTypesEditor { get; set; }

    /// <summary>Set by the window: asks a yes/no question.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Folder picker; replaceable for tests. Takes the starting folder, returns the choice or null.</summary>
    public Func<string, string?> PickFolder { get; set; } = ShowFolderDialog;

    public bool SortByFileType
    {
        get => _settings.SortByFileType;
        set
        {
            if (_settings.SortByFileType != value)
            {
                _settings.SortByFileType = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasRules => Rules.Count > 0;

    public DestinationSettingsViewModel(AppSettings settings, Action persist)
    {
        _settings = settings;
        _persist = persist;

        EditFileTypesCommand = new RelayCommand(EditFileTypes);
        ResetCategoriesCommand = new RelayCommand(ResetCategories);
        AddRuleCommand = new RelayCommand(() => EditRule(null));

        RebuildCategories();
        RebuildRules();
    }

    /// <summary>First problem with the folder settings, or null when they are all usable.</summary>
    public string? Validate()
    {
        if (!DestinationResolver.IsUsableFolder(_settings.DefaultDownloadFolder))
        {
            return "Other files folder is not a valid path.";
        }

        if (_settings.SortByFileType)
        {
            foreach (var category in _settings.Categories)
            {
                if (!DestinationResolver.IsUsableFolder(category.Folder))
                {
                    return $"{DestinationResolver.DisplayName(category.Category)} folder is not a valid path.";
                }
            }
        }

        return null;
    }

    internal void EditRule(RuleRowViewModel? row)
    {
        if (ShowRuleEditor is null)
        {
            return;
        }

        var editor = new RuleEditorViewModel(row?.Model, PickFolder);
        var result = ShowRuleEditor(editor);
        var rules = _settings.DestinationRules;

        if (result == RuleEditorResult.Save && editor.Result is { } saved)
        {
            var index = row is null ? -1 : rules.IndexOf(row.Model);
            if (index >= 0)
            {
                rules[index] = saved;
            }
            else
            {
                rules.Add(saved);
            }
        }
        else if (result == RuleEditorResult.Delete && row is not null)
        {
            rules.Remove(row.Model);
        }
        else
        {
            return;
        }

        RebuildRules();
        _persist();
    }

    internal void MoveRule(RuleRowViewModel row, int delta)
    {
        var rules = _settings.DestinationRules;
        var index = rules.IndexOf(row.Model);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= rules.Count)
        {
            return;
        }

        rules.RemoveAt(index);
        rules.Insert(target, row.Model);
        RebuildRules();
        _persist();
    }

    private void EditFileTypes()
    {
        if (ShowFileTypesEditor is null)
        {
            return;
        }

        var editor = new FileTypesEditorViewModel(_settings.Categories);
        if (ShowFileTypesEditor(editor))
        {
            editor.ApplyTo(_settings.Categories);
            RebuildCategories();
            _persist();
        }
    }

    private void ResetCategories()
    {
        if (Confirm is not null && !Confirm("Reset the Videos, Images, Audio and Documents folders and file types to their defaults?"))
        {
            return;
        }

        _settings.Categories = DestinationResolver.CreateDefaultCategories();
        RebuildCategories();
        _persist();
    }

    private void RebuildCategories()
    {
        Categories.Clear();
        foreach (var category in _settings.Categories)
        {
            Categories.Add(new CategoryRowViewModel(
                DestinationResolver.DisplayName(category.Category),
                () => category.Folder,
                v => category.Folder = v,
                string.Join(' ', category.Extensions),
                PickFolder));
        }

        Categories.Add(new CategoryRowViewModel(
            DestinationResolver.DisplayName(FileCategory.Other),
            () => _settings.DefaultDownloadFolder,
            v => _settings.DefaultDownloadFolder = v,
            "Everything else",
            PickFolder));
    }

    private void RebuildRules()
    {
        Rules.Clear();
        var rules = _settings.DestinationRules;
        for (var i = 0; i < rules.Count; i++)
        {
            Rules.Add(new RuleRowViewModel(rules[i], this, canMoveUp: i > 0, canMoveDown: i < rules.Count - 1));
        }

        OnPropertyChanged(nameof(HasRules));
    }

    private static string? ShowFolderDialog(string initial)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public sealed class CategoryRowViewModel : ObservableObject
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;

    public string Label { get; }
    public string ExtensionsText { get; }
    public RelayCommand BrowseCommand { get; }

    public string Folder
    {
        get => _get();
        set
        {
            if (_get() != value)
            {
                _set(value);
                OnPropertyChanged();
            }
        }
    }

    public CategoryRowViewModel(string label, Func<string> get, Action<string> set, string extensionsText, Func<string, string?> pickFolder)
    {
        Label = label;
        _get = get;
        _set = set;
        ExtensionsText = extensionsText;
        BrowseCommand = new RelayCommand(() =>
        {
            if (pickFolder(Folder) is { } chosen)
            {
                Folder = chosen;
            }
        });
    }
}

public sealed class RuleRowViewModel
{
    public DestinationRule Model { get; }
    public string Name => Model.Name;
    public string Summary => DestinationResolver.Describe(Model);
    public string Folder => Model.Folder;

    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand EditCommand { get; }

    public RuleRowViewModel(DestinationRule model, DestinationSettingsViewModel owner, bool canMoveUp, bool canMoveDown)
    {
        Model = model;
        MoveUpCommand = new RelayCommand(() => owner.MoveRule(this, -1), () => canMoveUp);
        MoveDownCommand = new RelayCommand(() => owner.MoveRule(this, 1), () => canMoveDown);
        EditCommand = new RelayCommand(() => owner.EditRule(this));
    }
}
