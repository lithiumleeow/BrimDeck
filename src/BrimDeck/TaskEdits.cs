using System.Globalization;
namespace BrimDeck;
public sealed record EditRequest(string id, string expectedRevision, string occurrenceKey, string title, string calendarId, string dueKind, string? dueDate, string? dueAt);
public static class TaskEdits
{
    public static bool SameAccount(BridgeList a, BridgeList b) => a.id == b.id || (!string.IsNullOrEmpty(a.sourceId) && a.sourceId == b.sourceId);
    public static bool CanEdit(BridgeTodo item) => item.writable && !item.completed && !string.IsNullOrWhiteSpace(item.editRevision) && (!item.recurring || !string.IsNullOrWhiteSpace(item.occurrenceKey));
    public static bool CanSnooze(BridgeTodo item) => CanEdit(item) && !item.recurring &&
        !string.IsNullOrWhiteSpace(item.calendarId) && item.title.Trim().Length is >= 1 and <= 500;

    public static EditRequest? BuildSnooze(BridgeTodo item, DateTimeOffset due, DateTimeOffset now)
    {
        if (!CanSnooze(item) || due <= now) return null;
        return new(item.id, item.editRevision, item.occurrenceKey, item.title, item.calendarId,
            "time", null, due.ToString("o", CultureInfo.InvariantCulture));
    }

    public static EditRequest Build(BridgeTodo item, string title, string calendarId, string kind, string? date, DateTimeOffset? time)
    {
        title = title.Trim();
        if (!CanEdit(item)) throw new ArgumentException("此任务当前不可编辑，请刷新或更新 Mac 桥接。");
        if (title.Length is < 1 or > 500) throw new ArgumentException("标题应为 1–500 个字符。");
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("请选择可写清单。");
        if (item.recurring && kind != "keep") throw new ArgumentException("重复任务的时间请在苹果端修改，本版保留原有重复规则。");
        if (kind == "date" && !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ArgumentException("日期无效。");
        if (kind == "time" && !time.HasValue) throw new ArgumentException("请选择日期与时间。");
        if (kind is not "keep" and not "none" and not "date" and not "time") throw new ArgumentException("提醒方式无效。");
        return new(item.id, item.editRevision, item.occurrenceKey, title, calendarId, kind, kind == "date" ? date : null, kind == "time" ? time?.ToString("o") : null);
    }
}
