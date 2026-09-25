using System.IO;
using DownloadManagerApplet.Models;
using DownloadManagerApplet.Services;
using DownloadManagerApplet.ViewModels;
using DotNetTestKit;

namespace DownloadManagerApplet.Tests;

/// <summary>Settings &gt; General (categories, rules, save feedback) and the add bar's "Save to" choice.</summary>
public class DestinationSettingsTests : TestBase
{
    [Fact]
    public void SaveSettings_ReportsSuccess()
    {
        var vm = NewViewModel(out _);

        vm.SaveSettingsCommand.Execute(null);

        Assert.Equal("\u2713 Settings saved", vm.SettingsSaveMessage);
        Assert.False(vm.SettingsSaveFailed);
    }

    [Fact]
    public void SaveSettings_ReportsWriteFailure()
    {
        var state = TestStates.InFolder(Temp.Path);
        var vm = new MainViewModel(state, new FailingStore(), NewOrchestrator(), NullLoggingService.Instance);

        vm.SaveSettingsCommand.Execute(null);

        Assert.True(vm.SettingsSaveFailed);
        Assert.Contains("Could not save", vm.SettingsSaveMessage);
    }

    [Fact]
    public void SaveSettings_RejectsInvalidFolderWithoutSaving()
    {
        var vm = NewViewModel(out var state);
        vm.Destinations.Categories.Single(c => c.Label == "Images").Folder = "pictures";

        vm.SaveSettingsCommand.Execute(null);

        Assert.True(vm.SettingsSaveFailed);
        Assert.Contains("Images folder", vm.SettingsSaveMessage);
        Assert.False(File.Exists(Path.Combine(Temp.Path, "state.json")));
        Assert.Equal("pictures", state.Settings.Categories.Single(c => c.Category == FileCategory.Images).Folder);
    }

    [Fact]
    public void AddDownload_Automatic_UsesCategoryFolder()
    {
        var vm = NewViewModel(out var state);

        vm.NewDownloadUrl = "https://example.com/report.pdf";
        vm.AddDownloadCommand.Execute(null);

        var item = state.Downloads.Single();
        Assert.Equal(Path.Combine(Temp.Path, "Documents"), item.DestinationFolder);
        Assert.True(item.AutoFolder);
        Assert.Equal(FileCategory.Documents, item.Category);
    }

    [Fact]
    public void AddDownload_ChosenCategory_OverridesFileType()
    {
        var vm = NewViewModel(out var state);
        vm.SelectedSaveTarget = vm.SaveTargets.Single(t => t.Category == FileCategory.Images);

        vm.NewDownloadUrl = "https://example.com/report.pdf";
        vm.AddDownloadCommand.Execute(null);

        var item = state.Downloads.Single();
        Assert.Equal(Path.Combine(Temp.Path, "Images"), item.DestinationFolder);
        Assert.False(item.AutoFolder);
    }

    [Fact]
    public void ChooseFolder_AddsCustomTarget_AndCancelRestoresPrevious()
    {
        var vm = NewViewModel(out var state);
        var custom = Path.Combine(Temp.Path, "Custom");
        var browse = vm.SaveTargets.Single(t => t.Kind == SaveTargetKind.Browse);

        vm.PickFolder = _ => null;
        vm.SelectedSaveTarget = browse;
        Assert.Equal(SaveTargetKind.Automatic, vm.SelectedSaveTarget.Kind);

        vm.PickFolder = _ => custom;
        vm.SelectedSaveTarget = browse;
        Assert.Equal(custom, vm.SelectedSaveTarget.Folder);
        Assert.Equal(browse, vm.SaveTargets[^1]);

        vm.NewDownloadUrl = "https://example.com/a.zip";
        vm.AddDownloadCommand.Execute(null);
        Assert.Equal(custom, state.Downloads.Single().DestinationFolder);
    }

    [Fact]
    public void RenameCommand_ShowsDialog_ErrorsKeepItOpen_SuccessPersists()
    {
        var vm = NewViewModel(out var state);
        vm.NewDownloadUrl = "https://example.com/report.pdf";
        vm.AddDownloadCommand.Execute(null);
        var card = vm.Queue.Single();
        var attempts = new List<string?>();
        vm.ShowRenameDialog = editor =>
        {
            editor.Name = "bad|name.pdf";
            var first = editor.TrySave();
            attempts.Add(editor.ErrorText);
            editor.Name = "final.pdf";
            return first || editor.TrySave();
        };

        card.RenameCommand.Execute(null);

        Assert.Contains("isn't allowed", attempts.Single());
        Assert.Equal("final.pdf", card.FileName);
        Assert.Contains("final.pdf", File.ReadAllText(Path.Combine(Temp.Path, "state.json")));
    }

    [Fact]
    public void Rules_AddEditMoveDelete()
    {
        var vm = NewViewModel(out var state);
        var destinations = vm.Destinations;
        var next = RuleEditorResult.Save;
        destinations.ShowRuleEditor = editor =>
        {
            if (!editor.IsExisting)
            {
                editor.Name = "Rule " + (state.Settings.DestinationRules.Count + 1);
                editor.Conditions[0].Value = "iso";
                editor.Folder = Path.Combine(Temp.Path, "ISOs");
            }

            return editor.TrySave() || next != RuleEditorResult.Save ? next : RuleEditorResult.Cancel;
        };

        destinations.AddRuleCommand.Execute(null);
        destinations.AddRuleCommand.Execute(null);
        Assert.Equal(["Rule 1", "Rule 2"], destinations.Rules.Select(r => r.Name));
        Assert.False(destinations.Rules[0].MoveUpCommand.CanExecute(null));

        destinations.Rules[1].MoveUpCommand.Execute(null);
        Assert.Equal(["Rule 2", "Rule 1"], state.Settings.DestinationRules.Select(r => r.Name));

        next = RuleEditorResult.Delete;
        destinations.Rules[0].EditCommand.Execute(null);
        Assert.Equal(["Rule 1"], destinations.Rules.Select(r => r.Name));
        Assert.True(File.Exists(Path.Combine(Temp.Path, "state.json")));
    }

    [Fact]
    public void RuleEditor_InvalidRuleIsNotSaved()
    {
        var editor = new RuleEditorViewModel(null, _ => null);

        Assert.False(editor.TrySave());
        Assert.NotNull(editor.ErrorText);
        Assert.Null(editor.Result);
    }

    [Fact]
    public void RuleEditor_OperatorFollowsField_AndSourceFieldsHidden()
    {
        var editor = new RuleEditorViewModel(null, _ => null);
        var row = editor.Conditions[0];

        row.Field = RuleField.Size;

        Assert.Equal(RuleOperator.GreaterThan, row.Operator);
        Assert.True(row.IsSize);
        Assert.DoesNotContain(editor.Fields, f => f.Value is RuleField.Source or RuleField.VideoSite);
    }

    [Fact]
    public void FileTypesEditor_RejectsDuplicateExtension()
    {
        var settings = TestStates.InFolder(Temp.Path).Settings;
        var editor = new FileTypesEditorViewModel(settings.Categories);
        editor.Rows.Single(r => r.Category == FileCategory.Documents).ExtensionsText += " mp4";

        Assert.False(editor.TryValidate());
        Assert.Contains(".mp4", editor.ErrorText);

        editor.Rows.Single(r => r.Category == FileCategory.Videos).ExtensionsText = "mkv";
        Assert.True(editor.TryValidate());
        editor.ApplyTo(settings.Categories);
        Assert.Equal(["mkv"], settings.Categories.Single(c => c.Category == FileCategory.Videos).Extensions);
    }

    [Fact]
    public void JsonStore_RoundTripsRules_AndUpgradesOldStateFile()
    {
        var path = Path.Combine(Temp.Path, "state.json");
        File.WriteAllText(path, """
            {"Settings":{"DefaultDownloadFolder":"C:\\Old","MaxConcurrentDownloads":3},
             "Downloads":[{"Url":"https://e.com/x.zip","FileName":"x.zip","DestinationFolder":"C:\\Old","Status":3}]}
            """);
        var store = new JsonAppStore(path, NullLoggingService.Instance);

        var state = store.Load();
        Assert.Equal(@"C:\Old", state.Settings.DefaultDownloadFolder);
        Assert.Equal(4, state.Settings.Categories.Count);
        Assert.True(state.Settings.SortByFileType);
        Assert.False(state.Downloads.Single().ResolveOnResponse);

        state.Settings.DestinationRules.Add(new DestinationRule { Name = "ISOs", Folder = @"D:\ISOs", Conditions = { new RuleCondition { Field = RuleField.Extension, Value = "iso" } } });
        Assert.True(store.Save(state));
        Assert.Equal("iso", store.Load().Settings.DestinationRules.Single().Conditions.Single().Value);
    }

    private MainViewModel NewViewModel(out AppState state)
    {
        state = TestStates.InFolder(Temp.Path);
        var store = new JsonAppStore(Path.Combine(Temp.Path, "state.json"), NullLoggingService.Instance);
        return new MainViewModel(state, store, NewOrchestrator(), NullLoggingService.Instance);
    }

    private static DownloadOrchestrator NewOrchestrator() =>
        new(new NeverCompletingEngine(), NullLoggingService.Instance, () => 2, () => 3);
}
