using ArChrono.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace ArChrono.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Shell => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Shell is not { } shell) return;
        shell.FolderPicker = async title =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };
        shell.ClipboardWriter = async text =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        shell.AboutPresenter = () => AboutWindow.ShowSingle(this);
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => Shell?.CloseOverlay();

    /// <summary>Klavye kısayolları. macOS'ta ⌘, diğer platformlarda Ctrl.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Shell is not { } shell) return;
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var mods = e.KeyModifiers;
        var hasCommand = mods.HasFlag(command);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var repo = shell.Repository;
        var inTextBox = e.Source is TextBox;

        if (e.Key == Key.Escape && shell.Overlay is not null)
        {
            shell.CloseOverlay();
            e.Handled = true;
            return;
        }
        if (shell.Overlay is not null && !(hasCommand && e.Key == Key.K)) return;

        switch (e.Key)
        {
            case Key.K when hasCommand:
            case Key.P when hasCommand && shift:
                if (shell.Overlay is CommandPaletteViewModel) shell.CloseOverlay();
                else shell.OpenCommandPalette();
                break;
            case Key.OemComma when hasCommand:
                _ = shell.OpenSettings();
                break;
            case Key.Z when hasCommand && !inTextBox && repo is not null:
                if (shift) repo.RedoCommand.Execute(null);
                else repo.UndoCommand.Execute(null);
                break;
            case Key.D1 when hasCommand && repo is not null:
                repo.Section = RepositorySection.Workspace;
                break;
            case Key.D2 when hasCommand && repo is not null:
                repo.Section = RepositorySection.Recovery;
                break;
            case Key.D3 when hasCommand && repo is not null:
                repo.Section = RepositorySection.TimeMachine;
                break;
            case Key.R when hasCommand && repo is not null:
                repo.RefreshCommand.Execute(null);
                break;
            case Key.F when hasCommand && alt && repo is not null:
                repo.FetchCommand.Execute(null);
                break;
            case Key.L when hasCommand && alt && repo is not null:
                repo.PullCommand.Execute(null);
                break;
            case Key.P when hasCommand && alt && repo is not null:
                repo.PushCommand.Execute(null);
                break;
            case Key.B when hasCommand && repo is not null:
                repo.CreateBranchCommand.Execute(null);
                break;
            case Key.S when hasCommand && shift && repo is not null:
                repo.StashCommand.Execute(null);
                break;
            case Key.J when hasCommand && repo is not null:
                repo.OpenTerminalCommand.Execute(null);
                break;
            case Key.OemTilde when hasCommand && repo is not null:
                repo.ToggleConsoleCommand.Execute(null);
                break;
            case Key.O when hasCommand && shift:
                _ = shell.CloseRepository();
                break;
            case Key.E when hasCommand && repo?.Workspace.Details is CommitDetailsViewModel details:
                details.ShowExplanation();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
