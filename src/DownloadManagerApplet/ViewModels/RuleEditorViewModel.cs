using System.Collections.ObjectModel;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Mvvm;
using DownloadManagerApplet.Services;

namespace DownloadManagerApplet.ViewModels;

public sealed record ChoiceOption<T>(T Value, string Label);

/// <summary>Edits a copy of a rule; <see cref="Result"/> holds the saved rule after <see cref="TrySave"/> succeeds.</summary>
public sealed class RuleEditorViewModel : ObservableObject
{
    // Source and Video site conditions only become meaningful with browser integration (8) and video sites (9).
    private static readonly RuleField[] StandardFields = [RuleField.Extension, RuleField.FileName, RuleField.Site, RuleField.Size, RuleField.MimeType];

    private readonly Guid _id;
    private string _name;
    private bool _matchAll;
    private string _folder;
    private string? _errorText;

    public string Title { get; }
    public bool IsExisting { get; }
    public IReadOnlyList<ChoiceOption<RuleField>> Fields { get; }
    public ObservableCollection<ConditionRowViewModel> Conditions { get; } = new();
    public DestinationRule? Result { get; private set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool MatchAll
    {
        get => _matchAll;
        set
        {
            if (SetProperty(ref _matchAll, value))
            {
                OnPropertyChanged(nameof(MatchAny));
            }
        }
    }

    public bool MatchAny
    {
        get => !_matchAll;
        set => MatchAll = !value;
    }

    public string Folder
    {
        get => _folder;
        set => SetProperty(ref _folder, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public RelayCommand AddConditionCommand { get; }
    public RelayCommand BrowseCommand { get; }

    public RuleEditorViewModel(DestinationRule? rule, Func<string, string?> pickFolder)
    {
        IsExisting = rule is not null;
        Title = IsExisting ? "Edit rule" : "Add rule";
        _id = rule?.Id ?? Guid.NewGuid();
        _name = rule?.Name ?? string.Empty;
        _matchAll = rule?.MatchAll ?? true;
        _folder = rule?.Folder ?? string.Empty;

        var fields = StandardFields.ToList();
        foreach (var extra in rule?.Conditions.Select(c => c.Field).Where(f => !fields.Contains(f)).Distinct() ?? [])
        {
            fields.Add(extra);
        }

        Fields = fields.Select(f => new ChoiceOption<RuleField>(f, DestinationResolver.FieldLabel(f))).ToList();

        foreach (var condition in rule?.Conditions ?? [])
        {
            Conditions.Add(new ConditionRowViewModel(this, condition));
        }

        if (Conditions.Count == 0)
        {
            AddCondition();
        }

        AddConditionCommand = new RelayCommand(AddCondition);
        BrowseCommand = new RelayCommand(() =>
        {
            if (pickFolder(Folder) is { } chosen)
            {
                Folder = chosen;
            }
        });
    }

    public DestinationRule Build() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        MatchAll = MatchAll,
        Folder = Folder.Trim(),
        Conditions = Conditions.Select(c => c.Build()).ToList()
    };

    /// <summary>Validates; on success sets <see cref="Result"/> and returns true, otherwise shows the problems.</summary>
    public bool TrySave()
    {
        var rule = Build();
        var errors = DestinationResolver.Validate(rule);
        if (errors.Count > 0)
        {
            ErrorText = string.Join(Environment.NewLine, errors);
            return false;
        }

        ErrorText = null;
        Result = rule;
        return true;
    }

    internal void Remove(ConditionRowViewModel row) => Conditions.Remove(row);

    private void AddCondition() =>
        Conditions.Add(new ConditionRowViewModel(this, new RuleCondition { Field = RuleField.Extension, Operator = RuleOperator.Is }));
}

public sealed class ConditionRowViewModel : ObservableObject
{
    private readonly RuleEditorViewModel _owner;
    private RuleField _field;
    private RuleOperator _operator;
    private string _value;
    private SizeUnit _unit;
    private IReadOnlyList<ChoiceOption<RuleOperator>> _operators = [];

    public IReadOnlyList<ChoiceOption<RuleField>> Fields => _owner.Fields;
    public IReadOnlyList<SizeUnit> Units { get; } = Enum.GetValues<SizeUnit>();
    public RelayCommand RemoveCommand { get; }

    public RuleField Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value))
            {
                RefreshOperators();
                OnPropertyChanged(nameof(IsSize));
                OnPropertyChanged(nameof(ValueHint));
            }
        }
    }

    public IReadOnlyList<ChoiceOption<RuleOperator>> Operators
    {
        get => _operators;
        private set => SetProperty(ref _operators, value);
    }

    public RuleOperator Operator
    {
        get => _operator;
        set => SetProperty(ref _operator, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public SizeUnit Unit
    {
        get => _unit;
        set => SetProperty(ref _unit, value);
    }

    public bool IsSize => Field == RuleField.Size;

    public string ValueHint => Field switch
    {
        RuleField.Extension => "iso img",
        RuleField.FileName => "*.zip",
        RuleField.Site => "github.com",
        RuleField.Size => "1.5",
        RuleField.MimeType => "video/*",
        RuleField.Source => "Browser",
        RuleField.VideoSite => "YouTube",
        _ => string.Empty
    };

    public ConditionRowViewModel(RuleEditorViewModel owner, RuleCondition condition)
    {
        _owner = owner;
        _field = condition.Field;
        _operator = condition.Operator;
        _value = condition.Value;
        _unit = condition.Unit;
        RefreshOperators();
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
    }

    public RuleCondition Build() => new() { Field = Field, Operator = Operator, Value = Value.Trim(), Unit = Unit };

    private void RefreshOperators()
    {
        var allowed = DestinationResolver.OperatorsFor(_field);
        Operators = allowed.Select(o => new ChoiceOption<RuleOperator>(o, DestinationResolver.OperatorLabel(o))).ToList();
        if (!allowed.Contains(_operator))
        {
            Operator = allowed[0];
        }
    }
}

/// <summary>Edits the extension lists; an extension may belong to only one category.</summary>
public sealed class FileTypesEditorViewModel : ObservableObject
{
    private string? _errorText;

    public ObservableCollection<FileTypeRowViewModel> Rows { get; } = new();

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public RelayCommand ResetCommand { get; }

    public FileTypesEditorViewModel(IEnumerable<CategoryFolder> categories)
    {
        foreach (var category in categories)
        {
            Rows.Add(new FileTypeRowViewModel(category.Category, string.Join(' ', category.Extensions)));
        }

        ResetCommand = new RelayCommand(() =>
        {
            foreach (var row in Rows)
            {
                row.ExtensionsText = string.Join(' ', DestinationResolver.DefaultExtensionsFor(row.Category));
            }
        });
    }

    public bool TryValidate()
    {
        var owner = new Dictionary<string, FileCategory>();
        var errors = new List<string>();
        foreach (var row in Rows)
        {
            foreach (var ext in DestinationResolver.ParseExtensions(row.ExtensionsText))
            {
                if (owner.TryGetValue(ext, out var first))
                {
                    errors.Add($".{ext} is listed under both {DestinationResolver.DisplayName(first)} and {row.Label}.");
                }
                else
                {
                    owner[ext] = row.Category;
                }
            }
        }

        ErrorText = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Take(5));
        return errors.Count == 0;
    }

    public void ApplyTo(List<CategoryFolder> categories)
    {
        foreach (var row in Rows)
        {
            var target = categories.FirstOrDefault(c => c.Category == row.Category);
            if (target is not null)
            {
                target.Extensions = DestinationResolver.ParseExtensions(row.ExtensionsText);
            }
        }
    }
}

public sealed class FileTypeRowViewModel : ObservableObject
{
    private string _extensionsText;

    public FileCategory Category { get; }
    public string Label => DestinationResolver.DisplayName(Category);

    public string ExtensionsText
    {
        get => _extensionsText;
        set => SetProperty(ref _extensionsText, value);
    }

    public FileTypeRowViewModel(FileCategory category, string extensionsText)
    {
        Category = category;
        _extensionsText = extensionsText;
    }
}
