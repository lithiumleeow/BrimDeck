using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace BrimDeck;

public sealed record StoredConnection(string url, string token, CreateRequest? pending = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public BridgeConnection Connection => BridgeConnection.Parse(JsonSerializer.Serialize(new { url, token }));
}
public sealed class ConnectionVault
{
    readonly string path;
    public ConnectionVault(string? file = null) => path = file ?? Path.Combine(AppContext.BaseDirectory, "portable-profile", "connection.dpapi");
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "BrimDeck iCloud Reminders", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("本机加密配置无法读取或保存。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
    public StoredConnection? Load()
    {
        if (!File.Exists(path)) return null;
        var encrypted = File.ReadAllBytes(path);
        if (encrypted.Length > 65536) throw new IOException("本机配置文件异常。");
        var plain = Transform(encrypted, false);
        try { var saved = JsonSerializer.Deserialize<StoredConnection>(plain) ?? throw new IOException("本机配置为空。"); _ = saved.Connection; return saved; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(StoredConnection saved)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(saved);
        byte[] encrypted;
        try { encrypted = Transform(plain, true); } finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
