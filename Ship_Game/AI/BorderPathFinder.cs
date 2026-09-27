using System;
using System.Collections.Generic;
using SDGraphics;

namespace Ship_Game.AI;

// Bounded A* over the resolved territory, with exact segment checks for edges
// and string pulling. A failed search never returns a partial path as a success.
internal static class BorderPathFinder
{
    public static bool TryFind(Vector2 from, Vector2 to, Vector2 min, Vector2 max,
                               float preferredCell, Func<Vector2, bool> canOccupy,
                               Func<Vector2, Vector2, bool> canTravel, out Vector2[] detours)
    {
        detours = System.Array.Empty<Vector2>();
        if (canTravel(from, to)) return true;
        for (int resolution = 160; resolution <= 320; resolution *= 2)
        {
            float cell = Math.Max(preferredCell * (160f / resolution),
                Math.Max(max.X - min.X, max.Y - min.Y) / resolution);
            if (Search(cell, out detours)) return true;
        }
        return false;

        bool Search(float cell, out Vector2[] result)
        {
            result = System.Array.Empty<Vector2>();
            int columns = (int)Math.Ceiling((max.X - min.X) / cell) + 1;
            int rows = (int)Math.Ceiling((max.Y - min.Y) / cell) + 1;
            int count = columns * rows;
            var costs = new float[count];
            System.Array.Fill(costs, float.PositiveInfinity);
            var parent = new int[count];
            System.Array.Fill(parent, -1);
            var state = new byte[count]; // 0 unknown, 1 clear, 2 blocked, 3 expanded
            var open = new PriorityQueue<int, (float Cost, int Index)>();
            int sx = (int)Math.Round((from.X - min.X) / cell);
            int sy = (int)Math.Round((from.Y - min.Y) / cell);
            for (int dy = -2; dy <= 2; ++dy)
                for (int dx = -2; dx <= 2; ++dx)
                {
                    int x = sx + dx, y = sy + dy;
                    if (!InBounds(x, y)) continue;
                    int index = x + y * columns;
                    Vector2 point = Point(index);
                    if (!Clear(index) || !canTravel(from, point)) continue;
                    costs[index] = from.Distance(point);
                    open.Enqueue(index, (costs[index] + point.Distance(to), index));
                }

            int expanded = 0;
            while (open.TryDequeue(out int current, out _) && expanded++ < 60000)
            {
                if (state[current] == 3) continue;
                state[current] = 3;
                Vector2 point = Point(current);
                if (point.SqDist(to) <= cell * cell * 9f && canTravel(point, to))
                {
                    var path = new List<Vector2> { to };
                    for (int p = current; p >= 0; p = parent[p]) path.Add(Point(p));
                    path.Add(from);
                    path.Reverse();
                    var pulled = new List<Vector2>();
                    int anchor = 0;
                    while (anchor < path.Count - 1)
                    {
                        int next = path.Count - 1;
                        while (next > anchor + 1 && !canTravel(path[anchor], path[next])) --next;
                        if (!canTravel(path[anchor], path[next])) return false;
                        if (next < path.Count - 1) pulled.Add(path[next]);
                        anchor = next;
                    }
                    result = pulled.ToArray();
                    return true;
                }
                int cx = current % columns, cy = current / columns;
                for (int dy = -1; dy <= 1; ++dy)
                    for (int dx = -1; dx <= 1; ++dx)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int x = cx + dx, y = cy + dy;
                        if (!InBounds(x, y)) continue;
                        int next = x + y * columns;
                        if (state[next] == 3 || !Clear(next)) continue;
                        // No diagonal corner cutting between two closed cells.
                        if (dx != 0 && dy != 0 && (!Clear(x + cy * columns) || !Clear(cx + y * columns))) continue;
                        float cost = costs[current] + cell * (dx == 0 || dy == 0 ? 1f : 1.41421356f);
                        if (cost >= costs[next] || !canTravel(point, Point(next))) continue;
                        costs[next] = cost;
                        parent[next] = current;
                        open.Enqueue(next, (cost + Point(next).Distance(to), next));
                    }
            }
            return false;

            bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < columns && y < rows;
            Vector2 Point(int index) => min + new Vector2(index % columns * cell, index / columns * cell);
            bool Clear(int index)
            {
                if (state[index] == 0) state[index] = canOccupy(Point(index)) ? (byte)1 : (byte)2;
                return state[index] != 2;
            }
        }
    }
}
