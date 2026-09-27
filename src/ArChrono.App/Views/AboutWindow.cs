using ArChrono.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ArChrono.App.Views;

/// <summary>Hakkında: ArSoft logosu, geliştirici ve web sitesi bağlantısı (ArSnap ile aynı düzen).</summary>
public sealed class AboutWindow : Window
{
    private const string LogoUri = "avares://ArChrono/Assets/ArSoftLogo.png";
    private const string WebsiteUrl = "https://www.arsoft.com.tr";
    private const double LogoWidth = 240;

    private static AboutWindow? s_current;

    private AboutWindow()
    {
        Title = Loc.T("About ArChrono", "ArChrono Hakkında");
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = App.WindowIcon;
        Bind(BackgroundProperty, this.GetResourceObservable("Chrono.Window"));

        Bitmap logo;
        using (var stream = AssetLoader.Open(new Uri(LogoUri)))
            logo = new Bitmap(stream);

        // Logo orijinal haliyle, en-boy oranı korunarak gösterilir. Gece modunda da okunabilmesi için
        // (logo değiştirilmeden) her zaman beyaz bir kartın üzerinde durur.
        var image = new Image { Source = logo, Width = LogoWidth, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);

        var logoCard = new Border
        {
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = image,
        };
        logoCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("Chrono.BorderSubtle"));

        Content = new StackPanel
        {
            Margin = new Thickness(32, 28),
            Spacing = 14,
            Children =
            {
                logoCard,
                new TextBlock
                {
                    Text = "Mustafa AKDEMİR",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new HyperlinkButton
                {
                    Content = "www.arsoft.com.tr",
                    NavigateUri = new Uri(WebsiteUrl),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };
    }

    /// <summary>Pencere zaten açıksa öne getirir, değilse açar.</summary>
    public static void ShowSingle(Window? owner)
    {
        if (s_current is not null)
        {
            s_current.Activate();
            return;
        }

        s_current = new AboutWindow();
        s_current.Closed += (_, _) => s_current = null;
        if (owner is { IsVisible: true })
            s_current.Show(owner);
        else
            s_current.Show();
        s_current.Activate();
    }

    /// <summary>Dil değişince açık pencerenin başlığını günceller.</summary>
    public static void RefreshTitle()
    {
        if (s_current is not null) s_current.Title = Loc.T("About ArChrono", "ArChrono Hakkında");
    }
}
