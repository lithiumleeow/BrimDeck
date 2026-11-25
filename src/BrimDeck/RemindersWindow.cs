using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BrimDeck;

public partial class MainWindow
{
    private RemindersWindow? _remindersWindow;

    private void Reminders_Click(object sender, RoutedEventArgs e)
    {
        if (_remindersWindow is { IsVisible: true })
        {
            _remindersWindow.Activate();
            return;
        }

        _remindersWindow = new RemindersWindow();
        _remindersWindow.Show();
    }
}

internal sealed class RemindersWindow : Window
{
    private readonly ConnectionVault _vault = App.RemindersVault;
    private readonly PasswordBox _link = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ComboBox _lists = new() { DisplayMemberPath = nameof(BridgeList.title), Margin = new Thickness(0, 4, 0, 4) };
    private readonly ListBox _tasks = new() { Height = 250, DisplayMemberPath = nameof(BridgeTodo.title) };
    private readonly TextBox _title = new();
    private readonly TextBox _due = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _panel = new() { Margin = new Thickness(16) };
    private readonly CancellationTokenSource _lifetime = new();
    private StoredConnection? _connection;
    private BridgeSnapshot? _snapshot;
    private CreateRequest? _pendingCreate;

    internal RemindersWindow()
    {
        Title = "iCloud 提醒事项";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _panel;

        _panel.Children.Add(new TextBlock { Text = "粘贴 Mac 桥接配置链接" });
        _panel.Children.Add(_link);
        AddButton("连接并保存", ConnectAsync);
        AddButton("刷新", RefreshAsync);
        _panel.Children.Add(_lists);
        _panel.Children.Add(_tasks);
        AddButton("完成选中待办", CompleteAsync);
        _panel.Children.Add(new TextBlock { Text = "新待办标题" });
        _panel.Children.Add(_title);
        _panel.Children.Add(new TextBlock { Text = "到期时间（本地时间，yyyy-MM-dd HH:mm，可留空）" });
        _panel.Children.Add(_due);
        AddButton("新增到选中清单", CreateAsync);
        _panel.Children.Add(_status);
        _lists.SelectionChanged += (_, _) => RenderTasks();
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            _connection = _vault.Load();
            if (_connection is not null) await RefreshAsync();
        });
        Closed += (_, _) => _lifetime.Cancel();
    }

    private void AddButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 4, 0, 4) };
        button.Click += async (_, _) => await RunAsync(action);
        _panel.Children.Add(button);
    }

    private async Task RunAsync(Func<Task> action)
    {
        _panel.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _panel.IsEnabled = true; }
    }

    private async Task ConnectAsync()
    {
        var connection = BridgeConnection.Parse(_link.Password);
        using var reader = new BridgeReader(connection);
        var snapshot = await reader.ReadAsync(_lifetime.Token);
        var saved = new StoredConnection(connection.Address.AbsoluteUri, connection.Token);
        _vault.Save(saved);
        _connection = saved;
        _pendingCreate = null;
        _link.Clear();
        SetSnapshot(snapshot);
    }

    private BridgeReader OpenReader() => new(
        (_connection ?? throw new InvalidOperationException("请先连接 Mac 桥接。")).Connection);

    private async Task RefreshAsync()
    {
        using var reader = OpenReader();
        SetSnapshot(await reader.ReadAsync(_lifetime.Token));
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
        _tasks.ItemsSource = tasks;
        _status.Text = $"{tasks.Length} 条未完成";
    }

    private async Task CompleteAsync()
    {
        if (_tasks.SelectedItem is not BridgeTodo item)
            throw new InvalidOperationException("请选择一条待办。");
        using var reader = OpenReader();
        await reader.CompleteAsync(item, _lifetime.Token);
        _tasks.ItemsSource = ((IEnumerable<BridgeTodo>)_tasks.ItemsSource).Where(x => x != item).ToArray();
        await RefreshAsync();
    }

    private async Task CreateAsync()
    {
        if (_lists.SelectedItem is not BridgeList { writable: true } list)
            throw new InvalidOperationException("请选择一个可写的清单。");
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
        await reader.CreateAsync(request, _lifetime.Token);
        _pendingCreate = null;
        _title.Clear();
        _due.Clear();
        await RefreshAsync();
    }
}
