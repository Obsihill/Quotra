using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quotra.WinUI.Services;

/// <summary>테마 선택 모드.</summary>
public enum ThemeMode
{
    /// <summary>Windows 설정을 따른다.</summary>
    Auto,
    Light,
    Dark,
}

/// <summary>
/// 위젯 설정. %LOCALAPPDATA%\Quotra\winui-settings.json 에 저장한다.
/// </summary>
public sealed class WidgetSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Quotra",
        "winui-settings.json");

    /// <summary>Always On Top(항상 위).</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>위젯 고정. 고정이면 화면에 늘 떠 있고, 해제하면 상단 흔들기로만 내려온다.</summary>
    public bool Pinned { get; set; } = true;

    /// <summary>다크/라이트 테마 모드. 기본은 Windows 설정 추종.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.Auto;

    /// <summary>창 뒤에 Windows Desktop Acrylic 재질을 표시한다.</summary>
    public bool Acrylic { get; set; } = true;

    /// <summary>위젯 모서리 반경(px).</summary>
    public int CornerRadius { get; set; } = 18;

    /// <summary>저장된 위치(물리 픽셀). null 이면 커서가 있는 모니터 상단 중앙.</summary>
    public int? Left { get; set; }

    public int? Top { get; set; }

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                WidgetSettings? loaded = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write(ex, "설정 읽기 실패 — 기본값 사용");
        }

        return new WidgetSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write(ex, "설정 저장 실패");
        }
    }
}
