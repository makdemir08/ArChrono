using ArChrono.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ArChrono.App.Controls;

/// <summary>
/// Gece/gündüz düğmesi: o an görünen temanın ikonunu (güneş/ay) gösterir.
/// Tıklama komutu kabuktadır; tercih ayarlara kaydedilir.
/// </summary>
public sealed class DayNightToggleButton : Button
{
    private readonly IconView _icon = new();

    public DayNightToggleButton()
    {
        Content = _icon;
    }

    /// <summary>Düğmenin Fluent Button temasıyla ve "icon" sınıfı stilleriyle çizilmesi için.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Avalonia.Application.Current is { } app) app.ActualThemeVariantChanged += OnThemeChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Avalonia.Application.Current is { } app) app.ActualThemeVariantChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var isNight = Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        _icon.Data = this.TryFindResource(isNight ? "Icon.Moon" : "Icon.Sun", out var geometry) ? geometry as Geometry : null;
        ToolTip.SetTip(this, isNight
            ? Loc.T("Night mode — click for day mode", "Gece modu — gündüz modu için tıklayın")
            : Loc.T("Day mode — click for night mode", "Gündüz modu — gece modu için tıklayın"));
    }
}
