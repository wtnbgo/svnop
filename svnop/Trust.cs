using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SvnOp;

/// <summary>実行ファイルの Authenticode 署名の検証 (op.exe と TortoiseSVN の exe を確かめるため)</summary>
public static class Trust
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public int dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    const int WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
    const int WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;  // 検証のためにネットワークへ出ない

    /// <summary>結果のキャッシュ (パス + 更新時刻)</summary>
    static readonly ConcurrentDictionary<(string, DateTime), string?> Cache = new();

    /// <summary>署名が有効なら署名者の Subject、そうでなければ null</summary>
    public static string? SignerOf(string path)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return Cache.GetOrAdd((Path.GetFullPath(path).ToLowerInvariant(), stamp), _ => Verify(path));
    }

    static string? Verify(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(file.cbStruct);
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            if (WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data) != 0)
                return null;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
        try
        {
#pragma warning disable SYSLIB0057
            return X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>有効な署名があり、署名者の Subject にいずれかの語を含むか</summary>
    public static bool IsSignedBy(string path, params string[] subjectWords) =>
        SignerOf(path) is { } subject && subjectWords.Any(w => subject.Contains(w, StringComparison.OrdinalIgnoreCase));
}
