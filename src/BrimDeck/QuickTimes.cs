namespace BrimDeck;
public sealed record QuickTime(string Id, string Label, DateTimeOffset? Due, bool Enabled);
public static class QuickTimes
{
    public static IReadOnlyList<QuickTime> Options(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var tomorrow = today.AddDays(1);
        QuickTime Make(string id, string text, DateTime local)
        {
            var due = Resolve(local, zone);
            return new(id, text, due, due.HasValue && due > now);
        }
        return [
            Make("today-noon", "今天中午", today.AddHours(12)),
            Make("today-work-end", "今天下班前", today.AddHours(16)),
            Make("today-evening", "今天晚上", today.AddHours(20)),
            Make("tomorrow-morning", "明天上午", tomorrow.AddHours(8).AddMinutes(30)),
            Make("tomorrow-noon", "明天中午", tomorrow.AddHours(12)),
            Make("tomorrow-work-end", "明天下班", tomorrow.AddHours(16)),
            Make("tomorrow-evening", "明天晚上", tomorrow.AddHours(20)),
            Make("friday", "这周五", monday.AddDays(4).AddHours(9)),
            Make("next-monday", "下周一", monday.AddDays(7).AddHours(9))
        ];
    }

    public static IReadOnlyList<QuickTime> SnoozeOptions(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var afternoon = Resolve(today.AddHours(16), zone);
        var tomorrow = Resolve(today.AddDays(1).AddHours(8).AddMinutes(30), zone);
        return [
            new("in-30", "30 分钟后", now.AddMinutes(30), true),
            new("today-afternoon", "今天下午 · 16:00", afternoon, afternoon > now),
            new("tomorrow-morning", "明天上午 · 08:30", tomorrow, tomorrow > now)
        ];
    }

    public static DateTimeOffset? Resolve(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local) ? null : new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
