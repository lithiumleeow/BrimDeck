using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrimDeck;

public partial class MainWindow
{
    internal void UpdateReminderDisplay(ReminderSync? sync, bool offline, bool showSummary)
    {
        if (sync is not null && RemindersHost.Child is RemindersPanel panel)
            panel.ApplySharedSnapshot(sync, offline);
        CompactReminders.Text = sync?.Summary(DateTimeOffset.Now) ?? "";
        bool show = showSummary && CompactReminders.Text.Length > 0;
        CompactReminders.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactRings.Opacity = show ? 0 : 1;
    }
}

internal sealed partial class RemindersPanel
{
    private readonly App _app;
    private FrameworkElement? _editor;
    private DateTimeOffset? _displayedSync;

    internal void ApplySharedSnapshot(ReminderSync sync, bool offline)
    {
        if (!IsEnabled || !sync.Matches(_connection)) return;
        if (_displayedSync != sync.SyncedAt && _editor is null)
        {
            _displayedSync = sync.SyncedAt;
            SetSnapshot(sync.Snapshot);
        }
        else if (offline && _editor is null) RenderTasks();
    }

    private void AttachTaskActions(DockPanel row, TextBlock title, BridgeTodo item)
    {
        bool edit = !_app.ReminderOffline && _snapshot?.SupportsUpdate == true && TaskEdits.CanEdit(item);
        bool delete = !_app.ReminderOffline && _snapshot?.SupportsDelete == true && TaskWrites.CanDelete(item);
        if (edit)
        {
            title.Cursor = Cursors.Hand;
            title.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenTaskEditor(row, item); };
        }
        if (!delete) return;
        var menu = new ContextMenu();
        menu.Opened += (_, _) => _menu = menu;
        var remove = new MenuItem { Header = "删除待办…" };
        remove.Click += async (_, _) =>
        {
            var message = item.recurring ? "删除这条重复待办及其后续重复？" : "删除这条待办？";
            if (MessageBox.Show(message, "删除待办", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await RunAsync(async () =>
            {
                using var reader = OpenReader();
                await reader.DeleteAsync(new DeleteRequest(item.id, item.deleteRevision, item.occurrenceKey, item.recurring), _lifetime);
                await RefreshAsync();
            });
        };
        menu.Items.Add(remove);
        row.ContextMenu = menu;
    }

    private void OpenTaskEditor(DockPanel row, BridgeTodo item)
    {
        bool wasOpen = _editor is not null && _tasks.Children.IndexOf(_editor) == _tasks.Children.IndexOf(row) + 1;
        if (_editor is not null) _tasks.Children.Remove(_editor);
        _editor = null;
        if (wasOpen) return;
        var editor = new StackPanel { Margin = new Thickness(28, 3, 4, 8) };
        _editor = editor;
        var title = new TextBox { Text = item.title, Margin = new Thickness(0, 0, 0, 4) };
        editor.Children.Add(title);
        var options = new WrapPanel();
        editor.Children.Add(options);
        string kind = "keep";
        DateTimeOffset? due = null;
        var selection = new TextBlock { Text = "保留原时间", Margin = new Thickness(4) };
        selection.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
        editor.Children.Add(selection);
        void SetTime(string choice, DateTimeOffset? value, string label)
        {
            kind = choice;
            due = value;
            selection.Text = label;
            title.Focus();
        }
        if (!item.recurring)
        {
            options.Children.Add(MakeButton("保留时间", () => { SetTime("keep", null, "保留原时间"); return Task.CompletedTask; }));
            options.Children.Add(MakeButton("清除时间", () => { SetTime("none", null, "清除到期时间"); return Task.CompletedTask; }));
            foreach (var option in QuickTimes.Options(DateTimeOffset.Now, TimeZoneInfo.Local).Where(option => option.Enabled))
                options.Children.Add(MakeButton(option.Label, () => { SetTime("time", option.Due, option.Label); return Task.CompletedTask; }));
        }
        async Task SaveAsync() => await RunAsync(async () =>
        {
            if (_app.ReminderOffline) throw new InvalidOperationException("离线时暂不能保存编辑。");
            using var reader = OpenReader();
            await reader.EditAsync(TaskEdits.Build(item, title.Text, item.calendarId, kind, null, due), _lifetime);
            _editor = null;
            await RefreshAsync();
        });
        options.Children.Add(MakeButton("保存", SaveAsync));
        options.Children.Add(MakeButton("取消", () =>
        {
            _tasks.Children.Remove(editor);
            _editor = null;
            if (_app.LastReminderSnapshot is { } latest) ApplySharedSnapshot(latest, _app.ReminderOffline);
            return Task.CompletedTask;
        }));
        title.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SaveAsync(); } };
        _tasks.Children.Insert(_tasks.Children.IndexOf(row) + 1, editor);
        title.Focus();
        title.SelectAll();
        editor.BringIntoView();
    }
}
