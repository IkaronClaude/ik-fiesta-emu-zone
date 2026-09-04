namespace Fiesta.Emu.Zone.Lua;

/// <summary>⭐ THE MAP AS RECTANGLES OF FREE SPACE, PLUS THE PORTALS BETWEEN THEM — the sibling bot
/// project's navmesh, which the operator confirms works, rebuilt here because tile A* does not.
///
/// <para><b>Why not tile A*.</b> A full-grid search pays for two different problems at once — WHICH WAY to
/// go, and HOW to get round the rock in front of you — and the first one dominates. This project measured
/// the consequence rather than reading about it: a bounded A* was <b>failing to route 100 of 101</b>
/// `walkTo` calls on Burning Hill, because a mere 36-tile journey around an obstacle fans the search out
/// over tens of thousands of tiles before it finds the way round. Every failure fell back to a straight
/// line into geometry and the character was blocked on 844 of 1,500 ticks, which read as a bot that
/// cannot navigate. Raising the budget tenfold papered over it; the search is the wrong shape.</para>
///
/// <para><b>Why rectangles.</b> Convexity is the whole trick: inside a convex region any two points join
/// by a straight line, so there is no local search left to do — only the route BETWEEN regions, over a
/// graph orders of magnitude smaller than the grid. Rectangles are the grid-native convex shape: integer
/// adjacency, axis-aligned edges, no floating-point geometry to get subtly wrong. A room with a rock in it
/// simply becomes a few rectangles around the rock.</para>
///
/// <para>⚠️ The bot's own mesh also carries `.sbi` DOOR regions, so that a door closing is a node toggle
/// rather than a guess about severed portals. That is deliberately absent here: this simulation does not
/// model doors, and carrying half of the mechanism would be a claim it cannot back.</para></summary>
public sealed class NavMesh
{
    public readonly record struct Rect(int X, int Y, int W, int H)
    {
        public int MaxX => X + W - 1;
        public int MaxY => Y + H - 1;
        public int CentreX => X + W / 2;
        public int CentreY => Y + H / 2;
    }

    /// <summary>A shared boundary between two regions. The run is the overlap along it; recording a portal
    /// as a single point would throw away the choice of where to cross.</summary>
    public readonly record struct Portal(int To, bool Vertical, int At, int From, int Until)
    {
        public int Middle => (From + Until) / 2;
    }

    public IReadOnlyList<Rect> Rects { get; }
    public IReadOnlyList<IReadOnlyList<Portal>> Portals { get; }

    private readonly int[] _owner;          // tile -> rect id, -1 where blocked
    private readonly int _width;
    private readonly int _height;

    private NavMesh(int width, int height, int[] owner, List<Rect> rects, List<List<Portal>> portals)
    {
        _width = width;
        _height = height;
        _owner = owner;
        Rects = rects;
        Portals = [.. portals.Select(p => (IReadOnlyList<Portal>)p)];
    }

    /// <summary>Which region a tile belongs to, or -1 when it is not free space.</summary>
    public int RegionAt(int tileX, int tileY)
        => (uint)tileX >= (uint)_width || (uint)tileY >= (uint)_height ? -1 : _owner[tileY * _width + tileX];

    /// <summary>Decompose a walkability grid. Greedy maximal rectangles, row-major: grow width first, then
    /// height while every tile of the row is free and unclaimed.</summary>
    public static NavMesh Build(WalkabilityGrid grid)
    {
        int w = grid.WidthTiles, h = grid.HeightTiles;

        var free = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                free[y * w + x] = grid.IsWalkableTile(x, y);

        var owner = new int[w * h];
        Array.Fill(owner, -1);
        var rects = new List<Rect>();

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var id = y * w + x;
                if (!free[id] || owner[id] >= 0) continue;

                var rw = 0;
                while (x + rw < w && free[y * w + x + rw] && owner[y * w + x + rw] < 0) rw++;

                var rh = 1;
                while (y + rh < h)
                {
                    var ok = true;
                    for (var k = 0; k < rw; k++)
                    {
                        var t = (y + rh) * w + x + k;
                        if (free[t] && owner[t] < 0) continue;
                        ok = false;
                        break;
                    }
                    if (!ok) break;
                    rh++;
                }

                var rid = rects.Count;
                for (var yy = 0; yy < rh; yy++)
                    for (var xx = 0; xx < rw; xx++)
                        owner[(y + yy) * w + x + xx] = rid;
                rects.Add(new Rect(x, y, rw, rh));
            }

        var portals = new List<List<Portal>>(rects.Count);
        for (var i = 0; i < rects.Count; i++) portals.Add([]);

        void Link(int a, int b, bool vertical, int at, int from, int until)
        {
            portals[a].Add(new Portal(b, vertical, at, from, until));
            portals[b].Add(new Portal(a, vertical, at, from, until));
        }

        // ⚠️ NO DEDUPLICATION GUARD, deliberately. Only each rectangle's RIGHT and BOTTOM edges are
        // scanned, so every adjacent pair is already visited exactly once, from the left/top side. The
        // sibling project records that an earlier `neighbour > i` guard looked like dedup and actually
        // DROPPED every adjacency whose neighbour had a lower id — which happens whenever a tall rectangle
        // created earlier sits to the right of a later one. The graph came out disconnected and most
        // routes silently failed.
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];

            var nx = r.MaxX + 1;
            if (nx < w)
            {
                int run = -1, from = 0;
                for (var y = r.Y; y <= r.MaxY; y++)
                {
                    var nb = owner[y * w + nx];
                    if (nb == run) continue;
                    if (run >= 0 && run != i) Link(i, run, true, nx, from, y - 1);
                    run = nb;
                    from = y;
                }
                if (run >= 0 && run != i) Link(i, run, true, nx, from, r.MaxY);
            }

            var ny = r.MaxY + 1;
            if (ny < h)
            {
                int run = -1, from = 0;
                for (var x = r.X; x <= r.MaxX; x++)
                {
                    var nb = owner[ny * w + x];
                    if (nb == run) continue;
                    if (run >= 0 && run != i) Link(i, run, false, ny, from, x - 1);
                    run = nb;
                    from = x;
                }
                if (run >= 0 && run != i) Link(i, run, false, ny, from, r.MaxX);
            }
        }

        return new NavMesh(w, h, owner, rects, portals);
    }

    /// <summary>Route between two tiles as a list of region ids, or null when they are not connected.
    /// A* over the REGION graph — thousands of nodes rather than millions of tiles.</summary>
    public IReadOnlyList<int>? RegionRoute(int fromRegion, int toRegion)
    {
        if (fromRegion < 0 || toRegion < 0) return null;
        if (fromRegion == toRegion) return [fromRegion];

        var open = new PriorityQueue<int, int>();
        var cameFrom = new Dictionary<int, int>();
        var cost = new Dictionary<int, int> { [fromRegion] = 0 };
        open.Enqueue(fromRegion, 0);

        var goal = Rects[toRegion];
        while (open.TryDequeue(out var current, out _))
        {
            if (current == toRegion)
            {
                var route = new List<int> { current };
                while (cameFrom.TryGetValue(current, out var previous))
                {
                    current = previous;
                    route.Add(current);
                }
                route.Reverse();
                return route;
            }

            var here = Rects[current];
            foreach (var portal in Portals[current])
            {
                var next = Rects[portal.To];
                var step = Math.Abs(next.CentreX - here.CentreX) + Math.Abs(next.CentreY - here.CentreY);
                var candidate = cost[current] + step;
                if (cost.TryGetValue(portal.To, out var known) && candidate >= known) continue;

                cost[portal.To] = candidate;
                cameFrom[portal.To] = current;
                open.Enqueue(portal.To,
                    candidate + Math.Abs(goal.CentreX - next.CentreX) + Math.Abs(goal.CentreY - next.CentreY));
            }
        }
        return null;
    }
}
