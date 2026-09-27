using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ArChrono.App.Controls;

public enum ChronoMarkKind
{
    RecoveryPoint,
    Snapshot,
    Failed,
    Outside,
}

public sealed record ChronoMark(DateTimeOffset Time, ChronoMarkKind Kind, string Title, object? Payload);

/// <summary>
/// ArChrono'nun imza öğesi: zaman ekseni üzerinde recovery point (◆ amber), snapshot (● teal),
/// başarısız işlem (✕ kırmızı) işaretleri. Üzerine gelince başlık, tıklayınca <see cref="MarkClicked"/>.
/// </summary>
public sealed class ChronoStrip : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChronoMark>?> MarksProperty =
        AvaloniaProperty.Register<ChronoStrip, IReadOnlyList<ChronoMark>?>(nameof(Marks));

    public static readonly StyledProperty<DateTimeOffset> StartProperty =
        AvaloniaProperty.Register<ChronoStrip, DateTimeOffset>(nameof(Start), DateTimeOffset.Now.AddHours(-8));

    public static readonly StyledProperty<DateTimeOffset> EndProperty =
        AvaloniaProperty.Register<ChronoStrip, DateTimeOffset>(nameof(End), DateTimeOffset.Now);

    public static readonly StyledProperty<object?> SelectedPayloadProperty =
        AvaloniaProperty.Register<ChronoStrip, object?>(nameof(SelectedPayload));

    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<ChronoStrip, bool>(nameof(ShowLabels), true);

    private ChronoMark? _hover;

    static ChronoStrip()
    {
        AffectsRender<ChronoStrip>(MarksProperty, StartProperty, EndProperty, SelectedPayloadProperty, ShowLabelsProperty);
    }

    public ChronoStrip()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public event EventHandler<ChronoMark>? MarkClicked;

    public IReadOnlyList<ChronoMark>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    public DateTimeOffset Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public DateTimeOffset End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public object? SelectedPayload
    {
        get => GetValue(SelectedPayloadProperty);
        set => SetValue(SelectedPayloadProperty, value);
    }

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    private double Padding => 12;

    private double XFor(DateTimeOffset time)
    {
        var span = (End - Start).TotalSeconds;
        if (span <= 0) return Padding;
        var ratio = Math.Clamp((time - Start).TotalSeconds / span, 0, 1);
        return Padding + ratio * (Bounds.Width - Padding * 2);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var axisY = ShowLabels ? height * 0.42 : height / 2;
        var subtle = Resource("Chrono.BorderStrong") ?? Brushes.Gray;
        var tertiary = Resource("Chrono.TextTertiary") ?? Brushes.Gray;
        var safe = Resource("Chrono.Safe") ?? Brushes.Teal;
        var time = Resource("Chrono.Time") ?? Brushes.Orange;
        var danger = Resource("Chrono.Danger") ?? Brushes.Red;
        var panel = Resource("Chrono.Panel") ?? Brushes.Black;

        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.DrawLine(new Pen(subtle, 1), new Point(Padding, axisY), new Point(width - Padding, axisY));

        // Saat çizgileri
        var totalHours = (End - Start).TotalHours;
        var step = totalHours <= 3 ? 0.25 : totalHours <= 12 ? 1 : totalHours <= 48 ? 3 : 24;
        var tick = new DateTimeOffset(Start.Year, Start.Month, Start.Day, Start.Hour, 0, 0, Start.Offset);
        while (tick <= End)
        {
            if (tick >= Start)
            {
                var x = XFor(tick);
                context.DrawLine(new Pen(subtle, 1), new Point(x, axisY - 3), new Point(x, axisY + 3));
                if (ShowLabels)
                {
                    var label = new FormattedText(tick.ToString(step >= 24 ? "MMM d" : "HH:mm", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Typeface.Default, 10, tertiary);
                    context.DrawText(label, new Point(x - label.Width / 2, axisY + 6));
                }
            }
            tick = tick.AddHours(step);
        }

        // "Şimdi"
        if (End >= DateTimeOffset.Now.AddMinutes(-1))
        {
            var nowX = XFor(DateTimeOffset.Now);
            context.DrawEllipse(safe, null, new Point(nowX, axisY), 3, 3);
        }

        if (Marks is null) return;
        foreach (var mark in Marks.OrderBy(m => m.Kind == ChronoMarkKind.RecoveryPoint))
        {
            if (mark.Time < Start || mark.Time > End) continue;
            var center = new Point(XFor(mark.Time), axisY);
            var selected = SelectedPayload is not null && Equals(mark.Payload, SelectedPayload);
            var hovered = ReferenceEquals(mark, _hover);
            var grow = selected || hovered ? 1.5 : 1.0;

            switch (mark.Kind)
            {
                case ChronoMarkKind.RecoveryPoint:
                {
                    var r = 5 * grow;
                    var diamond = new StreamGeometry();
                    using (var g = diamond.Open())
                    {
                        g.BeginFigure(new Point(center.X, center.Y - r), true);
                        g.LineTo(new Point(center.X + r, center.Y));
                        g.LineTo(new Point(center.X, center.Y + r));
                        g.LineTo(new Point(center.X - r, center.Y));
                        g.EndFigure(true);
                    }
                    context.DrawGeometry(time, new Pen(panel, 1.5), diamond);
                    break;
                }
                case ChronoMarkKind.Snapshot:
                    context.DrawEllipse(panel, new Pen(safe, 1.8), center, 3.5 * grow, 3.5 * grow);
                    break;
                case ChronoMarkKind.Failed:
                {
                    var r = 4 * grow;
                    var pen = new Pen(danger, 2, lineCap: PenLineCap.Round);
                    context.DrawLine(pen, new Point(center.X - r, center.Y - r), new Point(center.X + r, center.Y + r));
                    context.DrawLine(pen, new Point(center.X - r, center.Y + r), new Point(center.X + r, center.Y - r));
                    break;
                }
                default:
                    context.DrawEllipse(tertiary, null, center, 2.5 * grow, 2.5 * grow);
                    break;
            }

            if (selected)
                context.DrawEllipse(null, new Pen(time, 1), center, 9, 9);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover)) return;
        _hover = hit;
        ToolTip.SetTip(this, hit is null ? null : $"{hit.Time:HH:mm:ss}  {hit.Title}");
        ToolTip.SetIsOpen(this, hit is not null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitTest(e.GetPosition(this)) is { } mark)
        {
            MarkClicked?.Invoke(this, mark);
            e.Handled = true;
        }
    }

    private ChronoMark? HitTest(Point point)
    {
        if (Marks is null) return null;
        ChronoMark? best = null;
        var bestDistance = 8.0;
        foreach (var mark in Marks)
        {
            if (mark.Time < Start || mark.Time > End) continue;
            var distance = Math.Abs(XFor(mark.Time) - point.X);
            if (distance < bestDistance || (Math.Abs(distance - bestDistance) < 0.5 && mark.Kind == ChronoMarkKind.RecoveryPoint))
            {
                best = mark;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static IBrush? Resource(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;
}
