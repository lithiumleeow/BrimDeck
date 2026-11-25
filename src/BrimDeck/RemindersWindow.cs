using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

public partial class MainWindow
{
    private bool RemindersMenuOpen => (RemindersHost.Child as RemindersPanel)?.MenuOpen == true;

    private void Reminders_Click(object sender, RoutedEventArgs e)
    {
        if (RemindersHost.Visibility == Visibility.Visible)
        {
            CloseRemindersPage();
            return;
        }

        if (RemindersHost.Child is null)
        {
            var panel = new RemindersPanel(_app);
            RemindersHost.Child = panel;
            PreviewMouseDown += (_, _) => panel.DismissComposerOnOutsideClick();
        }
        RemindersHost.Visibility = Visibility.Visible;
        PageContent(CurrentPage).Opacity = 0;
        PlacePageCapsule();
        Activate();
    }

    private void CloseRemindersPage()
    {
        if (RemindersHost.Visibility != Visibility.Visible) return;
        RemindersHost.Visibility = Visibility.Collapsed;
        PageContent(CurrentPage).Opacity = 1;
        PlacePageCapsule();
    }
}

internal sealed partial class RemindersPanel : Grid
{
    private readonly StackPanel _composer = new();
    private Button? _reveal;
    private ContextMenu? _menu;

    internal void DismissComposerOnOutsideClick()
    {
        if (!MenuOpen && !_composer.IsMouseOver && _reveal?.IsMouseOver != true)
            _composer.Visibility = Visibility.Collapsed;
    }
    internal bool MenuOpen => _menu?.IsOpen == true;
    private readonly ConnectionVault _vault = App.RemindersVault;
    private readonly PasswordBox _link = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _lists = new() { DisplayMemberPath = nameof(BridgeList.title), Margin = new Thickness(0, 4, 0, 4) };
    private readonly StackPanel _tasks = new();
    private readonly TextBox _title = new();
    private readonly TextBox _due = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _panel = new();
    private readonly CancellationToken _lifetime;
    private StoredConnection? _connection;
    private BridgeSnapshot? _snapshot;
    private CreateRequest? _pendingCreate;
    private string? _destination;

    internal RemindersPanel(App app)
    {
        _app = app;
        _lifetime = app.Lifetime.Token;
        Margin = new Thickness(14, 2, 14, 8);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        Children.Add(toolbar);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        toolbar.Children.Add(actions);
        var caption = new TextBlock { Text = "提醒事项", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
        toolbar.Children.Add(caption);

        var scroll = new ScrollViewer { Content = _tasks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 1);
        Children.Add(scroll);

        var footer = new StackPanel();
        var bottomBar = new Grid { MinHeight = 26 };
        footer.Children.Add(_panel);
        footer.Children.Add(bottomBar);
        SetRow(footer, 2);
        Children.Add(footer);
        bottomBar.Children.Add(_status);
        _status.FontSize = 11;
        _status.Margin = new Thickness(0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.HorizontalAlignment = HorizontalAlignment.Left;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");

        var composer = _composer;
        composer.IsVisibleChanged += (_, _) => { if (!IsVisible) composer.Visibility = Visibility.Collapsed; };
        var create = new DockPanel();
        composer.Children.Add(create);
        var settings = new DockPanel();
        _panel.Children.Add(composer);
        _panel.Children.Add(settings);
        composer.Visibility = settings.Visibility = Visibility.Collapsed;
        void Toggle(DockPanel panel)
        {
            var show = panel.Visibility != Visibility.Visible;
            composer.Visibility = settings.Visibility = Visibility.Collapsed;
            if (show) panel.Visibility = Visibility.Visible;
        }

        void OpenMenu(Button anchor, params Control[] entries)
        {
            var menu = new ContextMenu { PlacementTarget = anchor };
            foreach (var entry in entries) menu.Items.Add(entry);
            _menu = menu;
            anchor.ContextMenu = menu;
            menu.IsOpen = true;
        }

        MenuItem Command(string title, Action handler)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => handler();
            return item;
        }

        var more = MakeButton("⋯");
        more.ToolTip = "清单和设置";
        more.Click += (_, _) =>
        {
            var lists = new MenuItem { Header = "筛选清单", IsEnabled = _lists.Items.Count > 0 };
            foreach (BridgeList list in _lists.Items)
            {
                var item = Command(list.title, () => _lists.SelectedItem = list);
                item.IsCheckable = true;
                item.IsChecked = list == _lists.SelectedItem;
                lists.Items.Add(item);
            }
            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await RunAsync(RefreshAsync);
            OpenMenu(more, lists, refresh, Command(_app.ReminderSummaryEnabled ? "隐藏胶囊摘要" : "显示胶囊摘要", _app.ToggleReminderSummary), Command("连接设置", () => { Toggle(settings); if (settings.IsVisible) _link.Focus(); }));
        };
        actions.Children.Add(more);

        var save = MakeButton("保存连接", () => RunAsync(async () => { await ConnectAsync(); settings.Visibility = Visibility.Collapsed; }));
        DockPanel.SetDock(save, Dock.Right);
        settings.Children.Add(save);
        _link.ToolTip = "粘贴 Mac 桥接配置链接";
        settings.Children.Add(_link);

        async Task SubmitAsync() => await RunAsync(async () => { await CreateAsync(); composer.Visibility = Visibility.Collapsed; });
        var add = MakeButton("添加", SubmitAsync);
        DockPanel.SetDock(add, Dock.Right);
        create.Children.Add(add);
        _due.Width = 124;
        _due.ToolTip = "到期时间，可留空 · yyyy-MM-dd HH:mm";
        DockPanel.SetDock(_due, Dock.Right);
        create.Children.Add(_due);
        var presets = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        composer.Children.Add(presets);
        var reveal = MakeButton("＋", () =>
        {
            ToggleComposer();
            return Task.CompletedTask;
        });
        reveal.FontSize = 16;
        reveal.ToolTip = "新增待办";
        reveal.HorizontalAlignment = HorizontalAlignment.Center;
        _reveal = reveal;
        bottomBar.Children.Add(reveal);

        void ToggleComposer()
        {
            var show = composer.Visibility != Visibility.Visible;
            composer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            settings.Visibility = Visibility.Collapsed;
            if (!show) return;
            presets.Children.Clear();
            presets.Children.Add(MakeButton("不设时间", () => { _due.Clear(); _title.Focus(); return Task.CompletedTask; }));
            foreach (var option in QuickTimes.Options(DateTimeOffset.Now, TimeZoneInfo.Local))
            {
                var button = MakeButton($"{option.Label} {option.Due:HH:mm}", () =>
                {
                    _due.Text = option.Due?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
                    _title.Focus();
                    return Task.CompletedTask;
                });
                button.IsEnabled = option.Enabled;
                presets.Children.Add(button);
            }
            _title.Focus();
        }

        var destination = MakeButton("存入 ▾");
        DockPanel.SetDock(destination, Dock.Right);
        create.Children.Add(destination);
        destination.Click += (_, _) =>
        {
            var options = (_snapshot?.Lists ?? []).Where(list => list.writable)
                .Select(list => (Control)Command(list.title, () =>
                {
                    _destination = list.id;
                    destination.Content = list.title;
                })).ToArray();
            if (options.Length > 0) OpenMenu(destination, options);
        };
        _title.ToolTip = "待办标题";
        create.Children.Add(_title);
        foreach (var input in new[] { _title, _due })
        {
            input.PreviewKeyDown += async (_, args) =>
            {
                if (args.Key != System.Windows.Input.Key.Enter) return;
                args.Handled = true;
                if (IsEnabled) await SubmitAsync();
            };
        }
        foreach (Control field in new Control[] { _link, _title, _due })
        {
            field.Height = 24;
            field.Margin = new Thickness(2);
            field.SetResourceReference(Control.BackgroundProperty, "PanelSwitchFill");
            field.SetResourceReference(Control.ForegroundProperty, "PanelPrimary");
            field.SetResourceReference(Control.BorderBrushProperty, "PanelPopupBorder");
        }

        _lists.SelectionChanged += (_, _) => RenderTasks();
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = _app.PollRemindersAsync(); };
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            _connection = _vault.Load();
            if (_connection is not null)
            {
                if (_app.LastReminderSnapshot is { } cached && cached.Matches(_connection)) SetSnapshot(cached.Snapshot);
                await RefreshAsync();
            }
            else settings.Visibility = Visibility.Visible;
        });

    }

    private static Button MakeButton(string label, Func<Task>? action = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(2, 0, 2, 0) };
        button.SetResourceReference(StyleProperty, "PanelOutlineButton");
        if (action is not null) button.Click += async (_, _) => await action();
        return button;
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { IsEnabled = true; }
    }

    private async Task ConnectAsync()
    {
        var connection = BridgeConnection.Parse(_link.Password);
        using var reader = new BridgeReader(connection);
        var snapshot = await reader.ReadAsync(_lifetime);
        var saved = new StoredConnection(connection.Address.AbsoluteUri, connection.Token);
        _vault.Save(saved);
        _connection = saved;
        _pendingCreate = null;
        _link.Clear();
        _app.PublishReminderSnapshot(saved, snapshot);
        SetSnapshot(snapshot);
    }

    private BridgeReader OpenReader() => new(
        (_connection ?? throw new InvalidOperationException("请先连接 Mac 桥接。")).Connection);

    private async Task RefreshAsync()
    {
        using var reader = OpenReader();
        try
        {
            var snapshot = await reader.ReadAsync(_lifetime);
            _app.PublishReminderSnapshot(_connection!, snapshot);
            SetSnapshot(snapshot);
        }
        catch (Exception ex) when (App.IsReminderConnectionError(ex) && !_lifetime.IsCancellationRequested)
        {
            _app.MarkReminderOffline(_connection!);
            if (_app.LastReminderSnapshot is { } cached && cached.Matches(_connection)) SetSnapshot(cached.Snapshot);
            throw new InvalidOperationException(_snapshot is null ? "无法连接 Mac 桥接。" : _status.Text, ex);
        }
    }

    private void SetSnapshot(BridgeSnapshot snapshot)
    {
        var selected = _lists.SelectedItem as BridgeList;
        _snapshot = snapshot;
        _lists.ItemsSource = new[] { new BridgeList { title = "全部清单" } }.Concat(snapshot.Lists).ToArray();
        _lists.SelectedItem = ((BridgeList[])_lists.ItemsSource).FirstOrDefault(x => x.id == selected?.id);
        if (_lists.SelectedIndex < 0) _lists.SelectedIndex = 0;
        RenderTasks();
    }

    private void RenderTasks()
    {
        if (_snapshot is null) return;
        string? id = (_lists.SelectedItem as BridgeList)?.id;
        var tasks = _snapshot.Todos.Where(x => string.IsNullOrEmpty(id) || x.calendarId == id).ToArray();
        _editor = null;
        _tasks.Children.Clear();
        string? previousGroup = null;
        foreach (var (group, item) in TodoOrdering.ByDate(tasks, TodoSort.Due, DateTimeOffset.Now, TimeZoneInfo.Local))
        {
            if (group != previousGroup)
            {
                var heading = new TextBlock { Text = group, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 0, 3) };
                heading.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
                _tasks.Children.Add(heading);
                previousGroup = group;
            }
            var row = new DockPanel { Margin = new Thickness(0, 1, 4, 1), LastChildFill = true };
            var complete = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            complete.Checked += async (_, _) =>
            {
                try { await RunAsync(() => CompleteAsync(item)); }
                finally { complete.IsChecked = false; }
            };
            complete.ToolTip = "标记完成";
            complete.IsEnabled = !_app.ReminderOffline && TaskWrites.CanComplete(item);
            DockPanel.SetDock(complete, Dock.Left);
            row.Children.Add(complete);
            if (!string.IsNullOrWhiteSpace(item.dueDisplay))
            {
                var due = new TextBlock { Text = item.dueDisplay, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
                due.SetResourceReference(TextBlock.ForegroundProperty, "PanelSecondary");
                DockPanel.SetDock(due, Dock.Right);
                row.Children.Add(due);
            }
            var title = new TextBlock { Text = item.title, ToolTip = item.title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "PanelPrimary");
            row.Children.Add(title);
            AttachTaskActions(row, title, item);
            _tasks.Children.Add(row);
        }
        var count = tasks.Length == 0 ? "没有未完成的待办" : $"{tasks.Length} 条未完成";
        _status.Text = _app.LastReminderSnapshot is { } sync ? $"{count} · {(_app.ReminderOffline ? "离线 · " : "")}上次同步 {sync.SyncedAt.ToLocalTime():MM-dd HH:mm}" : count;
    }

    private async Task CompleteAsync(BridgeTodo item)
    {
        using var reader = OpenReader();
        await reader.CompleteAsync(item, _lifetime);
        await RefreshAsync();
    }

    private async Task CreateAsync()
    {
        var list = _snapshot?.Lists.FirstOrDefault(item => item.writable && item.id == _destination)
            ?? _lists.SelectedItem as BridgeList;
        if (list?.writable != true) list = _snapshot?.Lists.FirstOrDefault(item => item.writable);
        if (list is null) throw new InvalidOperationException("没有可写清单，请先连接并刷新。");
        DateTimeOffset? due = null;
        if (!string.IsNullOrWhiteSpace(_due.Text))
        {
            if (!DateTimeOffset.TryParseExact(_due.Text.Trim(), "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value))
                throw new ArgumentException("请输入 yyyy-MM-dd HH:mm 格式的时间。");
            due = value;
        }

        var request = TaskWrites.Create(_title.Text, list.id, due);
        if (_pendingCreate is { } pending && pending with { operationId = request.operationId } == request)
            request = pending;
        _pendingCreate = request;
        using var reader = OpenReader();
        await reader.CreateAsync(request, _lifetime);
        _pendingCreate = null;
        _title.Clear();
        _due.Clear();
        await RefreshAsync();
    }
}
