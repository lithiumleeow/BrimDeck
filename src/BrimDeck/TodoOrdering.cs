using System.Globalization;

namespace BrimDeck;

internal enum TodoSort { Original, Due, Priority }

internal static class TodoOrdering
{
    public static bool CanSetPriority(BridgeTodo item) =>
        item.writable && !item.completed && !string.IsNullOrWhiteSpace(item.id) &&
        (!item.recurring || !string.IsNullOrWhiteSpace(item.occurrenceKey));

    public static int PriorityRank(int value) => value switch { 1 => 3, 5 => 2, 9 => 1, _ => 0 };
    public static string PriorityName(int value) => value switch { 1 => "高", 5 => "中", 9 => "低", _ => "无" };

    public static IEnumerable<BridgeTodo> Sort(IEnumerable<BridgeTodo> items, TodoSort sort) => sort switch
    {
        TodoSort.Due => items.OrderBy(DueKey),
        TodoSort.Priority => items.OrderByDescending(x => PriorityRank(x.priority)).ThenBy(DueKey),
        _ => items
    };

    public static IEnumerable<(string Group, BridgeTodo Item)> ByDate(
        IEnumerable<BridgeTodo> items, TodoSort sort, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var tomorrow = today.AddDays(1);
        var groups = Sort(items, sort)
            .GroupBy(item => DateGroup(item, today, zone))
            .OrderBy(group => group.Key.Day)
            .ThenBy(group => group.Key.Period);
        foreach (var group in groups)
        {
            var (day, period) = group.Key;
            string heading = day == DateTime.MinValue ? "已逾期"
                : day == DateTime.MaxValue ? "无日期"
                : day == today ? period switch { 0 => "今天上午", 1 => "今天 · 全天", _ => "今天下午" }
                : day == tomorrow ? "明天"
                : day.ToString(day.Year == today.Year ? "M月d日" : "yyyy年M月d日", CultureInfo.InvariantCulture);
            foreach (var item in group) yield return (heading, item);
        }
    }

    static (DateTime Day, int Period) DateGroup(BridgeTodo item, DateTime today, TimeZoneInfo zone)
    {
        DateTime local;
        if (TryTime(item, out var due)) local = TimeZoneInfo.ConvertTime(due, zone).DateTime;
        else if (!TryDay(item, out local)) return (DateTime.MaxValue, 0);
        if (local.Date < today) return (DateTime.MinValue, 0);
        if (local.Date != today) return (local.Date, 0);
        return (today, item.dueKind == "date" ? 1 : local.Hour < 12 ? 0 : 2);
    }

    internal static bool TryTime(BridgeTodo item, out DateTimeOffset due)
    {
        due = default;
        return item.dueKind == "time" &&
            DateTimeOffset.TryParse(item.dueAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out due);
    }

    internal static bool TryDay(BridgeTodo item, out DateTime day)
    {
        day = default;
        return item.dueKind == "date" &&
            DateTime.TryParseExact(item.dueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    static long DueKey(BridgeTodo item)
    {
        if (TryTime(item, out var due))
            return due.UtcTicks;
        if (TryDay(item, out var day))
            return day.Ticks - TimeZoneInfo.Local.GetUtcOffset(day).Ticks;
        return long.MaxValue;
    }
}
