using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SvnOp;

/// <summary>
/// TortoiseSVN の認証ダイアログ (CSimplePrompt)。
/// リソース ID には頼らず「TortoiseSVN の exe が出した #32770 で、パスワード欄と realm 表記の static がある」で判定する。
/// </summary>
public sealed partial class AuthDialog
{
    public IntPtr Hwnd { get; private init; }
    public uint Pid { get; private init; }
    public string Realm { get; private init; } = "";
    public IntPtr UserEdit { get; private init; }
    public IntPtr PassEdit { get; private init; }
    public IntPtr SaveCheck { get; private init; }
    public IntPtr OkButton { get; private init; }

    [GeneratedRegex(@"^<[a-z+]+://[^>]+> ")]
    private static partial Regex RealmPattern();

    /// <summary>TortoiseSVN の bin ディレクトリ (レジストリの Directory から)</summary>
    public static string TsvnBinDir { get; } = Path.Combine(
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\TortoiseSVN", "Directory", null) as string
            ?? @"C:\Program Files\TortoiseSVN\", "bin") + Path.DirectorySeparatorChar;

    /// <summary>偽のダイアログにパスワードを流し込まないよう、TortoiseSVN の bin にある exe のウインドウだけを対象にする</summary>
    public static bool IsTortoiseProcess(uint pid) =>
        Win32.ImagePath(pid) is { } path && path.StartsWith(TsvnBinDir, StringComparison.OrdinalIgnoreCase);

    public static AuthDialog? TryDetect(IntPtr hwnd)
    {
        if (Win32.ClassName(hwnd) != "#32770" || !Win32.IsWindowVisible(hwnd))
            return null;
        var pid = Win32.ProcessId(hwnd);
        if (!IsTortoiseProcess(pid))
            return null;

        IntPtr pass = 0, save = 0, ok = 0;
        var edits = new List<IntPtr>();
        string? realm = null;
        foreach (var c in Win32.Children(hwnd))
        {
            var cls = Win32.ClassName(c);
            var style = Win32.GetWindowLong(c, Win32.GWL_STYLE);
            if (cls == "Edit")
            {
                if ((style & Win32.ES_PASSWORD) != 0) pass = c;
                else edits.Add(c);
            }
            else if (cls == "Static" && realm == null)
            {
                var t = Win32.Text(c);
                if (RealmPattern().IsMatch(t)) realm = t;
            }
            else if (cls == "Button")
            {
                var type = style & Win32.BS_TYPEMASK;
                if (type is Win32.BS_CHECKBOX or Win32.BS_AUTOCHECKBOX) save = c;
                else if (Win32.GetDlgCtrlID(c) == Win32.IDOK) ok = c;
            }
        }
        if (pass == 0 || realm == null || edits.Count != 1 || ok == 0)
            return null;
        return new AuthDialog
        {
            Hwnd = hwnd, Pid = pid, Realm = realm, UserEdit = edits[0], PassEdit = pass, SaveCheck = save, OkButton = ok,
        };
    }

    public static List<AuthDialog> FindAll() =>
        Win32.TopLevelWindows().Select(TryDetect).OfType<AuthDialog>().ToList();

    /// <summary>ユーザ名とパスワードを入れて OK を押す。saveAuth=false なら「認証情報を保存」を外す</summary>
    public void Fill(string user, string password, bool saveAuth, bool submit)
    {
        Win32.SetText(UserEdit, user);
        Win32.SetText(PassEdit, password);
        if (SaveCheck != 0)
            Win32.SendMessage(SaveCheck, Win32.BM_SETCHECK, saveAuth ? 1 : 0, 0);
        if (submit)
            Win32.SendMessage(OkButton, Win32.BM_CLICK, 0, 0);
    }

    /// <summary>調査用: TortoiseSVN のダイアログの子コントロールを列挙 (パスワード欄の中身は出さない)</summary>
    public static IEnumerable<string> Dump()
    {
        foreach (var h in Win32.TopLevelWindows())
        {
            if (Win32.ClassName(h) != "#32770" || !Win32.IsWindowVisible(h))
                continue;
            var pid = Win32.ProcessId(h);
            if (!IsTortoiseProcess(pid))
                continue;
            yield return $"dialog 0x{h:X} pid={pid} title=\"{Win32.Text(h)}\" detected={TryDetect(h) != null}";
            foreach (var c in Win32.Children(h))
            {
                var cls = Win32.ClassName(c);
                var style = Win32.GetWindowLong(c, Win32.GWL_STYLE);
                var text = cls == "Edit" && (style & Win32.ES_PASSWORD) != 0 ? "(password)" : Win32.Text(c);
                yield return $"  {cls,-10} id={Win32.GetDlgCtrlID(c),-5} style=0x{style:X8} \"{text}\"";
            }
        }
    }
}
