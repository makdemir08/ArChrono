using ArChrono.Git.Models;

namespace ArChrono.Git.Graph;

public enum GraphAnchor
{
    Top,
    Middle,
    Bottom,
}

/// <summary>Bir satır içinde çizilecek kenar. Satırlar birbirinden bağımsız çizilebilir.</summary>
public readonly record struct GraphEdge(int FromLane, GraphAnchor FromAnchor, int ToLane, GraphAnchor ToAnchor, int ColorIndex);

public sealed record GraphRow(int NodeLane, int NodeColorIndex, IReadOnlyList<GraphEdge> Edges, int LaneCount, bool IsMerge);

/// <summary>
/// Commit grafiği lane yerleşimi. Commit'ler topolojik sırada (çocuk önce) verilmelidir.
/// Durum korunur; sayfalı yüklemede <see cref="Append"/> kaldığı yerden devam eder.
/// </summary>
public sealed class CommitGraphLayout
{
    public const int PaletteSize = 8;

    private readonly List<Lane> _lanes = [];
    private int _nextColor;

    private record struct Lane(string? Sha, int Color)
    {
        public readonly bool IsFree => Sha is null;
    }

    public static IReadOnlyList<GraphRow> Compute(IReadOnlyList<CommitInfo> commits) => new CommitGraphLayout().Append(commits);

    public IReadOnlyList<GraphRow> Append(IReadOnlyList<CommitInfo> commits)
    {
        var rows = new List<GraphRow>(commits.Count);
        foreach (var commit in commits) rows.Add(Place(commit));
        return rows;
    }

    private GraphRow Place(CommitInfo commit)
    {
        var edges = new List<GraphEdge>();

        var nodeLane = _lanes.FindIndex(l => l.Sha == commit.Sha);
        var isNewLane = nodeLane < 0;
        int color;
        if (isNewLane)
        {
            nodeLane = FirstFreeLane(exclude: -1);
            color = _nextColor++ % PaletteSize;
            _lanes[nodeLane] = new Lane(commit.Sha, color);
        }
        else
        {
            color = _lanes[nodeLane].Color;
        }

        var widthBefore = _lanes.Count;

        // Üst yarı: bu commit'i bekleyen lane'ler düğüme birleşir, diğerleri geçer.
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            if (lane.IsFree) continue;
            if (lane.Sha == commit.Sha)
            {
                if (i == nodeLane && isNewLane) continue;
                edges.Add(new GraphEdge(i, GraphAnchor.Top, nodeLane, GraphAnchor.Middle, lane.Color));
            }
            else
            {
                edges.Add(new GraphEdge(i, GraphAnchor.Top, i, GraphAnchor.Middle, lane.Color));
            }
        }

        for (var i = 0; i < _lanes.Count; i++)
        {
            if (i != nodeLane && _lanes[i].Sha == commit.Sha) _lanes[i] = default;
        }

        var createdThisRow = new HashSet<int>();
        if (commit.Parents.Count == 0)
        {
            _lanes[nodeLane] = default;
        }
        else
        {
            _lanes[nodeLane] = new Lane(commit.Parents[0], color);
            for (var p = 1; p < commit.Parents.Count; p++)
            {
                var parent = commit.Parents[p];
                var target = _lanes.FindIndex(l => l.Sha == parent);
                if (target < 0)
                {
                    target = FirstFreeLane(exclude: nodeLane);
                    _lanes[target] = new Lane(parent, _nextColor++ % PaletteSize);
                    createdThisRow.Add(target);
                }
                edges.Add(new GraphEdge(nodeLane, GraphAnchor.Middle, target, GraphAnchor.Bottom, _lanes[target].Color));
            }
        }

        // Alt yarı: yaşayan lane'ler aşağı devam eder.
        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            if (lane.IsFree || createdThisRow.Contains(i)) continue;
            edges.Add(new GraphEdge(i, GraphAnchor.Middle, i, GraphAnchor.Bottom, lane.Color));
        }

        var laneCount = Math.Max(widthBefore, _lanes.Count);
        TrimTrailingFreeLanes();
        return new GraphRow(nodeLane, color, edges, Math.Max(laneCount, nodeLane + 1), commit.IsMerge);
    }

    private int FirstFreeLane(int exclude)
    {
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (i != exclude && _lanes[i].IsFree) return i;
        }
        _lanes.Add(default);
        return _lanes.Count - 1;
    }

    private void TrimTrailingFreeLanes()
    {
        while (_lanes.Count > 0 && _lanes[^1].IsFree) _lanes.RemoveAt(_lanes.Count - 1);
    }
}
