using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BrimDeck;

internal sealed record ReminderSync(string Account, DateTimeOffset SyncedAt, BridgeSnapshot Snapshot)
{
    internal static string AccountFor(StoredConnection connection) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.url + "\n" + connection.token)));

    internal bool Matches(StoredConnection? connection) => connection is not null && Account == AccountFor(connection);

    internal string Summary(DateTimeOffset now)
    {
        var today = now.LocalDateTime.Date;
        var timed = Snapshot.Todos.Where(x => TodoOrdering.TryTime(x, out _))
            .Select(x => (Item: x, Due: DateTimeOffset.Parse(x.dueAt!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var next = timed.Where(x => x.Due > now && x.Due <= now.AddHours(1)).OrderBy(x => x.Due).FirstOrDefault();
        if (next.Item is not null) return $"{next.Due.LocalDateTime:HH:mm} {next.Item.title}";
        int overdue = timed.Count(x => x.Due < now) + Snapshot.Todos.Count(x => TodoOrdering.TryDay(x, out var day) && day < today);
        int todayCount = timed.Count(x => x.Due >= now && x.Due.LocalDateTime.Date == today)
            + Snapshot.Todos.Count(x => TodoOrdering.TryDay(x, out var day) && day == today);
        return $"逾期 {overdue} · 今天 {todayCount}";
    }
}

public partial class App
{
    private static readonly string ReminderFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
    internal static ConnectionVault RemindersVault { get; } = new(Path.Combine(ReminderFolder, "reminders.dpapi"));
    private static string CachePath => Path.Combine(ReminderFolder, "reminders-cache.dpapi");
    private static string SummaryPath => Path.Combine(ReminderFolder, "reminders-summary.enabled");
    internal ReminderSync? LastReminderSnapshot { get; private set; }
    internal bool ReminderOffline { get; private set; }
    internal bool ReminderSummaryEnabled { get; private set; } = File.Exists(SummaryPath);

    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly Dictionary<(string Account, string Id, string Occurrence, DateTimeOffset Due), DateTimeOffset?> _shownReminders = new();
    private bool _pollingReminders;
    private Window? _reminderNotice;

    private void StartReminderPolling()
    {
        try
        {
            var connection = RemindersVault.Load();
            if (connection is not null && File.Exists(CachePath) && new FileInfo(CachePath).Length <= 20 * 1024 * 1024)
            {
                var plain = ConnectionVault.Transform(File.ReadAllBytes(CachePath), false);
                try
                {
                    var saved = JsonSerializer.Deserialize<ReminderSync>(plain);
                    if (saved?.Matches(connection) == true) LastReminderSnapshot = saved;
                    ReminderOffline = true;
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        catch (Exception ex) when (IsReminderConnectionError(ex)) { }
        _reminderTimer.Tick += async (_, _) => await PollRemindersAsync();
        _reminderTimer.Start();
    }

    internal static bool IsReminderConnectionError(Exception ex) => ex is IOException or System.Net.Http.HttpRequestException
        or OperationCanceledException or InvalidOperationException or ArgumentException or CryptographicException or JsonException;

    internal void ToggleReminderSummary()
    {
        try
        {
            if (ReminderSummaryEnabled) File.Delete(SummaryPath);
            else
            {
                Directory.CreateDirectory(ReminderFolder);
                File.WriteAllText(SummaryPath, "");
            }
            ReminderSummaryEnabled = !ReminderSummaryEnabled;
            UpdateReminderDisplays();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("无法保存胶囊摘要设置。");
        }
    }

    internal void PublishReminderSnapshot(StoredConnection connection, BridgeSnapshot snapshot)
    {
        LastReminderSnapshot = new ReminderSync(ReminderSync.AccountFor(connection), DateTimeOffset.Now, snapshot);
        ReminderOffline = false;
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(LastReminderSnapshot);
            byte[] encrypted;
            try { encrypted = ConnectionVault.Transform(plain, true); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Directory.CreateDirectory(ReminderFolder);
            var temporary = CachePath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, CachePath, true);
        }
        catch (Exception ex) when (IsReminderConnectionError(ex)) { }
        UpdateReminderDisplays();
    }

    internal void MarkReminderOffline(StoredConnection connection)
    {
        if (LastReminderSnapshot?.Matches(connection) != true) LastReminderSnapshot = null;
        ReminderOffline = true;
        UpdateReminderDisplays();
    }

    private void UpdateReminderDisplays()
    {
        foreach (var deck in Decks)
            deck.UpdateReminderDisplay(LastReminderSnapshot, ReminderOffline, ReminderSummaryEnabled);
    }

    internal async Task PollRemindersAsync()
    {
        if (_pollingReminders || Exiting) return;
        _pollingReminders = true;
        StoredConnection? connection = null;
        try
        {
            connection = RemindersVault.Load();
            if (connection is null) return;
            var before = LastReminderSnapshot;
            using var reader = new BridgeReader(connection.Connection);
            var snapshot = await reader.ReadAsync(Lifetime.Token);
            if (Exiting || RemindersVault.Load() != connection || LastReminderSnapshot != before) return;
            PublishReminderSnapshot(connection, snapshot);
            ShowDueReminder(connection, snapshot.Todos);
        }
        catch (Exception ex) when (IsReminderConnectionError(ex))
        {
            if (connection is not null && !Exiting && RemindersVault.Load() == connection) MarkReminderOffline(connection);
        }
        finally { _pollingReminders = false; }
    }

    private void ShowDueReminder(StoredConnection connection, IReadOnlyList<BridgeTodo> tasks)
    {
        if (_reminderNotice is { IsVisible: true }) return;
        var now = DateTimeOffset.Now;
        var account = ReminderSync.AccountFor(connection);
        foreach (var task in tasks)
        {
            if (!TodoOrdering.TryTime(task, out var due) || due > now) continue;
            var key = (account, task.id, task.occurrenceKey, due);
            if (_shownReminders.TryGetValue(key, out var again) ? again is null || again > now : due < now.AddMinutes(-5)) continue;

            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(new TextBlock { Text = task.title, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 });
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
            content.Children.Add(status);
            var buttons = new WrapPanel();
            content.Children.Add(buttons);
            var notice = new Window
            {
                Title = "待办到期", Content = content, Width = 340, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true
            };
            var work = SystemParameters.WorkArea;
            notice.Left = Math.Max(work.Left, work.Right - notice.Width - 16);
            notice.Top = Math.Max(work.Top, work.Bottom - 240);
            var complete = new Button { Content = "完成", IsEnabled = TaskWrites.CanComplete(task), Margin = new Thickness(0, 0, 6, 0) };
            complete.Click += async (_, _) =>
            {
                buttons.IsEnabled = false;
                try
                {
                    if (RemindersVault.Load() != connection) throw new InvalidOperationException("桥接连接已更改。");
                    using var reader = new BridgeReader(connection.Connection);
                    await reader.CompleteAsync(task, Lifetime.Token);
                    notice.Close();
                    await PollRemindersAsync();
                }
                catch (Exception ex) when (IsReminderConnectionError(ex)) { status.Text = "同步未成功，请重试。"; }
                finally { buttons.IsEnabled = true; }
            };
            var snooze = new Button { Content = "10 分钟后提醒" };
            snooze.Click += (_, _) => { _shownReminders[key] = DateTimeOffset.Now.AddMinutes(10); notice.Close(); };
            buttons.Children.Add(complete);
            buttons.Children.Add(snooze);
            _shownReminders[key] = null;
            _reminderNotice = notice;
            notice.Closed += (_, _) => { _reminderNotice = null; };
            notice.Show();
            break;
        }
    }
}
