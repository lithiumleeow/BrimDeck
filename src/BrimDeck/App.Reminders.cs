using System.Windows.Threading;

namespace BrimDeck;

public partial class App
{
    internal static ConnectionVault RemindersVault { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck", "reminders.dpapi"));

    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly HashSet<(string Id, string Occurrence, DateTimeOffset Due)> _notifiedReminders = new();
    private bool _pollingReminders;

    private void StartReminderPolling()
    {
        _reminderTimer.Tick += async (_, _) => await PollRemindersAsync();
        _reminderTimer.Start();
    }

    internal async Task PollRemindersAsync()
    {
        if (_pollingReminders || Exiting) return;
        _pollingReminders = true;
        try
        {
            var connection = RemindersVault.Load();
            if (connection is null) return;
            using var reader = new BridgeReader(connection.Connection);
            var tasks = await reader.ReadOpenTodosAsync(Lifetime.Token);
            if (RemindersVault.Load() != connection) return;
            var now = DateTimeOffset.Now;
            var due = tasks.Where(x => x.dueAt is not null)
                .Select(x => (Task: x, Valid: DateTimeOffset.TryParse(x.dueAt, out var time), Time: time))
                .Where(x => x.Valid && x.Time <= now && x.Time >= now.AddMinutes(-5))
                .Where(x => !_notifiedReminders.Contains((x.Task.id, x.Task.occurrenceKey, x.Time)))
                .ToArray();
            _notifiedReminders.RemoveWhere(x => x.Due < now.AddMinutes(-5));
            if (due.Length > 0 && _tray?.ShowReminder(due.Length == 1 ? due[0].Task.title : $"{due.Length} 条待办已到期") == true)
                foreach (var item in due) _notifiedReminders.Add((item.Task.id, item.Task.occurrenceKey, item.Time));
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException
            or OperationCanceledException or InvalidOperationException or ArgumentException
            or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException) { }
        finally { _pollingReminders = false; }
    }
}
