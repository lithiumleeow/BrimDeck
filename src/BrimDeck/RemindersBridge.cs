using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BrimDeck;

public sealed record BridgeConnection(Uri Address, string Token)
{
    public static BridgeConnection Parse(string raw)
    {
        raw = raw.Trim();
        if (raw.Length > 16384) throw new ArgumentException("配置链接过长。");
        if (raw.StartsWith("lireminders://connect?", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var parts = uri.Query.TrimStart('?').Split('&');
            var values = parts.Where(x => x.StartsWith("data=", StringComparison.Ordinal)).ToArray();
            if (values.Length != 1) throw new ArgumentException("配置链接缺少 data。");
            var encoded = Uri.UnescapeDataString(values[0][5..]).Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var url = root.GetProperty("url").GetString() ?? "";
        var token = root.GetProperty("token").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || !IsSupportedAddress(address))
            throw new ArgumentException("请输入内网 IP 或 HTTPS 域名作为桥接地址。");
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("连接令牌格式无效，请从 Mac 重新复制配置链接。");
        return new BridgeConnection(address, token);
    }

    public static bool IsSupportedAddress(Uri address)
    {
        if ((address.Scheme != "http" && address.Scheme != "https") ||
            address.UserInfo != "" || address.AbsolutePath != "/" ||
            address.Query != "" || address.Fragment != "") return false;
        if (!IPAddress.TryParse(address.Host, out var ip))
            return address.Scheme == Uri.UriSchemeHttps && address.HostNameType == UriHostNameType.Dns;
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 192 && b[1] == 168) ||
            (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
            (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }
}

public sealed class BridgeList
{
    public string sourceId { get; set; } = "";
    public bool writable { get; set; }
    public string id { get; set; } = "";
    public string title { get; set; } = "";
    public string source { get; set; } = "";
    public override string ToString() => string.IsNullOrEmpty(source) ? title : $"[{source}] {title}";
}
public sealed class BridgeTodo
{
    public int priority { get; set; }
    public string deleteRevision { get; set; } = "";
    public string editRevision { get; set; } = "";
    public string dueKind { get; set; } = "none";
    public string? dueDate { get; set; }
    public string? dueAt { get; set; }
    public bool writable { get; set; }
    public bool recurringWritable { get; set; }
    public string occurrenceKey { get; set; } = "";
    public string id { get; set; } = "";
    public string title { get; set; } = "";
    public string calendarId { get; set; } = "";
    public string listName { get; set; } = "";
    public string dueDisplay { get; set; } = "未设日期";
    public bool completed { get; set; }
    public bool recurring { get; set; }
}
public sealed class BridgeReply
{
    public bool supportsUpdate { get; set; }
    public bool supportsDelete { get; set; }
    public bool supportsPriority { get; set; }
    public bool ok { get; set; }
    public List<BridgeList>? lists { get; set; }
    public List<BridgeTodo>? reminders { get; set; }
}
public sealed record BridgeSnapshot(IReadOnlyList<BridgeList> Lists, IReadOnlyList<BridgeTodo> Todos, bool SupportsUpdate = false, bool SupportsDelete = false, bool SupportsPriority = false);

public sealed record DeleteRequest(string id, string expectedRevision, string occurrenceKey, bool deleteRecurring);
public sealed record PriorityRequest(string id, int priority, int expectedPriority, string occurrenceKey);

public sealed record CreateRequest(string operationId, string title, string calendarId, string dueKind, string? dueDate, string? dueAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? priority = null);
public static class TaskWrites
{
    public static bool CanDelete(BridgeTodo item) => item.writable && !item.completed &&
        !string.IsNullOrWhiteSpace(item.id) && !string.IsNullOrWhiteSpace(item.deleteRevision) &&
        (!item.recurring || !string.IsNullOrWhiteSpace(item.occurrenceKey));
    public static bool CanComplete(BridgeTodo item) => item.writable && !item.completed &&
        (!item.recurring || (item.recurringWritable && !string.IsNullOrEmpty(item.occurrenceKey)));
    public static CreateRequest Create(string title, string calendarId, DateTimeOffset? due, int priority = 0)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > 500) throw new ArgumentException("标题应为1～500个字符。");
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("请选择可写清单。");
        if (due.HasValue && due.Value <= DateTimeOffset.Now) throw new ArgumentException("请选择未来的提醒时间。");
        if (priority is not (0 or 1 or 5 or 9)) throw new ArgumentException("请选择有效的优先级。");
        return new CreateRequest(Guid.NewGuid().ToString(), title, calendarId, due.HasValue ? "time" : "none", null, due?.ToString("o"), priority == 0 ? null : priority);
    }
}

public sealed class BridgeReader : IDisposable
{
    readonly HttpClient client;
    public BridgeReader(BridgeConnection connection)
    {
        if (!BridgeConnection.IsSupportedAddress(connection.Address)) throw new ArgumentException("桥接地址无效。");
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = connection.Address, Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
    }
    public async Task<BridgeSnapshot> ReadAsync(CancellationToken cancellation)
    {
        var lists = await GetAsync("v1/lists", cancellation);
        if (lists.lists is null)
            throw new InvalidOperationException("Mac 返回的数据不完整。");
        var todos = await ReadOpenTodosAsync(cancellation);
        return new BridgeSnapshot(lists.lists, todos, lists.supportsUpdate, lists.supportsDelete, lists.supportsPriority);
    }
    internal async Task<IReadOnlyList<BridgeTodo>> ReadOpenTodosAsync(CancellationToken cancellation)
    {
        var reply = await GetAsync("v1/reminders", cancellation);
        return (reply.reminders ?? throw new InvalidOperationException("Mac 返回的待办不完整。"))
            .Where(item => !item.completed).ToArray();
    }

    public Task EditAsync(EditRequest request, CancellationToken cancellation) => PostAsync("v1/update", request, cancellation);
    public Task DeleteAsync(DeleteRequest request, CancellationToken cancellation) => PostAsync("v1/delete", request, cancellation);
    public Task CreateAsync(CreateRequest request, CancellationToken cancellation) => PostAsync("v1/reminders", request, cancellation);
    public Task CompleteAsync(BridgeTodo item, CancellationToken cancellation)
    {
        if (!TaskWrites.CanComplete(item)) throw new InvalidOperationException("此事项不可在 Windows 完成，请检查清单权限或 Mac 桥接版本。");
        return PostAsync("v1/complete", new { id = item.id, completed = true, occurrenceKey = item.occurrenceKey }, cancellation);
    }
    async Task PostAsync(string path, object body, CancellationToken cancellation)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content, cancellation);
        response.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<BridgeReply>(await response.Content.ReadAsStringAsync(cancellation));
        if (result?.ok != true) throw new InvalidOperationException("Mac 未确认写入结果。");
    }
    async Task<BridgeReply> GetAsync(string path, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("配置链接已失效，请从 Mac 重新复制。");
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("请在 Mac 上允许桥接访问提醒事项。");
        response.EnsureSuccessStatusCode();
        // Buffer at most 16 MiB; cancellation applies to both headers and body.
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int size = await stream.ReadAsync(buffer, cancellation);
            if (size == 0) break;
            if (memory.Length + size > 16 * 1024 * 1024) throw new InvalidOperationException("Mac 返回的数据过大。");
            memory.Write(buffer, 0, size);
        }
        var reply = JsonSerializer.Deserialize<BridgeReply>(memory.ToArray());
        if (reply?.ok != true) throw new InvalidOperationException("Mac 桥接未确认读取结果。");
        return reply;
    }
    public void Dispose() => client.Dispose();
}
