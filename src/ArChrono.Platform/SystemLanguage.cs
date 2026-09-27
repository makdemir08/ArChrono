using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ArChrono.Localization;

namespace ArChrono.Platform;

/// <summary>İşletim sisteminin tercih edilen arayüz dili.</summary>
public static class SystemLanguage
{
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;

    /// <remarks>
    /// Uygulama <see cref="CultureInfo.CurrentUICulture"/>'ı açılışta en-US'e sabitlediği için bu metot ondan önce çağrılmalıdır.
    /// macOS'ta .NET kültürü "Bölge" (tarih/sayı biçimi) ayarından gelir; arayüz dili ise ayrı bir tercih olduğundan
    /// CoreFoundation'ın tercih edilen diller listesinden okunur (ör. dil İngilizce, bölge Türkiye).
    /// </remarks>
    public static AppLanguage Detect()
    {
        if (OperatingSystem.IsMacOS() && TryReadMacPreferredLanguage() is { } preferred)
            return preferred.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Turkish : AppLanguage.English;
        return Loc.DetectSystemLanguage(CultureInfo.CurrentUICulture);
    }

    private static string? TryReadMacPreferredLanguage()
    {
        try
        {
            var languages = CFLocaleCopyPreferredLanguages();
            if (languages == IntPtr.Zero) return null;
            try
            {
                if (CFArrayGetCount(languages) == 0) return null;
                var first = CFArrayGetValueAtIndex(languages, 0);
                var buffer = new byte[64];
                if (!CFStringGetCString(first, buffer, buffer.Length, Utf8Encoding)) return null;
                var length = Array.IndexOf(buffer, (byte)0);
                return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
            }
            finally
            {
                CFRelease(languages);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport(CoreFoundationLibrary)] private static extern IntPtr CFLocaleCopyPreferredLanguages();
    [DllImport(CoreFoundationLibrary)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundationLibrary)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundationLibrary)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr text, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundationLibrary)] private static extern void CFRelease(IntPtr handle);
}
