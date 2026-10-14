using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quotra.WinUI.Interop;

/// <summary>Read only Claude Desktop's sessionKey cookie, never a general browser profile.</summary>
internal static class ClaudeDesktopSessionReader
{
    internal sealed record Result(string? Session, string? Error, string? OrganizationId = null);

    public static Result Read()
    {
        string? error = null;
        foreach (string root in ProfileRoots())
        {
            foreach (string profile in CookieProfiles(root))
            {
                foreach (string relative in new[] { "Network/Cookies", "Cookies" })
                {
                    string database = Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(database)) continue;
                    try
                    {
                        string state = File.Exists(Path.Combine(profile, "Local State")) ? profile : root;
                        string? session = ReadDatabase(database, Path.Combine(state, "Local State"));
                        if (!string.IsNullOrEmpty(session))
                        {
                            string? organization = null;
                            try { organization = ReadDatabase(database, Path.Combine(state, "Local State"), "lastActiveOrg"); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException or InvalidOperationException) { }
                            return new Result(session, null, organization);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                        or CryptographicException or FormatException or InvalidOperationException or DllNotFoundException)
                    {
                        error = ex is InvalidOperationException ? ex.Message
                            : "Claude Desktop의 보호된 로그인 정보를 읽을 수 없습니다. Claude Code에서 로그인한 뒤 새로고침하세요.";
                    }
                }
            }
        }
        return new Result(null, error);
    }

    internal static IEnumerable<string> ProfileRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        string[] directories;
        try { directories = Directory.Exists(packages) ? Directory.GetDirectories(packages, "*Claude*") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string package in directories)
        {
            yield return Path.Combine(package, "LocalCache", "Roaming", "Claude");
            yield return Path.Combine(package, "LocalCache", "Roaming", "AnthropicClaude");
            yield return Path.Combine(package, "RoamingState", "Claude");
        }
    }

    private static IEnumerable<string> CookieProfiles(string root)
    {
        yield return root;
        string partitions = Path.Combine(root, "Partitions");
        string[] directories;
        try { directories = Directory.Exists(partitions) ? Directory.GetDirectories(partitions) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string partition in directories) yield return partition;
    }

    internal static string? ReadDatabase(string path, string localState, string name = "sessionKey")
    {
        if (name is not ("sessionKey" or "lastActiveOrg")) throw new ArgumentException("Unsupported cookie name.");
        IntPtr database = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            // READONLY; SQLite reads the live WAL too. Neither desktop cookies nor auth are changed.
            if (Sqlite.Open(path, out database, 1, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Claude Desktop 로그인 저장소에 접근할 수 없습니다. Claude Code에서 로그인한 뒤 새로고침하세요.");
            Sqlite.BusyTimeout(database, 500);
            long expires = (DateTimeOffset.UtcNow - new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)).Ticks / 10;
            string sql = "SELECT host_key,value,encrypted_value FROM cookies "
                + $"WHERE name='{name}' AND host_key IN ('claude.ai','.claude.ai') "
                + $"AND (expires_utc=0 OR expires_utc>{expires}) ORDER BY last_access_utc DESC LIMIT 1";
            if (Sqlite.Prepare(database, sql, -1, out statement, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Claude Desktop 로그인 저장소 형식을 읽을 수 없습니다. Claude Code에서 로그인한 뒤 새로고침하세요.");
            int step = Sqlite.Step(statement);
            if (step == 101) return null; // DONE: no live session cookie.
            if (step != 100)
                throw new InvalidOperationException("Claude Desktop 로그인 저장소가 잠겨 있습니다. Claude Desktop을 종료한 뒤 새로고침하세요.");
            string host = Marshal.PtrToStringUTF8(Sqlite.ColumnText(statement, 0)) ?? "";
            string plaintext = Marshal.PtrToStringUTF8(Sqlite.ColumnText(statement, 1)) ?? "";
            if (!string.IsNullOrEmpty(plaintext)) return ValidateSession(plaintext);
            int size = Sqlite.ColumnBytes(statement, 2);
            if (size <= 0 || size > 16 * 1024) return null;
            byte[] encrypted = new byte[size];
            Marshal.Copy(Sqlite.ColumnBlob(statement, 2), encrypted, 0, size);
            return DecryptCookie(encrypted, localState, host);
        }
        finally
        {
            if (statement != IntPtr.Zero) Sqlite.Finalize(statement);
            if (database != IntPtr.Zero) Sqlite.Close(database);
        }
    }

    internal static string DecryptCookie(byte[] encrypted, string localState, string host)
    {
        byte[] clear;
        if (encrypted.AsSpan().StartsWith("v20"u8))
            throw new InvalidOperationException("Claude Desktop의 앱 전용 암호화로 자동 연결할 수 없습니다. Claude Code에서 로그인한 뒤 새로고침하세요.");
        if (encrypted.AsSpan().StartsWith("v10"u8) || encrypted.AsSpan().StartsWith("v11"u8))
        {
            if (encrypted.Length < 31) throw new CryptographicException();
            using JsonDocument state = JsonDocument.Parse(File.ReadAllText(localState));
            if (!state.RootElement.TryGetProperty("os_crypt", out JsonElement crypt)
                || !crypt.TryGetProperty("encrypted_key", out JsonElement keyElement)
                || keyElement.ValueKind != JsonValueKind.String)
                throw new CryptographicException();
            byte[] protectedKey = Convert.FromBase64String(keyElement.GetString()!);
            if (!protectedKey.AsSpan().StartsWith("DPAPI"u8)) throw new CryptographicException();
            byte[] key = Unprotect(protectedKey[5..]);
            try
            {
                clear = new byte[encrypted.Length - 31];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, encrypted.Length - 31), encrypted.AsSpan(encrypted.Length - 16), clear);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        else clear = Unprotect(encrypted);
        try
        {
            // Chromium's newer cookie schema prepends the SHA256 of host_key.
            byte[] hostHash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
            int prefix = clear.Length >= 32 && CryptographicOperations.FixedTimeEquals(clear.AsSpan(0, 32), hostHash) ? 32 : 0;
            return ValidateSession(Encoding.UTF8.GetString(clear, prefix, clear.Length - prefix));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal static string ValidateSession(string session)
    {
        if (session.Length is < 8 or > 8192 || session.Any(c => c <= ' ' || c >= 127 || c is ';' or ','))
            throw new InvalidOperationException("Claude Desktop 로그인 정보가 올바르지 않습니다. Claude Desktop에서 다시 로그인한 뒤 새로고침하세요.");
        return session;
    }

    internal static byte[] Unprotect(byte[] data)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output))
                throw new CryptographicException("Windows 로그인 보호 정보를 읽을 수 없습니다.");
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    // Windows 11 includes this SQLite runtime; no extra native dependency in the portable build.
    private static class Sqlite
    {
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Prepare(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)] internal static extern int BusyTimeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)] internal static extern int Step(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnText(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ColumnBlob(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)] internal static extern int ColumnBytes(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)] internal static extern int Finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)] internal static extern int Close(IntPtr db);
    }
}
