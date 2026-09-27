using System.Globalization;
using ArChrono.AI.Context;
using ArChrono.Application.Settings;
using ArChrono.Git.Services;
using ArChrono.Localization;
using Avalonia.Data.Converters;

namespace ArChrono.App.Localization;

/// <summary>
/// XAML'de iki dilli metin: <c>Text="{l:Loc 'Open a repository', 'Depo aç'}"</c>.
/// Değer görünüm oluşturulurken seçilir; dil değişince kabuk görünümleri yeniden kurar
/// (bkz. <see cref="ViewModels.MainWindowViewModel.SetLanguage"/>), bu yüzden bağlama/abonelik gerekmez.
/// </summary>
public sealed class LocExtension(string english, string turkish)
{
    public string ProvideValue(IServiceProvider serviceProvider) => Loc.T(english, turkish);
}

/// <summary>Ayar seçeneklerinde gösterilen enum adları.</summary>
public static class Labels
{
    public static string Of(object? value) => value switch
    {
        ThemePreference.System => Loc.T("Same as system", "Sistemle aynı"),
        ThemePreference.Light => Loc.T("Day (light)", "Gündüz (açık)"),
        ThemePreference.Dark => Loc.T("Night (dark)", "Gece (koyu)"),
        LanguagePreference.System => Loc.T("Same as system", "Sistemle aynı"),
        LanguagePreference.English => "English",
        LanguagePreference.Turkish => "Türkçe",
        SnapshotRetention.OneDay => Loc.T("1 day", "1 gün"),
        SnapshotRetention.SevenDays => Loc.T("7 days", "7 gün"),
        SnapshotRetention.ThirtyDays => Loc.T("30 days", "30 gün"),
        SnapshotRetention.NinetyDays => Loc.T("90 days", "90 gün"),
        SnapshotRetention.UntilDiskLimit => Loc.T("Until the storage limit", "Depolama sınırına kadar"),
        CommitMessageStyle.Short => Loc.T("Short", "Kısa"),
        CommitMessageStyle.Conventional => "Conventional Commits",
        CommitMessageStyle.Detailed => Loc.T("Detailed", "Ayrıntılı"),
        PullMode.Default => Loc.T("Git default (pull.rebase)", "Git varsayılanı (pull.rebase)"),
        PullMode.Merge => "Merge",
        PullMode.Rebase => "Rebase",
        PullMode.FastForwardOnly => Loc.T("Fast-forward only", "Yalnızca fast-forward"),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };
}

public sealed class LabelConverter : IValueConverter
{
    public static readonly LabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Labels.Of(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
