using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using ArChrono.Localization;

namespace ArChrono.Platform.Credentials;

/// <summary>Sırlar yalnızca OS credential store'da tutulur; düz metin fallback yoktur (ADR-0007).</summary>
public interface ICredentialStore
{
    bool IsAvailable { get; }

    string Description { get; }

    string? Read(string key);

    void Write(string key, string secret);

    bool Delete(string key);
}

public static class CredentialStoreFactory
{
    public static ICredentialStore Create() =>
        OperatingSystem.IsMacOS() ? new MacKeychainCredentialStore()
        : OperatingSystem.IsWindows() ? new WindowsCredentialStore()
        : new UnavailableCredentialStore();
}

public sealed class CredentialStoreException(string message) : Exception(message);

public sealed class UnavailableCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;
    public string Description => Loc.T("No secure credential store is available on this system; secrets are not saved.", "Bu sistemde güvenli bir kimlik bilgisi deposu yok; gizli bilgiler kaydedilmez.");
    public string? Read(string key) => null;
    public void Write(string key, string secret) => throw new CredentialStoreException(Description);
    public bool Delete(string key) => false;
}

/// <summary>macOS Keychain — Security.framework SecItem API'si (sır hiçbir sürece argüman olarak geçmez).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    private const string ServicePrefix = "dev.archrono.";
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    public bool IsAvailable => true;
    public string Description => Loc.T("macOS Keychain", "macOS Anahtar Zinciri");

    public string? Read(string key)
    {
        using var scope = new CfScope();
        var query = scope.Dictionary(
            (Sec.Class, Sec.ClassGenericPassword),
            (Sec.AttrService, scope.String(ServicePrefix + key)),
            (Sec.AttrAccount, scope.String(key)),
            (Sec.ReturnData, Sec.BooleanTrue),
            (Sec.MatchLimit, Sec.MatchLimitOne));

        var status = SecItemCopyMatching(query, out var data);
        if (status == ErrSecItemNotFound) return null;
        if (status != ErrSecSuccess) throw new CredentialStoreException(Loc.T($"Keychain read failed (OSStatus {status}).", $"Anahtar Zinciri okunamadı (OSStatus {status})."));
        scope.Track(data);

        var length = (int)CFDataGetLength(data);
        var bytes = new byte[length];
        Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    public void Write(string key, string secret)
    {
        using var scope = new CfScope();
        var service = scope.String(ServicePrefix + key);
        var account = scope.String(key);
        var data = scope.Data(Encoding.UTF8.GetBytes(secret));

        var query = scope.Dictionary((Sec.Class, Sec.ClassGenericPassword), (Sec.AttrService, service), (Sec.AttrAccount, account));
        var update = scope.Dictionary((Sec.ValueData, data));
        var status = SecItemUpdate(query, update);
        if (status == ErrSecItemNotFound)
        {
            var add = scope.Dictionary(
                (Sec.Class, Sec.ClassGenericPassword),
                (Sec.AttrService, service),
                (Sec.AttrAccount, account),
                (Sec.AttrLabel, scope.String("ArChrono: " + key)),
                (Sec.ValueData, data));
            status = SecItemAdd(add, IntPtr.Zero);
        }
        if (status != ErrSecSuccess) throw new CredentialStoreException(Loc.T($"Keychain write failed (OSStatus {status}).", $"Anahtar Zincirine yazılamadı (OSStatus {status})."));
    }

    public bool Delete(string key)
    {
        using var scope = new CfScope();
        var query = scope.Dictionary((Sec.Class, Sec.ClassGenericPassword), (Sec.AttrService, scope.String(ServicePrefix + key)), (Sec.AttrAccount, scope.String(key)));
        var status = SecItemDelete(query);
        return status == ErrSecSuccess;
    }

    [DllImport(SecurityLibrary)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(SecurityLibrary)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(SecurityLibrary)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
    [DllImport(SecurityLibrary)] private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationLibrary)] private static extern void CFRelease(IntPtr handle);
    [DllImport(CoreFoundationLibrary, CharSet = CharSet.Unicode)] private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string characters, nint length);
    [DllImport(CoreFoundationLibrary)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundationLibrary)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundationLibrary)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundationLibrary)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);

    /// <summary>Security/CoreFoundation'ın dışa aktardığı sabitler (CFStringRef değişkenleri ve callback struct adresleri).</summary>
    private static class Sec
    {
        private static readonly IntPtr Security = NativeLibrary.Load(SecurityLibrary);
        private static readonly IntPtr CoreFoundation = NativeLibrary.Load(CoreFoundationLibrary);

        public static readonly IntPtr Class = Constant(Security, "kSecClass");
        public static readonly IntPtr ClassGenericPassword = Constant(Security, "kSecClassGenericPassword");
        public static readonly IntPtr AttrService = Constant(Security, "kSecAttrService");
        public static readonly IntPtr AttrAccount = Constant(Security, "kSecAttrAccount");
        public static readonly IntPtr AttrLabel = Constant(Security, "kSecAttrLabel");
        public static readonly IntPtr ValueData = Constant(Security, "kSecValueData");
        public static readonly IntPtr ReturnData = Constant(Security, "kSecReturnData");
        public static readonly IntPtr MatchLimit = Constant(Security, "kSecMatchLimit");
        public static readonly IntPtr MatchLimitOne = Constant(Security, "kSecMatchLimitOne");
        public static readonly IntPtr BooleanTrue = Constant(CoreFoundation, "kCFBooleanTrue");
        public static readonly IntPtr KeyCallbacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr ValueCallbacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

        private static IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    /// <summary>Oluşturulan CF nesnelerini kapsam sonunda serbest bırakır.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _handles = [];

        public IntPtr Track(IntPtr handle)
        {
            if (handle != IntPtr.Zero) _handles.Add(handle);
            return handle;
        }

        public IntPtr String(string value) => Track(CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length));

        public IntPtr Data(byte[] value) => Track(CFDataCreate(IntPtr.Zero, value, value.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs) =>
            Track(CFDictionaryCreate(IntPtr.Zero, pairs.Select(p => p.Key).ToArray(), pairs.Select(p => p.Value).ToArray(), pairs.Length, Sec.KeyCallbacks, Sec.ValueCallbacks));

        public void Dispose()
        {
            foreach (var handle in _handles) CFRelease(handle);
            _handles.Clear();
        }
    }
}

/// <summary>Windows Credential Manager — advapi32 CredWrite/CredRead/CredDelete.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string TargetPrefix = "ArChrono:";

    public bool IsAvailable => true;
    public string Description => Loc.T("Windows Credential Manager", "Windows Kimlik Bilgileri Yöneticisi");

    public string? Read(string key)
    {
        if (!CredReadW(TargetPrefix + key, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new CredentialStoreException(Loc.T($"Credential Manager read failed (error {error}).", $"Kimlik Bilgileri Yöneticisi okunamadı (hata {error})."));
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return string.Empty;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string key, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetPrefix + key,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "ArChrono",
            };
            if (!CredWriteW(ref credential, 0))
                throw new CredentialStoreException(Loc.T($"Credential Manager write failed (error {Marshal.GetLastWin32Error()}).", $"Kimlik Bilgileri Yöneticisine yazılamadı (hata {Marshal.GetLastWin32Error()})."));
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public bool Delete(string key) => CredDeleteW(TargetPrefix + key, CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
