using System.IO;
using System.Text;

namespace Quotra.WinUI;

/// <summary>부팅 단계 표식(문제가 생겼을 때 어디까지 갔는지 남기기 위한 최소 장치).</summary>
internal static class Boot
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        // Claude's status-line command must read stdin and exit before boot logs or any windows.
        if (Environment.GetCommandLineArgs().Contains("--claude-statusline"))
        {
            Services.ClaudeStatusLineBridge.Collect(Console.In.ReadToEnd());
            Environment.Exit(0);
        }
        // 자동화가 stdout 을 읽을 때 한글이 콘솔 코드페이지(OEM)로 깨지지 않게 UTF-8 로 고정한다.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            // 콘솔이 없는 환경 — 로그 파일에는 어차피 UTF-8 로 남는다.
        }

        Log.Write($"boot: 관리 코드 진입 (pid={Environment.ProcessId})");
    }
}

/// <summary>
/// GUI 앱은 stdout 이 없으므로 파일에 로그를 남긴다.
///
/// 쓰기 경로는 스스로 고른다. %LOCALAPPDATA%\Quotra 가 권한 문제로 막히는 환경이 있어
/// (샌드박스/보안 정책) 실패하면 %TEMP% 로 내려간다. 로그를 못 쓴다고 앱이 멈추면 안 된다.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>실제로 쓰고 있는 로그 파일 경로.</summary>
    public static string FilePath => _path ??= Resolve();

    private static string Resolve()
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Quotra",
            "quotra-winui.log");

        if (TryPrepare(local, out string? reason))
        {
            return local;
        }

        string temp = Path.Combine(Path.GetTempPath(), "Quotra", "quotra-winui.log");
        if (TryPrepare(temp, out _))
        {
            return temp;
        }

        System.Diagnostics.Debug.WriteLine($"로그 경로 확보 실패: {reason}");
        return local;
    }

    /// <summary>폴더를 만들고 쓰기 가능한지 확인한다.</summary>
    private static bool TryPrepare(string path, out string? reason)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, string.Empty, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 로그를 못 지워도 앱은 계속 돈다.
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }
    }

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        // 검증 스크립트가 읽을 수 있도록 stdout 으로도 내보낸다(GUI 앱이라 리다이렉트했을 때만 보인다).
        Console.WriteLine(message);
        Console.Out.Flush();

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 여기서 다시 로그를 시도하면 무한 재귀가 된다(첫 실행에서 실제로 겪었다).
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }
    }

    /// <summary>예외는 스택까지 남긴다(현장에서 원인을 찾을 수 있게).</summary>
    public static void Write(Exception ex, string context) => Write($"{context}: {ex}");
}
