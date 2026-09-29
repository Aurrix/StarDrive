using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SDGraphics;
using SDUtils;
using Ship_Game.Data.Serialization;
using System.IO;
using System.Security.Cryptography;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UnitTests")]

namespace Ship_Game.Universe;

// Reserve one slot for each kind of work: repeated simulation rebuilds must not
// starve presentation. No queued jobs or waits on simulation/render threads.
internal static class BorderWorker
{
    static readonly SemaphoreSlim Slots = new(1);
    static readonly SemaphoreSlim VisualSlots = new(1);

    public static Task<T> TryRun<T>(Func<T> build)
        => TryRun(Slots, build);

    public static Task<T> TryRunVisual<T>(Func<T> build)
        => TryRun(VisualSlots, build);

    static Task<T> TryRun<T>(SemaphoreSlim slots, Func<T> build)
    {
        if (!slots.Wait(0)) return null;
        return Task.Run(() =>
        {
            try { return build(); }
            finally { slots.Release(); }
        });
    }
}

// CPU-only, immutable once published. Movement and rendering sample exactly the
// same field. All population-dependent widths are captured before starting work.
[StarDataType]
internal sealed class BorderField
{
    [StarData] public readonly Vector2 Origin;
    [StarData] public readonly float Cell;
    [StarData] public readonly int Columns, Rows;
    [StarData] readonly float[] Raw;
    [StarData] readonly float[] Smoothed;
    [StarDataConstructor] BorderField() { }
    byte[] CacheDigest;
    internal bool Valid => float.IsFinite(Cell) && Cell > 0 && Columns >= 0 && Rows >= 0
        && Columns <= 512 && Rows <= 512 && Raw?.Length == Columns*Rows && Smoothed?.Length == Columns*Rows;
    internal void WriteCacheKey(BinaryWriter w)
    {
        w.Write(Origin.X); w.Write(Origin.Y); w.Write(Cell); w.Write(Columns); w.Write(Rows);
        if (CacheDigest == null)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Raw.AsSpan()));
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Smoothed.AsSpan()));
            CacheDigest = hash.GetHashAndReset();
        }
        w.Write(CacheDigest);
    }
    public RectF Bounds => new(Origin.X, Origin.Y, (Columns - 1) * Cell, (Rows - 1) * Cell);

    public readonly struct Node
    {
        public readonly Vector2 Position;
        public readonly float Radius, Phase, Growth;
        public Node(Vector2 position, float radius, float phase, float growth)
        { Position = position; Radius = radius; Phase = phase; Growth = growth; }

        public float Influence(Vector2 point)
        {
            Vector2 offset = point - Position;
            float cutoff = Radius * (1f + Empire.BorderShapeMaxVariation) * 3f;
            float distance2 = offset.SqLen();
            if (distance2 >= cutoff * cutoff) return 0f;
            float angle = (float)Math.Atan2(offset.Y, offset.X);
            float radius = Math.Max(1f, Radius * (1f
                + 0.032f * (float)Math.Sin(angle * 2f + Phase)
                + 0.017f * (float)Math.Sin(angle * 3f - Phase * 0.73f)
                + 0.006f * (float)Math.Sin(angle * 5f + Phase * 1.37f)));
            float normalized2 = distance2 / (radius * radius);
            return normalized2 >= 9f ? 0f : (float)Math.Exp(-0.5f * normalized2);
        }
    }

    public readonly struct Bridge
    {
        readonly Vector2 A, AB;
        readonly float InverseLength2, Radius1, Radius2;
        public readonly Vector2 Min, Max;
        public Bridge(Node a, Node b)
        {
            A = a.Position;
            AB = b.Position - A;
            InverseLength2 = AB.SqLen() < 1f ? 0f : 1f / AB.SqLen();
            float width = 1.02f + Math.Min(a.Growth, b.Growth) * 0.03f;
            Radius1 = a.Radius * width;
            Radius2 = b.Radius * width;
            float support = Math.Max(Radius1, Radius2) * 1.12f * 3f;
            Min = new(Math.Min(A.X, b.Position.X) - support, Math.Min(A.Y, b.Position.Y) - support);
            Max = new(Math.Max(A.X, b.Position.X) + support, Math.Max(A.Y, b.Position.Y) + support);
        }
        public float Influence(Vector2 point)
        {
            float t = ((point - A).Dot(AB) * InverseLength2).Clamped(0f, 1f);
            float radius = Math.Max(1f, (Radius1 + (Radius2 - Radius1) * t)
                * (1f + 0.12f * (float)Math.Sin(Math.PI * t)));
            float normalized2 = point.SqDist(A + AB * t) / (radius * radius);
            return normalized2 >= 9f ? 0f : (float)Math.Exp(-0.5f * normalized2);
        }
    }

    public BorderField(Node[] nodes, Bridge[] bridges)
    {
        if (nodes.Length == 0)
        {
            Cell = 1f; Columns = Rows = 0;
            Raw = Smoothed = System.Array.Empty<float>();
            return;
        }
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        float minRadius = float.MaxValue;
        foreach (Node n in nodes)
        {
            // Include the entire finite Gaussian support, even for dense groups.
            float support = n.Radius * (1f + Empire.BorderShapeMaxVariation) * 3f;
            Include(n.Position - new Vector2(support), n.Position + new Vector2(support));
            minRadius = Math.Min(minRadius, n.Radius);
        }
        foreach (Bridge b in bridges) Include(b.Min, b.Max);
        Cell = Math.Max(1000f, Math.Max(minRadius / 24f, Math.Max(max.X - min.X, max.Y - min.Y) / 384f));
        Origin = min - new Vector2(Cell * 3f);
        Columns = (int)Math.Ceiling((max.X - min.X) / Cell) + 7;
        Rows = (int)Math.Ceiling((max.Y - min.Y) / Cell) + 7;
        Raw = new float[Columns * Rows];
        foreach (Node node in nodes)
        {
            float support = node.Radius * (1f + Empire.BorderShapeMaxVariation) * 3f;
            Splat(node.Position - new Vector2(support), node.Position + new Vector2(support), node.Influence);
        }
        foreach (Bridge bridge in bridges) Splat(bridge.Min, bridge.Max, bridge.Influence);
        for (int i = 0; i < Raw.Length; ++i)
            Raw[i] = Empire.CombineGaussianInfluence(Raw[i]) - Empire.GaussianBorderThreshold;
        // Never close features wider than a small fraction of the smallest node.
        int closingRadius = Math.Min(2, (int)(minRadius * 0.12f / Cell));
        int maxHoleCells = Math.Min(64, (int)(Math.PI * Math.Pow(minRadius * 0.35f / Cell, 2)));
        Smoothed = CloseCavities(Raw, Columns, Rows, closingRadius, maxHoleCells);

        void Include(Vector2 a, Vector2 b)
        {
            min.X = Math.Min(min.X, a.X); min.Y = Math.Min(min.Y, a.Y);
            max.X = Math.Max(max.X, b.X); max.Y = Math.Max(max.Y, b.Y);
        }
    }

    // Rasterize only each primitive's support rectangle instead of evaluating
    // every node/bridge of every empire at every grid point.
    void Splat(Vector2 min, Vector2 max, Func<Vector2, float> influence)
    {
        int x0 = Math.Max(0, (int)Math.Floor((min.X - Origin.X) / Cell));
        int y0 = Math.Max(0, (int)Math.Floor((min.Y - Origin.Y) / Cell));
        int x1 = Math.Min(Columns - 1, (int)Math.Ceiling((max.X - Origin.X) / Cell));
        int y1 = Math.Min(Rows - 1, (int)Math.Ceiling((max.Y - Origin.Y) / Cell));
        for (int y = y0; y <= y1; ++y)
            for (int x = x0; x <= x1; ++x)
                Empire.AccumulateGaussianInfluence(ref Raw[x + y * Columns],
                    influence(Origin + new Vector2(x * Cell, y * Cell)));
    }

    public float Strength(Vector2 point, bool smooth = true)
    {
        float x = (point.X - Origin.X) / Cell, y = (point.Y - Origin.Y) / Cell;
        if (x < 0 || y < 0 || x >= Columns - 1 || y >= Rows - 1)
            return -Empire.GaussianBorderThreshold;
        int ix = (int)x, iy = (int)y, index = ix + iy * Columns;
        float tx = x - ix, ty = y - iy;
        float[] field = smooth ? Smoothed : Raw;
        float a = field[index] + (field[index + 1] - field[index]) * tx;
        float b = field[index + Columns] + (field[index + Columns + 1] - field[index + Columns]) * tx;
        return a + (b - a) * ty;
    }

    // Conservative closing followed by size-limited enclosed-hole filling.
    // Only adds claims; rival raw claims take precedence in BorderScene/Empire.
    internal static float[] CloseCavities(float[] raw, int columns, int rows, int radius, int maxHoleCells)
    {
        bool[] inside = new bool[raw.Length];
        for (int i = 0; i < raw.Length; ++i) inside[i] = raw[i] >= 0f;
        if (radius > 0)
        {
            bool[] dilated = new bool[raw.Length];
            for (int y = 0; y < rows; ++y)
                for (int x = 0; x < columns; ++x)
                    if (inside[x + y * columns])
                        for (int dy = -radius; dy <= radius; ++dy)
                            for (int dx = -radius; dx <= radius; ++dx)
                                if (dx * dx + dy * dy <= radius * radius
                                    && x + dx >= 0 && x + dx < columns && y + dy >= 0 && y + dy < rows)
                                    dilated[x + dx + (y + dy) * columns] = true;
            for (int y = radius; y < rows - radius; ++y)
                for (int x = radius; x < columns - radius; ++x)
                {
                    bool closed = true;
                    for (int dy = -radius; dy <= radius && closed; ++dy)
                        for (int dx = -radius; dx <= radius; ++dx)
                            if (dx * dx + dy * dy <= radius * radius && !dilated[x + dx + (y + dy) * columns])
                            { closed = false; break; }
                    inside[x + y * columns] |= closed;
                }
        }
        bool[] visited = new bool[raw.Length];
        int[] queue = new int[raw.Length];
        for (int start = 0; start < raw.Length; ++start)
        {
            if (inside[start] || visited[start]) continue;
            int count = 1; queue[0] = start; visited[start] = true;
            bool exterior = false;
            for (int head = 0; head < count; ++head)
            {
                int index = queue[head], x = index % columns, y = index / columns;
                exterior |= x == 0 || y == 0 || x == columns - 1 || y == rows - 1;
                // Eight neighbours preserve diagonal passages to the exterior.
                for (int dy = -1; dy <= 1; ++dy)
                    for (int dx = -1; dx <= 1; ++dx)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= columns || ny < 0 || ny >= rows) continue;
                        int next = nx + ny * columns;
                        if (inside[next] || visited[next]) continue;
                        visited[next] = true; queue[count++] = next;
                    }
            }
            if (!exterior && count <= maxHoleCells)
                for (int i = 0; i < count; ++i) inside[queue[i]] = true;
        }
        var result = (float[])raw.Clone();
        for (int i = 0; i < result.Length; ++i)
            if (inside[i] && result[i] < 0f) result[i] = 0.015f;
        return result;
    }
}

[StarDataType]
internal sealed class SavedBorderGeometry
{
    internal const int CurrentVersion = 2;
    [StarData] public int Version;
    [StarData] public byte[] Inputs;
    [StarData] public BorderField Field, KnownField;
    internal bool Matches(byte[] inputs) => Version == CurrentVersion && Inputs != null
        && inputs.AsSpan().SequenceEqual(Inputs) && Field?.Valid == true && KnownField?.Valid == true;
    internal static byte[] Key(Empire.InfluenceNode[] nodes, BorderField.Node[] samples, float radius)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(CurrentVersion); w.Write(radius); w.Write(nodes.Length);
        float quantum = Math.Max(1f, radius * 0.01f);
        for (int i = 0; i < nodes.Length; ++i)
        {
            w.Write(nodes[i].Source.Id); w.Write(nodes[i].KnownToPlayer);
            w.Write((int)(samples[i].Position.X / quantum)); w.Write((int)(samples[i].Position.Y / quantum));
            w.Write((int)(samples[i].Radius / quantum)); w.Write(samples[i].Phase);
            w.Write((int)(samples[i].Growth * 100));
        }
        return SHA256.HashData(stream.ToArray());
    }
}

// Captured on the simulation thread; completed by the save worker while gameplay
// is paused. The serialized fields and overview always describe the same scene.
internal sealed class BorderSavePreparation
{
    readonly Empire[] Empires;
    readonly (Empire.InfluenceNode[] Nodes, BorderField.Node[] Samples, float Radius)[] Inputs;
    readonly SavedBorderGeometry[] Previous;
    readonly BorderScene Scene;
    readonly SavedBorderOverview PreviousOverview;
    readonly float Radius;
    readonly List<BorderVisualTile.Key> DetailKeys;

    internal BorderSavePreparation(UniverseState state)
    {
        Empires = state.Empires.ToArr();
        Radius = state.Size;
        PreviousOverview = state.BorderOverviewCache;
        var view = state.Screen.ExactVisibleWorldRect;
        if (view.Width > 0 && view.Height > 0 && state.Screen.ScreenWidth > 0)
        {
            float pixel = (float)view.Width/state.Screen.ScreenWidth;
            int level = BorderVisualTile.ChooseLevel(pixel);
            DetailKeys = BorderVisualRenderer.Keys(new(view.X1,view.Y1,view.Width,view.Height),level,128);
        }
        Inputs = new (Empire.InfluenceNode[], BorderField.Node[], float)[Empires.Length];
        Previous = new SavedBorderGeometry[Empires.Length];
        var placeholders = new BorderSnapshot[Empires.Length];
        for (int i = 0; i < Empires.Length; ++i)
        {
            Inputs[i] = Empires[i].CaptureBorderSaveInputs();
            Previous[i] = Empires[i].SavedBorderCache;
            placeholders[i] = new(Inputs[i].Nodes, Inputs[i].Radius);
        }
        Scene = new BorderScene(Empires, placeholders);
    }

    internal SavedBorderOverview Complete()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Log.Info($"Preparing border save: {Empires.Length} empires, {DetailKeys?.Count ?? 0} visible tiles");
        for (int i = 0; i < Empires.Length; ++i)
        {
            var input = Inputs[i];
            var snapshot = new BorderSnapshot(input.Nodes, input.Samples, input.Radius, Previous[i]);
            Scene.Empires[i].Snapshot = snapshot;
            Empires[i].BorderGeometryForSave = snapshot.SaveCache;
        }
        byte[] key = Scene.SaveKey();
        bool reuse = PreviousOverview?.Version == SavedBorderOverview.CurrentVersion && PreviousOverview.Radius == Radius
            && PreviousOverview.SceneKey != null && key.AsSpan().SequenceEqual(PreviousOverview.SceneKey);
        int level = BorderVisualTile.ChooseLevel(Radius*2/450);
        var keys = BorderVisualRenderer.Keys(new(-Radius,-Radius,Radius*2,Radius*2),level);
        var tiles = reuse ? PreviousOverview.Tiles : new byte[keys.Count][];
        var scratch = new BorderVisualTile.Scratch();
        if (!reuse)
            for (int i = 0; i < tiles.Length; ++i) tiles[i] = new BorderVisualTile(Scene,keys[i],scratch).Save();
        var detail = new List<byte[]>();
        if (DetailKeys != null)
            foreach (var address in DetailKeys)
                if (!keys.Contains(address)) detail.Add(new BorderVisualTile(Scene,address,scratch).Save());
        Log.Info($"Border save prepared in {timer.ElapsedMilliseconds}ms: {tiles.Length} overview, {detail.Count} detail tiles");
        return new() { Version = SavedBorderOverview.CurrentVersion, Radius = Radius, SceneKey = key,
            Tiles = tiles, DetailTiles = detail.ToArray() };
    }

    internal void Release()
    {
        foreach (Empire empire in Empires) empire.BorderGeometryForSave = null;
    }
}

internal sealed class BorderSnapshot
{
    public readonly SavedBorderGeometry SaveCache;
    public readonly Empire.InfluenceNode[] Nodes;
    public readonly InfluenceConnection[] Connections;
    public readonly BorderField Field, KnownField;
    public readonly float ProjectorRadius;

    // Placeholder used only while capturing save inputs on the simulation thread.
    internal BorderSnapshot(Empire.InfluenceNode[] nodes, float radius)
    {
        Nodes = nodes; ProjectorRadius = radius;
        Connections = System.Array.Empty<InfluenceConnection>();
    }

    public BorderSnapshot(Empire.InfluenceNode[] nodes, BorderField.Node[] samples, float projectorRadius,
                          SavedBorderGeometry saved = null)
    {
        Nodes = nodes;
        ProjectorRadius = projectorRadius;
        var connections = new HashSet<InfluenceConnection>();
        BorderNodeCache.BuildConnections(projectorRadius, nodes, false, connections);
        Connections = new InfluenceConnection[connections.Count];
        connections.CopyTo(Connections);
        byte[] inputs = SavedBorderGeometry.Key(nodes, samples, projectorRadius);
        if (saved?.Matches(inputs) == true)
        {
            Field = saved.Field; KnownField = saved.KnownField; SaveCache = saved;
            return;
        }
        var indices = new Dictionary<GameObject, int>();
        for (int i = 0; i < nodes.Length; ++i) indices[nodes[i].Source] = i;
        var bridges = new BorderField.Bridge[Connections.Length];
        for (int i = 0; i < bridges.Length; ++i)
            bridges[i] = new(samples[indices[Connections[i].Node1.Source]], samples[indices[Connections[i].Node2.Source]]);
        Field = new(samples, bridges);
        var knownNodes = new List<BorderField.Node>();
        var knownBridges = new List<BorderField.Bridge>();
        for (int i = 0; i < nodes.Length; ++i)
            if (nodes[i].KnownToPlayer) knownNodes.Add(samples[i]);
        for (int i = 0; i < bridges.Length; ++i)
            if (Connections[i].Node1.KnownToPlayer && Connections[i].Node2.KnownToPlayer) knownBridges.Add(bridges[i]);
        KnownField = knownNodes.Count == nodes.Length ? Field : new(knownNodes.ToArray(), knownBridges.ToArray());
        SaveCache = new() { Version = SavedBorderGeometry.CurrentVersion, Inputs = inputs, Field = Field, KnownField = KnownField };
    }
}

// Captured on the simulation thread after all empire updates have joined. Worker
// code never walks live planet lists, relations, or mutable empire collections.
internal sealed class BorderScene
{
    internal sealed class Entry
    {
        public Empire Owner; // identity only, for rendering labels
        public int Id;
        public BorderSnapshot Snapshot;
        public Empire.InfluenceNode[] CurrentNodes;
        public int[] ColonyIds;
        public bool Active;
        public bool Known;
        public string Name;
        public Microsoft.Xna.Framework.Color Color;
    }
    public readonly Entry[] Empires;
    readonly bool[,] Overlap;
    readonly BorderField.Node[][] SharedSystems;
    public readonly int Signature;
    public readonly bool RevealAll;
    byte[] SavedKey;
    internal byte[] SaveKey()
    {
        if (SavedKey != null) return SavedKey;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(SavedBorderGeometry.CurrentVersion); w.Write(RevealAll); w.Write(Empires.Length);
        foreach (Entry e in Empires)
        {
            w.Write(e.Id); w.Write(e.Active); w.Write(e.Known); w.Write(e.Color.PackedValue);
            bool hasField = e.Active && e.Snapshot != null;
            w.Write(hasField);
            if (hasField)
            {
                e.Snapshot.Field.WriteCacheKey(w);
                e.Snapshot.KnownField.WriteCacheKey(w);
            }
        }
        foreach (bool overlap in Overlap) w.Write(overlap);
        foreach (var nodes in SharedSystems)
        {
            w.Write(nodes?.Length ?? 0);
            if (nodes == null) continue;
            foreach (var n in nodes)
            {
                w.Write(n.Position.X); w.Write(n.Position.Y); w.Write(n.Radius); w.Write(n.Phase); w.Write(n.Growth);
            }
        }
        return SavedKey = SHA256.HashData(stream.ToArray());
    }
    static long NextGeneration;
    public readonly long Generation = Interlocked.Increment(ref NextGeneration);

    public static BorderScene Capture(BorderScene previous, Empire[] empires)
    {
        var candidate = new BorderScene(empires);
        // Hashes accelerate comparisons but never determine correctness alone.
        return previous != null && previous.Signature == candidate.Signature
            && candidate.SameInputs(previous) ? previous : candidate;
    }

    bool SameInputs(BorderScene other)
    {
        if (RevealAll != other.RevealAll) return false;
        if (Empires.Length != other.Empires.Length) return false;
        for (int i = 0; i < Empires.Length; ++i)
        {
            Entry a = Empires[i], b = other.Empires[i];
            if (a.Id != b.Id || a.Snapshot != b.Snapshot || a.Active != b.Active
                || a.Known != b.Known || a.Name != b.Name || a.Color != b.Color) return false;
            if (!a.ColonyIds.AsSpan().SequenceEqual(b.ColonyIds)) return false;
            for (int j = 0; j < Empires.Length; ++j)
                if (!SameOverlap(other, i, j)) return false;
        }
        return true;
    }

    bool SameOverlap(BorderScene other, int i, int j)
    {
        if (Overlap[i,j] != other.Overlap[i,j]) return false;
        BorderField.Node[] a = SharedSystems[i*Empires.Length+j], b = other.SharedSystems[i*Empires.Length+j];
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        for (int k = 0; k < a.Length; ++k)
            if (a[k].Position != b[k].Position || a[k].Radius != b[k].Radius
                || a[k].Phase != b[k].Phase || a[k].Growth != b[k].Growth) return false;
        return true;
    }

    internal bool CanReuseTile(BorderScene previous, RectF bounds)
    {
        if (RevealAll != previous.RevealAll) return false;
        if (Empires.Length != previous.Empires.Length) return false;
        bool Touches(Entry e)
        {
            if (e.Snapshot == null) return false;
            RectF b = e.Snapshot.Field.Bounds;
            return b.Right >= bounds.Left && b.Left <= bounds.Right && b.Bottom >= bounds.Top && b.Top <= bounds.Bottom;
        }
        for (int i = 0; i < Empires.Length; ++i)
        {
            Entry a = Empires[i], b = previous.Empires[i];
            if (a.Id != b.Id || a.Color != b.Color || a.Name != b.Name) return false;
            if ((a.Snapshot != b.Snapshot || a.Active != b.Active || a.Known != b.Known) && (Touches(a) || Touches(b))) return false;
            for (int j = 0; j < Empires.Length; ++j)
                if (!SameOverlap(previous,i,j) && (Touches(a) || Touches(b)
                    || Touches(Empires[j]) || Touches(previous.Empires[j]))) return false;
        }
        return true;
    }

    internal bool NeedsImmediateRefresh(BorderScene previous)
    {
        if (RevealAll != previous.RevealAll || Empires.Length != previous.Empires.Length) return true;
        for (int i = 0; i < Empires.Length; ++i)
        {
            Entry a = Empires[i], b = previous.Empires[i];
            if (a.Id != b.Id || a.Active != b.Active || a.Known != b.Known
                || !a.ColonyIds.AsSpan().SequenceEqual(b.ColonyIds)) return true;
            var nodes = a.Snapshot?.Nodes;
            var old = b.Snapshot?.Nodes;
            if (nodes == null || old == null) { if (nodes != old) return true; continue; }
            if (nodes.Length != old.Length) return true;
            for (int n = 0; n < nodes.Length; ++n)
                if (nodes[n].Source != old[n].Source || nodes[n].KnownToPlayer != old[n].KnownToPlayer) return true;
        }
        return false;
    }

    internal bool SameGeometry(BorderScene other)
    {
        if (NeedsImmediateRefresh(other)) return false;
        if (RevealAll != other.RevealAll) return false;
        if (Empires.Length != other.Empires.Length) return false;
        for (int i = 0; i < Empires.Length; ++i)
        {
            Entry a = Empires[i], b = other.Empires[i];
            if (a.Id != b.Id || a.Snapshot != b.Snapshot || a.Active != b.Active || a.Known != b.Known) return false;
            for (int j = 0; j < Empires.Length; ++j) if (!SameOverlap(other,i,j)) return false;
        }
        return true;
    }

    internal bool KnowledgeRemovedSince(BorderScene previous)
    {
        if (previous.RevealAll && !RevealAll) return true;
        if (RevealAll) return false;
        // Do not keep a stale visible generation after knowledge is withdrawn.
        foreach (Entry old in previous.Empires)
        {
            if (!old.Known || old.Snapshot == null) continue;
            Entry current = Array.Find(Empires, e => e.Id == old.Id);
            // Removal/defeat is an ownership update, not newly secret information.
            // Keep the last complete overlay while replacement fields are built.
            if (current == null) continue;
            if (!current.Known) return true;
            foreach (Empire.InfluenceNode node in old.Snapshot.Nodes)
            {
                if (!node.KnownToPlayer) continue;
                foreach (Empire.InfluenceNode next in current.CurrentNodes)
                    // Growth, shrinkage and movement invalidate geometry, not
                    // knowledge. Hiding the whole overlay for a shrinking colony
                    // would keep it blank while replacement snapshots catch up.
                    if (next.Source == node.Source && !next.KnownToPlayer) return true;
            }
        }
        return false;
    }

    static int GetSignature(Empire[] empires)
    {
        var hash = new HashCode();
        foreach (Empire owner in empires)
        {
            BorderSnapshot snapshot = owner.PreparedBorders;
            hash.Add(owner.Id); hash.Add(snapshot);
            hash.Add(owner.Universe.Debug);
            hash.Add(owner.IsDefeated); hash.Add(owner.InfluenceActive);
            hash.Add(owner.WeArePirates); hash.Add(owner.WeAreRemnants);
            hash.Add(owner.Universe.Player.IsKnown(owner) || owner.isPlayer);
            hash.Add(owner.Name); hash.Add(owner.EmpireColor);
            foreach (Planet colony in owner.GetPlanets()) hash.Add(colony.Id);
            foreach (Empire rival in empires)
                if (owner != rival) hash.Add(owner.IsAtWarWith(rival));
            if (snapshot == null) continue;
            foreach (Empire.InfluenceNode node in snapshot.Nodes)
                if (node.Source is SolarSystem system)
                    for (int p = 0; p < system.PlanetList.Count; ++p)
                        hash.Add(system.PlanetList[p].Owner?.Id ?? 0);
        }
        return hash.ToHashCode();
    }

    public BorderScene(Empire[] empires, BorderSnapshot[] snapshots = null)
    {
        RevealAll = empires.Length > 0 && empires[0].Universe.Debug;
        int count = empires.Length;
        Empires = new Entry[count];
        Overlap = new bool[count, count];
        SharedSystems = new BorderField.Node[count * count][];
        for (int i = 0; i < count; ++i)
        {
            Empire owner = empires[i];
            var colonies = owner.GetPlanets();
            var colonyIds = new int[colonies.Count];
            for (int p = 0; p < colonies.Count; ++p) colonyIds[p] = colonies[p].Id;
            BorderSnapshot snapshot = snapshots == null ? owner.PreparedBorders : snapshots[i];
            Empires[i] = new Entry { Owner = owner, Id = owner.Id, Snapshot = snapshot,
                CurrentNodes = snapshot?.Nodes ?? owner.BorderNodes, ColonyIds = colonyIds,
                Active = !owner.IsDefeated && owner.InfluenceActive,
                Known = owner.isPlayer || owner.Universe.Player.IsKnown(owner),
                Name = owner.Name, Color = owner.EmpireColor };
        }
        for (int i = 0; i < count; ++i)
            for (int j = 0; j < count; ++j)
            {
                Empire a = empires[i], b = empires[j];
                Overlap[i, j] = a != b && (a.WeArePirates || b.WeArePirates || a.WeAreRemnants || b.WeAreRemnants || a.IsAtWarWith(b));
                if (i == j || Overlap[i, j] || !Empires[i].Active || !Empires[j].Active || Empires[i].Snapshot == null) continue;
                var shared = new List<BorderField.Node>();
                foreach (Empire.InfluenceNode node in Empires[i].Snapshot.Nodes)
                {
                    if (node.Source is not SolarSystem system) continue;
                    bool ours = false, theirs = false;
                    for (int p = 0; p < system.PlanetList.Count; ++p)
                    {
                        Empire owner = system.PlanetList[p].Owner;
                        ours |= owner == a; theirs |= owner == b;
                    }
                    if (!ours || !theirs) continue;
                    shared.Add(new(node.Position, node.Radius, node.Source.Id * 0.754877666f + a.Id * 1.618033989f, 0f));
                }
                SharedSystems[i * count + j] = shared.ToArray();
            }
        Signature = GetSignature(empires);
    }

    BorderScene(BorderScene source, bool revealAll)
    {
        Empires = source.Empires;
        Overlap = source.Overlap;
        SharedSystems = source.SharedSystems;
        RevealAll = revealAll;
        Signature = HashCode.Combine(source.Signature,revealAll);
    }

    // Debug visibility can change while paused. Reuse captured immutable inputs
    // rather than walking live empires from the graphics thread.
    internal BorderScene WithDebugVisibility(bool revealAll)
        => RevealAll == revealAll ? this : new BorderScene(this,revealAll);

    bool Overlaps(int a, int b, Vector2 point)
    {
        if (Overlap[a, b]) return true;
        BorderField.Node[] shared = SharedSystems[a * Empires.Length + b];
        if (shared != null)
            foreach (BorderField.Node node in shared)
                if (node.Influence(point) >= Empire.GaussianBorderThreshold) return true;
        return false;
    }

    float Strength(int owner, Vector2 point)
    {
        BorderField field = Empires[owner].Snapshot.Field;
        float raw = field.Strength(point, smooth: false), smooth = field.Strength(point);
        if (raw < 0f && smooth >= 0f)
            for (int i = 0; i < Empires.Length; ++i)
                if (i != owner && Empires[i].Active && Empires[i].Snapshot != null
                    && Empires[i].Snapshot.Field.Strength(point, smooth: false) >= 0f && !Overlaps(owner, i, point))
                    return raw;
        return smooth;
    }

    public float Territory(int owner, Vector2 point, out bool occupied)
    {
        occupied = false;
        Entry ours = Empires[owner];
        float scale = ours.Snapshot.ProjectorRadius * 2f;
        float visible = ours.Snapshot.KnownField.Strength(point);
        if (visible < 0f) return visible * scale;
        float strength = Strength(owner, point);
        float result = Math.Min(visible, strength);
        for (int i = 0; i < Empires.Length; ++i)
        {
            Entry rival = Empires[i];
            if (i == owner || !rival.Active || rival.Snapshot == null) continue;
            float other = Strength(i, point);
            if (other >= 0f && Overlaps(owner, i, point))
            {
                occupied |= ours.Id < rival.Id;
                continue;
            }
            // Continuous competition field gives marching squares a real shared
            // frontier instead of a binary sign flip of our entire claim.
            float advantage = strength - other + (ours.Id < rival.Id ? 0.001f : -0.001f);
            result = Math.Min(result, advantage);
        }
        occupied &= result >= 0f;
        return result * scale;
    }

    // Caller-owned scratch arrays avoid allocations for every raster sample.
    // Competition includes unknown empires; knowledge only filters the result.
    internal int Classify(Vector2 point, float[] strengths, int[] winners)
    {
        for (int i = 0; i < Empires.Length; ++i)
            strengths[i] = Empires[i].Active && Empires[i].Snapshot != null
                ? Strength(i, point) : float.NegativeInfinity;
        int count = 0;
        for (int a = 0; a < Empires.Length; ++a)
        {
            Entry owner = Empires[a];
            if (strengths[a] < 0) continue;
            bool rejected = false;
            for (int b = 0; b < Empires.Length; ++b)
            {
                if (a == b || strengths[b] == float.NegativeInfinity) continue;
                if (strengths[b] >= 0 && Overlaps(a, b, point)) continue;
                float difference = strengths[b] - strengths[a];
                if (difference > 0.001f || Math.Abs(difference) <= 0.001f && Empires[b].Id < owner.Id)
                { rejected = true; break; }
            }
            if (!rejected && (RevealAll || owner.Known && owner.Snapshot.KnownField.Strength(point) >= 0))
                winners[count++] = a;
        }
        // IDs, rather than empire enumeration or military power, anchor stripes.
        for (int i = 1; i < count; ++i)
        {
            int value = winners[i], j = i - 1;
            while (j >= 0 && Empires[winners[j]].Id > Empires[value].Id)
            { winners[j + 1] = winners[j]; --j; }
            winners[j + 1] = value;
        }
        return count;
    }
}
