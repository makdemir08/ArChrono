using System.Globalization;
using ArChrono.Git.Models;
using ArChrono.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ArChrono.App.Infrastructure;

public abstract class ViewModelBase : ObservableObject
{
    protected static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>"XxxViewModel" → "XxxView" eşlemesi (aynı assembly, Views ad alanı).</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Type?> Cache = [];

    public static ViewLocator Instance { get; } = new();

    public Control? Build(object? data)
    {
        if (data is null) return null;
        var type = data.GetType();
        if (!Cache.TryGetValue(type, out var viewType))
        {
            var name = type.FullName!.Replace(".ViewModels.", ".Views.").Replace("ViewModel", "View");
            viewType = type.Assembly.GetType(name);
            Cache[type] = viewType;
        }
        return viewType is null
            ? new TextBlock { Text = Loc.T("View not found: ", "Görünüm bulunamadı: ") + type.Name }
            : (Control)Activator.CreateInstance(viewType)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}

public static class Format
{
    public static string Relative(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        if (span.TotalSeconds < 45) return Loc.T("just now", "az önce");
        if (span.TotalMinutes < 60) return Loc.T($"{(int)span.TotalMinutes} min ago", $"{(int)span.TotalMinutes} dk önce");
        if (span.TotalHours < 24) return Loc.T($"{(int)span.TotalHours} h ago", $"{(int)span.TotalHours} sa önce");
        if (span.TotalDays < 7) return Loc.T($"{(int)span.TotalDays} d ago", $"{(int)span.TotalDays} gün önce");
        return time.ToString(Loc.T("MMM d, yyyy", "d MMM yyyy"), Loc.Culture);
    }

    public static string DayHeader(DateTimeOffset time)
    {
        var date = time.LocalDateTime.Date;
        if (date == DateTime.Today) return Loc.T("Today", "Bugün");
        if (date == DateTime.Today.AddDays(-1)) return Loc.T("Yesterday", "Dün");
        return date.ToString(Loc.T("dddd, MMM d", "d MMMM, dddd"), Loc.Culture);
    }

    /// <summary>Gösterim kültürüyle tarih/saat ("dddd, MMM d · HH:mm:ss" gibi).</summary>
    public static string Date(DateTimeOffset time, string englishPattern, string turkishPattern) =>
        time.ToString(Loc.T(englishPattern, turkishPattern), Loc.Culture);

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", Loc.Culture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", Loc.Culture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", Loc.Culture) + " GB",
    };

    public static string ChangeLetter(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.Untracked => "U",
        ChangeKind.Unmerged => "!",
        ChangeKind.TypeChanged => "T",
        _ => "M",
    };

    public static string ChangeResource(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.Untracked => "Chrono.Safe",
        ChangeKind.Deleted => "Chrono.Danger",
        ChangeKind.Unmerged => "Chrono.Time",
        ChangeKind.Renamed or ChangeKind.Copied => "Chrono.Info",
        _ => "Chrono.Time",
    };
}

public static class Resources
{
    public static IBrush Brush(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    public static Geometry? Icon(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, null, out var value) ? value as Geometry : null;
}

/// <summary>XAML'de "Icon.Branch" gibi anahtarları geometriye çevirir.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Resources.Icon(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Kaynak anahtarını (ör. "Chrono.Safe") fırçaya çevirir.</summary>
public sealed class BrushKeyConverter : IValueConverter
{
    public static readonly BrushKeyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Resources.Brush(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToWeightConverter : IValueConverter
{
    public static readonly BoolToWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontWeight.SemiBold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
