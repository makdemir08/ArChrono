using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ArChrono.App.Services;

/// <summary>
/// Uygulama ikonu kodla çizilir: koyu mürekkep zemin, teal geri dönüş oku (zaman makinesi)
/// ve ok ucunda amber recovery point elması. Pencere ikonu ve kurulum paketleri aynı çizimi kullanır.
/// </summary>
public static class AppIcon
{
    public static void Render(DrawingContext context, double size)
    {
        var s = size / 1024.0;
        var background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#1B2230"), 0), new GradientStop(Color.Parse("#0B0D12"), 1) },
        };
        context.DrawRectangle(background, null, new RoundedRect(new Rect(40 * s, 40 * s, 944 * s, 944 * s), 210 * s));

        var teal = new SolidColorBrush(Color.Parse("#2DD4BF"));
        var amber = new SolidColorBrush(Color.Parse("#F5B84B"));
        var center = new Point(512 * s, 530 * s);
        const double radius = 290;

        // Saat yönünün tersine dönen, sol üstte açık bir halka: "zamanda geri".
        var ring = new StreamGeometry();
        using (var g = ring.Open())
        {
            g.BeginFigure(PointOnCircle(center, radius * s, 200), false);
            g.ArcTo(PointOnCircle(center, radius * s, 250), new Size(radius * s, radius * s), 0, true, SweepDirection.CounterClockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(teal, 64 * s, lineCap: PenLineCap.Round), ring);

        // Ok ucu: halkanın sonunda, hareket yönünde (sola).
        var tip = PointOnCircle(center, radius * s, 250);
        var theta = 250 * Math.PI / 180;
        var direction = new Vector(Math.Sin(theta), -Math.Cos(theta));
        var normal = new Vector(-direction.Y, direction.X);
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(tip + direction * 95 * s, true);
            g.LineTo(tip - direction * 35 * s + normal * 82 * s);
            g.LineTo(tip - direction * 35 * s - normal * 82 * s);
            g.EndFigure(true);
        }
        context.DrawGeometry(teal, new Pen(teal, 14 * s, lineJoin: PenLineJoin.Round), arrow);

        // Saat kolları
        var hands = new Pen(new SolidColorBrush(Color.Parse("#E6E9EF")), 46 * s, lineCap: PenLineCap.Round);
        context.DrawLine(hands, center, new Point(center.X, center.Y - 150 * s));
        context.DrawLine(hands, center, new Point(center.X + 112 * s, center.Y + 64 * s));

        // Recovery point elması (halkanın başlangıcı): ok ona doğru döner.
        var diamondCenter = PointOnCircle(center, radius * s, 200);
        const double d = 74;
        var diamond = new StreamGeometry();
        using (var g = diamond.Open())
        {
            g.BeginFigure(new Point(diamondCenter.X, diamondCenter.Y - d * s), true);
            g.LineTo(new Point(diamondCenter.X + d * s, diamondCenter.Y));
            g.LineTo(new Point(diamondCenter.X, diamondCenter.Y + d * s));
            g.LineTo(new Point(diamondCenter.X - d * s, diamondCenter.Y));
            g.EndFigure(true);
        }
        context.DrawGeometry(amber, new Pen(new SolidColorBrush(Color.Parse("#0B0D12")), 22 * s), diamond);
    }

    public static RenderTargetBitmap CreateBitmap(int size)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext()) Render(context, size);
        return bitmap;
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
