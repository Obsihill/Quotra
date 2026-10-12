using Microsoft.Win32;

namespace Quotra.WinUI.Services;

/// <summary>
/// Windows 의 앱 테마(라이트/다크)를 읽는다.
///
/// WinUI 는 <c>ElementTheme.Default</c> 로 두면 시스템 테마를 자동으로 따르지만,
/// "지금 시스템이 라이트인가"를 우리가 알아야 하는 순간이 있다(아이콘 색, 유리 대비, 로그).
/// 또 <see cref="ThemeMode"/> 로 사용자가 강제 지정할 수 있어야 해서 읽기 경로를 한 곳에 둔다.
/// </summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Windows 앱 테마가 라이트인가. 값을 못 읽으면 Windows 기본값(라이트)으로 본다.</summary>
    public static bool IsLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            object? value = key?.GetValue("AppsUseLightTheme");

            if (value is int light)
            {
                return light != 0;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Write(ex, "테마 레지스트리 읽기 실패 — 라이트로 가정");
        }

        return true;
    }

    /// <summary>모드와 시스템 값을 합쳐 실제로 적용할 테마를 정한다.</summary>
    public static bool ResolveIsLight(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => IsLight(),
    };
}
