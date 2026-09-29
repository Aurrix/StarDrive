using System;
using System.Collections.Generic;
using SDGraphics;
using Color = Microsoft.Xna.Framework.Color;
using Vector4 = Microsoft.Xna.Framework.Vector4;
using System.IO;
using System.IO.Compression;
using Ship_Game.Data.Serialization;

namespace Ship_Game.Universe;

[StarDataType]
internal sealed class SavedBorderOverview
{
    [StarData] public int Version;
    [StarData] public float Radius;
    [StarData] public byte[] SceneKey;
    [StarData] public byte[][] Tiles;
    internal const int CurrentVersion = 1;
}

// CPU-only immutable products. No graphics device or live empire is accessed here.
internal sealed class BorderClaimSet : IEquatable<BorderClaimSet>
{
    public readonly int[] Members;
    readonly int Hash;
    public BorderClaimSet(int[] members, int count)
    {
        Members = new int[count];
        Array.Copy(members, Members, count);
        var hash = new HashCode();
        foreach (int id in Members) hash.Add(id);
        Hash = hash.ToHashCode();
    }
    public bool Equals(BorderClaimSet other) => other != null && Members.AsSpan().SequenceEqual(other.Members);
    public override bool Equals(object obj) => obj is BorderClaimSet other && Equals(other);
    public override int GetHashCode() => Hash;
}

internal sealed class BorderVisualTile
{
    // A renderer has only one CPU job in flight. Reuse its large distance-transform
    // buffers across tiles; none of these arrays escape into the finished tile.
    internal sealed class Scratch
    {
        internal const int Width = Size * 2 + 1;
        internal readonly float[] Distance = new float[Width * Width];
        internal readonly int[] Seeds = new int[Width * Width];
        internal readonly float[] Input = new float[Width], Intersections = new float[Width + 1];
        internal readonly int[] InputSeeds = new int[Width], Sites = new int[Width];
    }
    public const int Interior = 256, Gutter = 32, Size = Interior + Gutter * 2;
    public const int PaletteWidth = 1024;
    public readonly record struct Key(int Level, int X, int Y)
    {
        public float Cell => 1000f * MathF.Pow(2, Level / 4f);
        public RectF Bounds => new(X * Interior * Cell, Y * Interior * Cell, Interior * Cell, Interior * Cell);
    }
    public readonly Key Address;
    internal byte[] SavedPixels;
    public readonly Color[] Territory = new Color[Size * Size];
    public Color[] Neighbor = new Color[Size * Size];
    public Vector4[] Metadata, Colors;
    public readonly BorderClaimSet[] Regions;
    public int[] Labels;
    public readonly record struct Edge(int X, int Y, bool Vertical, int A, int B, float Offset = 0)
    {
        public float Distance(float x, float y)
        {
            float dx = Vertical ? x - (X + 1 + Offset) : Math.Max(0, Math.Abs(x - (X + 0.5f)) - 0.5f);
            float dy = Vertical ? Math.Max(0, Math.Abs(y - (Y + 0.5f)) - 0.5f) : y - (Y + 1 + Offset);
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }
    public static Color Encode(int id, byte value) => new((byte)id, (byte)(id >> 8), (byte)(id >> 16), value);
    public static int Decode(Color value) => value.R | value.G << 8 | value.B << 16;
    public static int TileCoordinate(float world, float cell) => (int)MathF.Floor(world / (Interior * cell));
    public static int ChooseLevel(float worldPerPixel) => (int)MathF.Round(4 * MathF.Log2(worldPerPixel * 0.9f / 1000));

    internal byte[] Save()
    {
        using var stream = new MemoryStream();
        using (var zip = new DeflateStream(stream, CompressionLevel.Fastest, true))
        using (var w = new BinaryWriter(zip))
        {
            w.Write(Address.Level); w.Write(Address.X); w.Write(Address.Y);
            foreach (Color c in Territory) w.Write(c.PackedValue);
            foreach (Color c in Neighbor) w.Write(c.PackedValue);
            Write(Metadata); Write(Colors);
            w.Write(Regions.Length);
            foreach (var region in Regions)
            {
                w.Write(region.Members.Length);
                foreach (int member in region.Members) w.Write(member);
            }
            void Write(Vector4[] values)
            {
                w.Write(values.Length);
                foreach (var v in values) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
            }
        }
        return stream.ToArray();
    }

    internal BorderVisualTile(byte[] saved, int empireCount)
    {
        SavedPixels = saved;
        using var stream = new MemoryStream(saved);
        using var zip = new DeflateStream(stream, CompressionMode.Decompress);
        using var r = new BinaryReader(zip);
        Address = new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        for (int i = 0; i < Territory.Length; ++i) Territory[i] = new Color { PackedValue = r.ReadUInt32() };
        for (int i = 0; i < Neighbor.Length; ++i) Neighbor[i] = new Color { PackedValue = r.ReadUInt32() };
        Metadata = Read(); Colors = Read();
        int count = Count(Size*Size + 1);
        if (count == 0) throw new InvalidDataException("Missing border regions");
        Regions = new BorderClaimSet[count];
        for (int i = 0; i < count; ++i)
        {
            var members = new int[Count(empireCount)];
            for (int j = 0; j < members.Length; ++j)
            {
                members[j] = r.ReadInt32();
                if (members[j] < 0 || members[j] >= empireCount) throw new InvalidDataException("Invalid border claimant");
            }
            Regions[i] = new(members, members.Length);
        }
        int Count(int max)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max) throw new InvalidDataException("Invalid border cache array");
            return n;
        }
        Vector4[] Read()
        {
            int n = Count(4*1024*1024);
            if (n == 0 || n % PaletteWidth != 0) throw new InvalidDataException("Invalid border palette");
            var values = new Vector4[n];
            for (int i = 0; i < n; ++i) values[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            return values;
        }
    }

    public BorderVisualTile(BorderScene scene, Key key, Scratch scratch = null)
    {
        Address = key;
        Labels = new int[Size * Size];
        var sets = new List<BorderClaimSet> { new(Array.Empty<int>(), 0) };
        var lookup = new Dictionary<int, List<int>>();
        var strengths = new float[scene.Empires.Length];
        var winners = new int[scene.Empires.Length];
        float cell = key.Cell;
        int startX = key.X * Interior - Gutter, startY = key.Y * Interior - Gutter;
        for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                int count = scene.Classify(new((startX + x + 0.5f) * cell, (startY + y + 0.5f) * cell), strengths, winners);
                if (count == 0) continue;
                var hash = new HashCode();
                for (int i = 0; i < count; ++i) hash.Add(winners[i]);
                int h = hash.ToHashCode(), id = 0;
                if (!lookup.TryGetValue(h, out List<int> bucket)) lookup.Add(h, bucket = new());
                foreach (int candidate in bucket)
                    if (winners.AsSpan(0, count).SequenceEqual(sets[candidate].Members)) { id = candidate; break; }
                if (id == 0)
                {
                    id = sets.Count;
                    sets.Add(new(winners, count));
                    bucket.Add(id);
                }
                Labels[y * Size + x] = id;
            }
        Regions = sets.ToArray();

        // Canonical right/down edges only. Search bounded spatial buckets for the
        // exact nearest incident segment, including at three-way junctions. This
        // avoids propagating a non-incident frontier through a narrow enclave.
        const int bucketSize = 8, buckets = Size / bucketSize;
        var edges = new List<Edge>[buckets * buckets];
        var allEdges = new List<Edge>();
        void Add(Edge edge)
        {
            // Locate the actual classifier transition between sample centers.
            // Keeping every edge at the cell midpoint quantizes smooth gameplay
            // contours into stair steps, especially while magnifying cached LODs.
            float low = 0, high = 1;
            for (int step = 0; step < 7; ++step)
            {
                float t = (low+high)*0.5f;
                var point = new Vector2((startX+edge.X+0.5f+(edge.Vertical?t:0))*cell,
                                        (startY+edge.Y+0.5f+(edge.Vertical?0:t))*cell);
                int count = scene.Classify(point,strengths,winners);
                if (winners.AsSpan(0,count).SequenceEqual(sets[edge.A].Members)) low = t;
                else high = t;
            }
            edge = edge with { Offset = (low+high)*0.5f-0.5f };
            int bucket = edge.Y / bucketSize * buckets + edge.X / bucketSize;
            (edges[bucket] ??= new()).Add(edge);
            allEdges.Add(edge);
        }
        for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                int id = Labels[y * Size + x];
                if (x + 1 < Size && id != Labels[y * Size + x + 1]) Add(new(x, y, true, id, Labels[y * Size + x + 1]));
                if (y + 1 < Size && id != Labels[(y + 1) * Size + x]) Add(new(x, y, false, id, Labels[(y + 1) * Size + x]));
            }
        int[] nearest = NearestEdges(allEdges, scratch ?? new Scratch());
        const int lattice = Size * 2 + 1;
        for (int y = 0; y < Size; ++y)
            for (int x = 0; x < Size; ++x)
            {
                int p = y * Size + x, id = Labels[p], neighbor = 0;
                float distance = 16;
                int seed = nearest[(y * 2 + 1) * lattice + x * 2 + 1];
                bool incident = false;
                if (seed >= 0)
                {
                    Edge edge = allEdges[seed];
                    incident = edge.A == id || edge.B == id;
                    if (incident)
                    {
                        distance = Math.Min(16, edge.Distance(x + 0.5f, y + 0.5f));
                        neighbor = edge.A == id ? edge.B : edge.A;
                    }
                }
                // Midpoints identify a candidate; exact segment distances in the
                // nearby buckets resolve endpoint ties and non-incident junctions.
                int radius = (int)Math.Ceiling(distance) + 1;
                if (seed >= 0)
                for (int by = Math.Max(0, (y - radius) / bucketSize); by <= Math.Min(buckets - 1, (y + radius) / bucketSize); ++by)
                    for (int bx = Math.Max(0, (x - radius) / bucketSize); bx <= Math.Min(buckets - 1, (x + radius) / bucketSize); ++bx)
                    {
                        List<Edge> bucket = edges[by * buckets + bx];
                        if (bucket == null) continue;
                        foreach (Edge edge in bucket)
                        {
                            if (edge.A != id && edge.B != id) continue;
                            float d = edge.Distance(x + 0.5f, y + 0.5f);
                            if (d < distance) { distance = d; neighbor = edge.A == id ? edge.B : edge.A; }
                        }
                    }
                Territory[p] = Encode(id, (byte)Math.Clamp((int)MathF.Round(distance / 16 * 255), 0, 255));
                Neighbor[p] = Encode(neighbor, 0);
            }
        var palette = new List<Vector4> { Vector4.Zero };
        Metadata = new Vector4[((Regions.Length + PaletteWidth - 1) / PaletteWidth) * PaletteWidth];
        for (int r = 1; r < Regions.Length; ++r)
        {
            Metadata[r] = new(palette.Count, Regions[r].Members.Length, 0, 0);
            foreach (int member in Regions[r].Members) palette.Add(scene.Empires[member].Color.ToVector4());
        }
        Colors = new Vector4[((palette.Count + PaletteWidth - 1) / PaletteWidth) * PaletteWidth];
        palette.CopyTo(Colors);
    }

    // Separable squared Euclidean distance transform on the half-cell lattice.
    // Retain seed IDs through both passes, so distances refer to actual edges.
    static int[] NearestEdges(List<Edge> edges, Scratch scratch)
    {
        const int width = Size * 2 + 1;
        const float infinity = 1e12f;
        float[] distance = scratch.Distance;
        int[] seeds = scratch.Seeds;
        Array.Fill(distance, infinity);
        Array.Fill(seeds, -1);
        for (int i = 0; i < edges.Count; ++i)
        {
            Edge e = edges[i];
            int x = e.X * 2 + (e.Vertical ? 2 : 1), y = e.Y * 2 + (e.Vertical ? 1 : 2);
            distance[y * width + x] = 0;
            seeds[y * width + x] = i;
        }
        float[] input = scratch.Input, intersections = scratch.Intersections;
        int[] inputSeeds = scratch.InputSeeds, sites = scratch.Sites;
        for (int pass = 0; pass < 2; ++pass)
            for (int line = 0; line < width; ++line)
            {
                int Offset(int q) => pass == 0 ? line * width + q : q * width + line;
                for (int q = 0; q < width; ++q) { input[q] = distance[Offset(q)]; inputSeeds[q] = seeds[Offset(q)]; }
                int k = -1;
                for (int q = 0; q < width; ++q)
                {
                    if (inputSeeds[q] < 0) continue;
                    float crossing = float.NegativeInfinity;
                    while (k >= 0)
                    {
                        int v = sites[k];
                        crossing = ((input[q] + q*q) - (input[v] + v*v)) / (2*(q-v));
                        if (crossing > intersections[k]) break;
                        --k;
                    }
                    ++k; sites[k] = q;
                    intersections[k] = k == 0 ? float.NegativeInfinity : crossing;
                    intersections[k+1] = float.PositiveInfinity;
                }
                if (k < 0) continue;
                int site = 0;
                for (int q = 0; q < width; ++q)
                {
                    while (intersections[site+1] < q) ++site;
                    int v = sites[site], offset = Offset(q);
                    distance[offset] = (q-v)*(q-v) + input[v];
                    seeds[offset] = inputSeeds[v];
                }
            }
        return seeds;
    }

    public void ReleaseUploadData()
    {
        // Retain just categorical samples/distances and small claim sets for hover.
        Neighbor = null; Metadata = Colors = null; Labels = null;
    }
}
