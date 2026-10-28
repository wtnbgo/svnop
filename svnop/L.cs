using System.Runtime.InteropServices;

namespace SvnOp;

/// <summary>
/// 表示言語の切り替え (日本語 / 英語)。
/// 優先順: 環境変数 SVNOP_LANG (ja / en) → config.json の language (ja / en / auto) → Windows の表示言語。
/// InvariantGlobalization で CultureInfo が使えないので、表示言語は Win32 から取る。
/// </summary>
public static class L
{
    /// <summary>日本語表示か。agent はトレイから切り替えたときに書き換える</summary>
    public static bool Ja { get; set; } = Detect();

    /// <summary>日本語表示なら ja、それ以外は en を返す</summary>
    public static string T(string ja, string en) => Ja ? ja : en;

    [DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();

    public static bool Detect()
    {
        var env = Environment.GetEnvironmentVariable("SVNOP_LANG");
        if (!string.IsNullOrEmpty(env))
            return env.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        try
        {
            var lang = Config.Load().Language;
            if (lang is "ja" or "en")
                return lang == "ja";
        }
        catch (Exception e) when (e is SvnOpException or IOException) { }
        const int LANG_JAPANESE = 0x11;
        return (GetUserDefaultUILanguage() & 0x3ff) == LANG_JAPANESE;
    }
}
