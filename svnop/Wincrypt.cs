using System.Runtime.InteropServices;
using System.Text;

namespace SvnOp;

/// <summary>
/// svn の windows-cryptoapi パスワード形式 (libsvn_subr/win32_crypto.c と同じ)。
/// 平文 (UTF-8, NUL なし) を CryptProtectData (説明 "auth_svn.simple.wincrypt"、エントロピーなし) し、
/// 改行なし base64 にしたもの。復号時は説明文字列の一致も確かめる。
/// ProtectedData は説明文字列を指定できないので crypt32 を直接呼ぶ。
/// </summary>
public static partial class Wincrypt
{
    public const string PassType = "wincrypt";
    const string Description = "auth_svn.simple.wincrypt";
    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
                                                 IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob pDataIn, out IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
                                                   IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    public static string Encrypt(string password)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        var handle = GCHandle.Alloc(plain, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = plain.Length, pbData = handle.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, Description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                  CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new SvnOpException(L.T("CryptProtectData に失敗しました: ", "CryptProtectData failed: ") + Marshal.GetLastPInvokeError());
            try
            {
                var bytes = new byte[output.cbData];
                Marshal.Copy(output.pbData, bytes, 0, bytes.Length);
                return Convert.ToBase64String(bytes);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Array.Clear(plain);
            handle.Free();
        }
    }

    /// <summary>復号できない (別ユーザ・別 PC で暗号化された、説明文字列が違う等) ときは null</summary>
    public static string? Decrypt(string base64)
    {
        byte[] crypted;
        try { crypted = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        var handle = GCHandle.Alloc(crypted, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = crypted.Length, pbData = handle.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref input, out var descr, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                return null;
            try
            {
                if (Marshal.PtrToStringUni(descr) != Description)
                    return null;
                var bytes = new byte[output.cbData];
                Marshal.Copy(output.pbData, bytes, 0, bytes.Length);
                var s = Encoding.UTF8.GetString(bytes);
                Array.Clear(bytes);
                return s;
            }
            finally
            {
                LocalFree(output.pbData);
                LocalFree(descr);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
