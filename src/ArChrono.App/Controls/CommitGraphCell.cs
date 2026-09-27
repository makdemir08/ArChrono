using ArChrono.Git.Graph;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ArChrono.App.Controls;

/// <summary>Commit grafiğinin tek satırı. Her satır kendi kenarlarını çizer; sanallaştırılmış listede bağımsız render edilir.</summary>
public sealed class CommitGraphCell : Control
{
    public const double LaneWidth = 14;
    public const double LeftPadding = 6;

    public static readonly StyledProperty<GraphRow?> RowProperty =
        AvaloniaProperty.Register<CommitGraphCell, GraphRow?>(nameof(Row));

    public static readonly StyledProperty<bool> IsHeadProperty =
        AvaloniaProperty.Register<CommitGraphCell, bool>(nameof(IsHead));

    public static readonly StyledProperty<bool> IsWorkingTreeProperty =
        AvaloniaProperty.Register<CommitGraphCell, bool>(nameof(IsWorkingTree));

    static CommitGraphCell()
    {
        AffectsRender<CommitGraphCell>(RowProperty, IsHeadProperty, IsWorkingTreeProperty);
    }

    public GraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public bool IsHead
    {
        get => GetValue(IsHeadProperty);
        set => SetValue(IsHeadProperty, value);
    }

    public bool IsWorkingTree
    {
        get => GetValue(IsWorkingTreeProperty);
        set => SetValue(IsWorkingTreeProperty, value);
    }

    public static double LaneX(int lane) => LeftPadding + lane * LaneWidth + LaneWidth / 2;

    public static IBrush LaneBrush(int colorIndex)
    {
        var key = "Chrono.Lane" + (colorIndex % CommitGraphLayout.PaletteSize);
        if (Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Color color)
            return new SolidColorBrush(color);
        return Brushes.Teal;
    }

    public override void Render(DrawingContext context)
    {
        var height = Bounds.Height;
        var middle = height / 2;

        if (IsWorkingTree)
        {
            // Commit edilmemiş değişiklikler: kesik çizgili halka + HEAD'e inen kesik çizgi.
            var brush = TryBrush("Chrono.Time") ?? Brushes.Orange;
            var dashed = new Pen(brush, 1.5, new DashStyle([2, 2], 0));
            var x = LaneX(Row?.NodeLane ?? 0);
            context.DrawLine(dashed, new Point(x, middle + 5), new Point(x, height));
            context.DrawEllipse(null, new Pen(brush, 1.8), new Point(x, middle), 5, 5);
            return;
        }

        if (Row is not { } row) return;

        foreach (var edge in row.Edges)
        {
            var pen = new Pen(LaneBrush(edge.ColorIndex), 1.8, lineCap: PenLineCap.Round);
            var start = new Point(LaneX(edge.FromLane), Y(edge.FromAnchor, height));
            var end = new Point(LaneX(edge.ToLane), Y(edge.ToAnchor, height));
            if (Math.Abs(start.X - end.X) < 0.1)
            {
                context.DrawLine(pen, start, end);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var bend = (end.Y - start.Y) * 0.8;
                g.BeginFigure(start, false);
                g.CubicBezierTo(new Point(start.X, start.Y + bend), new Point(end.X, end.Y - bend), end);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        var node = new Point(LaneX(row.NodeLane), middle);
        var nodeBrush = LaneBrush(row.NodeColorIndex);
        var background = TryBrush("Chrono.Panel") ?? Brushes.Black;
        if (IsHead)
        {
            context.DrawEllipse(background, new Pen(nodeBrush, 2), node, 6, 6);
            context.DrawEllipse(nodeBrush, null, node, 3, 3);
        }
        else if (row.IsMerge)
        {
            context.DrawEllipse(background, new Pen(nodeBrush, 2), node, 4, 4);
        }
        else
        {
            context.DrawEllipse(nodeBrush, new Pen(background, 1.5), node, 4.5, 4.5);
        }
    }

    private static double Y(GraphAnchor anchor, double height) => anchor switch
    {
        GraphAnchor.Top => 0,
        GraphAnchor.Middle => height / 2,
        _ => height,
    };

    private static IBrush? TryBrush(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value as IBrush : null;
}
