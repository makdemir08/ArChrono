using System.Text.RegularExpressions;

namespace ArChrono.Git.Process;

/// <summary>Log ve Git Console çıktısında URL içindeki kimlik bilgilerini maskeler.</summary>
public static partial class SecretMasker
{
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)(?<cred>[^/@\s]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    public static string Mask(string text) =>
        string.IsNullOrEmpty(text) ? text : UrlCredentials().Replace(text, m => m.Groups["scheme"].Value + "***@");
}
