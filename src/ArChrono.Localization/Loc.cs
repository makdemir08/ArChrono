using System.Globalization;

namespace ArChrono.Localization;

public enum AppLanguage
{
    English,
    Turkish,
}

/// <summary>
/// Arayüz dili. Metinler çağrı noktasında iki dilde yazılır: <c>Loc.T("Undo", "Geri al")</c>.
/// Varsayılan İngilizce'dir; uygulama açılışta ayarlara göre dili seçer (testler deterministik kalır).
/// </summary>
/// <remarks>
/// Yalnızca metin seçimi yapılır; <see cref="CultureInfo.CurrentCulture"/> değiştirilmez.
/// Git çıktısı ayrıştırma ve karşılaştırmalar kültürden bağımsız kalsın diye (Türkçe "I/ı" sorunu)
/// tarih/sayı biçimlendirmesi yalnızca gösterimde <see cref="Culture"/> ile yapılır.
/// </remarks>
public static class Loc
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static AppLanguage Language { get; private set; } = AppLanguage.English;

    public static bool IsTurkish => Language == AppLanguage.Turkish;

    /// <summary>Gösterim amaçlı tarih ve sayı biçimlendirme kültürü.</summary>
    public static CultureInfo Culture => IsTurkish ? Turkish : English;

    public static event EventHandler? LanguageChanged;

    public static string T(string english, string turkish) => IsTurkish ? turkish : english;

    public static void SetLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>İşletim sisteminin arayüz dili; Türkçe değilse İngilizce.</summary>
    public static AppLanguage DetectSystemLanguage(CultureInfo systemCulture) =>
        systemCulture.TwoLetterISOLanguageName == "tr" ? AppLanguage.Turkish : AppLanguage.English;

    /// <summary>Gösterim kültürüne göre büyük harf ("i" → "İ").</summary>
    public static string Upper(string text) => text.ToUpper(Culture);
}
