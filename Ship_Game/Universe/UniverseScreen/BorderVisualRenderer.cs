using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;
using SDGraphics;
using SDGraphics.Shaders;
using SDGraphics.Sprites;
using SDGraphics.Rendering;
using Ship_Game.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;
using Key = Ship_Game.Universe.BorderVisualTile.Key;

namespace Ship_Game.Universe;

// Owned exclusively by the render thread. Tasks return CPU arrays; disposal never
// waits on them and they never own GPU resources or a UniverseScreen.
internal sealed class BorderVisualRenderer : IDisposable
{
    sealed class Tile : IDisposable
    {
        int References = 1;
        public readonly BorderVisualTile Data;
        public readonly Texture2D Territory, Neighbor, Metadata, Colors;
        public long Bytes => (long)Territory.Width*Territory.Height*8 + (long)(Metadata.Width*Metadata.Height+Colors.Width*Colors.Height)*16;
        public Tile(GraphicsDevice device, BorderVisualTile data)
        {
            Data = data;
            Territory = new(device, BorderVisualTile.Size, BorderVisualTile.Size);
            Neighbor = new(device, BorderVisualTile.Size, BorderVisualTile.Size);
            Metadata = new(device, BorderVisualTile.PaletteWidth, data.Metadata.Length / BorderVisualTile.PaletteWidth, false, SurfaceFormat.Vector4);
            Colors = new(device, BorderVisualTile.PaletteWidth, data.Colors.Length / BorderVisualTile.PaletteWidth, false, SurfaceFormat.Vector4);
            Territory.SetData(data.Territory); Neighbor.SetData(data.Neighbor);
            Metadata.SetData(data.Metadata); Colors.SetData(data.Colors);
            data.ReleaseUploadData();
        }
        public Tile Retain() { ++References; return this; }
        public void Recolor(BorderScene scene)
        {
            // The compressed palette belongs to the old scene. Never label it
            // with a new save key after recoloring only the GPU resource.
            Data.SavedPixels = null;
            var colors = new Microsoft.Xna.Framework.Vector4[Colors.Width*Colors.Height];
            int index = 1;
            for (int r = 1; r < Data.Regions.Length; ++r)
                foreach (int member in Data.Regions[r].Members) colors[index++] = scene.Empires[member].Color.ToVector4();
            Colors.SetData(colors);
        }
        public void Dispose() { if (--References == 0) { Territory.Dispose(); Neighbor.Dispose(); Metadata.Dispose(); Colors.Dispose(); } }
    }
    sealed class Generation : IDisposable
    {
        public BorderScene Scene;
        public readonly Dictionary<Key, Tile> Tiles = new();
        public readonly List<Key> Overview;
        public Generation(BorderScene scene, float radius, Generation previous = null)
        {
            Scene = scene;
            int level = BorderVisualTile.ChooseLevel(radius * 2 / 450);
            Overview = Keys(new(-radius, -radius, radius * 2, radius * 2), level);
            if (previous != null)
                foreach (var pair in previous.Tiles)
                {
                    RectF b = pair.Key.Bounds;
                    float gutter = pair.Key.Cell * BorderVisualTile.Gutter;
                    b = new(b.X-gutter,b.Y-gutter,b.W+gutter*2,b.H+gutter*2);
                    if (scene.CanReuseTile(previous.Scene,b)) Tiles.Add(pair.Key,pair.Value.Retain());
                }
        }
        public void Dispose() { foreach (Tile tile in Tiles.Values) tile.Dispose(); Tiles.Clear(); }
        public bool Complete(List<Key> keys) { foreach (Key key in keys) if (!Tiles.ContainsKey(key)) return false; return true; }
        public long Bytes { get { long bytes = 0; foreach (Tile tile in Tiles.Values) bytes += tile.Bytes; return bytes; } }
    }
    readonly GraphicsDevice Device;
    readonly SpriteShader Effect;
    readonly SpriteRenderer MiniRenderer;
    Generation Displayed, Building;
    Task<BorderVisualTile[]> Worker;
    BorderVisualTile.Scratch WorkerScratch;
    long NextSceneRefresh;
    internal const int SceneRefreshMilliseconds = 2000;
    readonly Queue<BorderVisualTile> Uploads = new();
    readonly Queue<Tile> PaletteUpdates = new();
    BorderScene PaletteScene;
    int? Level;
    int BudgetLevel = int.MinValue;
    int LastDrawLevel;
    float StripeCell, StripeFrom, StripeTarget;
    long StripeChanged;
    bool Disposed;
    bool HideStaleKnowledge;
    BorderScene LastKnowledgeChecked;
    const long GenerationBudget = 256L*1024*1024;
    public int JobsStarted { get; private set; }
    public int TilesUploaded { get; private set; }
    public long GpuBytes => (Displayed?.Bytes ?? 0) + (Building != Displayed ? Building?.Bytes ?? 0 : 0);
    public long ResidentCpuBytes => ((Displayed?.Tiles.Count ?? 0) + (Building != Displayed ? Building?.Tiles.Count ?? 0 : 0)) * 320L*320*4;
    public BorderScene DisplayedScene => Displayed?.Scene;
    BorderScene SavedOverviewScene;
    internal SavedBorderOverview SavedOverview { get; private set; }

    internal bool RestoreOverview(SavedBorderOverview saved, BorderScene scene, float radius)
    {
        if (Building != null || saved == null || saved.Version != SavedBorderOverview.CurrentVersion
            || saved.Radius != radius || saved.SceneKey == null
            || !saved.SceneKey.AsSpan().SequenceEqual(scene.SaveKey())) return false;
        var generation = new Generation(scene, radius);
        if (saved.Tiles?.Length != generation.Overview.Count) return false;
        var restored = new List<BorderVisualTile>();
        var keys = new HashSet<Key>();
        try
        {
            foreach (byte[] bytes in saved.Tiles)
            {
                var tile = new BorderVisualTile(bytes, scene.Empires.Length);
                if (!generation.Overview.Contains(tile.Address) || !keys.Add(tile.Address)) return false;
                restored.Add(tile);
            }
        }
        catch (Exception e) when (e is System.IO.IOException || e is ArgumentException)
        {
            Log.Warning("Ignoring invalid saved border overview: " + e.Message);
            return false;
        }
        Building = generation;
        foreach (var tile in restored) Uploads.Enqueue(tile);
        return true;
    }

    void CaptureOverview(float radius)
    {
        if (Displayed == null || SavedOverviewScene == Displayed.Scene) return;
        var tiles = new byte[Displayed.Overview.Count][];
        for (int i = 0; i < tiles.Length; ++i)
        {
            tiles[i] = Displayed.Tiles[Displayed.Overview[i]].Data.SavedPixels;
            if (tiles[i] == null) return;
        }
        SavedOverview = new() { Version = SavedBorderOverview.CurrentVersion, Radius = radius,
            SceneKey = Displayed.Scene.SaveKey(), Tiles = tiles };
        SavedOverviewScene = Displayed.Scene;
    }
    internal bool IsViewReady(RectF view, float worldPerPixel)
        => Displayed != null && Displayed.Complete(PrefetchKeys(view, Level ?? BorderVisualTile.ChooseLevel(worldPerPixel)));

    public BorderVisualRenderer(GraphicsDevice device)
    {
        Device = device;
        Effect = new(Shader.FromFile(device, ResourceManager.GetModOrVanillaFile("Effects/PoliticalBorders.mgfx").FullName));
        MiniRenderer = new(device);
    }
    internal static List<Key> Keys(RectF view, int level, int maxCount = int.MaxValue)
    {
        float cell = new Key(level, 0, 0).Cell;
        int x1 = BorderVisualTile.TileCoordinate(view.Left, cell), y1 = BorderVisualTile.TileCoordinate(view.Top, cell);
        int x2 = BorderVisualTile.TileCoordinate(view.Right, cell), y2 = BorderVisualTile.TileCoordinate(view.Bottom, cell);
        if (((long)x2-x1+1)*((long)y2-y1+1) > maxCount) return null;
        var keys = new List<Key>();
        for (int y = y1; y <= y2; ++y)
            for (int x = x1; x <= x2; ++x) keys.Add(new(level, x, y));
        return keys;
    }

    static List<Key> PrefetchKeys(RectF view, int level)
    {
        float margin = new Key(level,0,0).Cell*256;
        List<Key> keys = Keys(new(view.X-margin,view.Y-margin,view.W+margin*2,view.H+margin*2),level);
        return keys.Count <= 240 ? keys : Keys(view,level);
    }
    public void Update(BorderScene latest, float radius, RectF view, float worldPerPixel, bool overviewOnly = false)
    {
        if (Disposed || latest == null) return;
        if (!ReferenceEquals(latest, LastKnowledgeChecked))
        {
            HideStaleKnowledge = Displayed != null && latest.KnowledgeRemovedSince(Displayed.Scene);
            LastKnowledgeChecked = latest;
        }
        // Classification already skips missing fields. One empire waiting for
        // geometry must not block uploads or hide every ready empire's border.
        if (PaletteScene != null)
        {
            var paletteTimer = Stopwatch.StartNew();
            int bytes = 0;
            while (PaletteUpdates.Count > 0 && bytes < 8*1024*1024 && paletteTimer.Elapsed.TotalMilliseconds < 2)
            {
                Tile tile = PaletteUpdates.Dequeue();
                tile.Recolor(PaletteScene);
                bytes += tile.Colors.Width*tile.Colors.Height*16;
            }
            if (PaletteUpdates.Count > 0) return;
            Building.Scene = PaletteScene;
            PaletteScene = null;
        }
        if (Level == null || new Key(Level.Value,0,0).Cell / worldPerPixel is < 0.7f or > 1.2f)
            Level = Math.Max(BudgetLevel,BorderVisualTile.ChooseLevel(worldPerPixel));
        // Finish a useful generation before accepting the newest request: a busy
        // simulation cannot continuously cancel every build before publication.
        Building ??= new(latest, radius);
        if (Worker is { IsCompleted: true })
        {
            if (Worker.IsCompletedSuccessfully)
                foreach (BorderVisualTile tile in Worker.Result) Uploads.Enqueue(tile);
            else if (Worker.Exception != null) Log.Error(Worker.Exception, "Border visual tile build failed");
            Worker = null;
        }
        var timer = Stopwatch.StartNew();
        int uploadBytes = 0;
        while (Uploads.Count > 0 && uploadBytes < 8*1024*1024 && timer.Elapsed.TotalMilliseconds < 2)
        {
            BorderVisualTile data = Uploads.Dequeue();
            int bytes = data.Territory.Length*8 + (data.Metadata.Length+data.Colors.Length)*16;
            if (Building.Bytes + bytes > GenerationBudget)
            {
                // Fall back to the complete overview until a coarser detailed
                // viewport fits. Never drop selected small claims from a tile.
                var remove = new List<Key>();
                foreach (Key key in Building.Tiles.Keys) if (!Building.Overview.Contains(key)) remove.Add(key);
                foreach (Key key in remove) { Building.Tiles[key].Dispose(); Building.Tiles.Remove(key); }
                ++Level;
                BudgetLevel = Level.Value;
            }
            Building.Tiles.Add(data.Address, new(Device, data));
            uploadBytes += bytes;
            ++TilesUploaded;
        }
        // Publish a coherent overview before accepting urgent replacements.
        // Cancelling after every tile can starve the first visible generation
        // indefinitely in a live galaxy with changing colonies/source lists.
        if (Worker == null && Uploads.Count == 0 && Building.Complete(Building.Overview)
            && latest.NeedsImmediateRefresh(Building.Scene))
        {
            if (Displayed != Building)
            {
                Displayed?.Dispose();
                Displayed = Building;
                LastKnowledgeChecked = null;
            }
            Building = new(latest, radius, Displayed);
        }
        List<Key> detail = overviewOnly ? Building.Overview : Keys(view, Level.Value);
        List<Key> prefetched = overviewOnly ? Building.Overview : PrefetchKeys(view, Level.Value);
        // A pathological viewport must not exhaust graphics memory. The coherent
        // overview remains available; no individual claims are removed.
        if (detail.Count > 240) detail = Building.Overview;
        if (Building.Complete(Building.Overview) && Building.Complete(detail))
        {
            if (Displayed != Building)
            {
                Displayed?.Dispose(); Displayed = Building; LastKnowledgeChecked = null;
                NextSceneRefresh = Environment.TickCount64 + SceneRefreshMilliseconds;
            }
            if (!ReferenceEquals(latest, Building.Scene) && Worker == null && Uploads.Count == 0)
            {
                if (latest.SameGeometry(Building.Scene))
                {
                    PaletteScene = latest;
                    foreach (Tile tile in Building.Tiles.Values) PaletteUpdates.Enqueue(tile);
                    return;
                }
                // Coalesce simulation snapshots while leaving camera-detail work
                // responsive. Revoking knowledge must bypass the cooldown.
                if (HideStaleKnowledge || latest.NeedsImmediateRefresh(Building.Scene)
                    || Environment.TickCount64 >= NextSceneRefresh)
                    Building = new(latest, radius, Displayed);
            }
        }
        else if (Displayed == null && Building.Complete(Building.Overview))
        {
            Displayed = Building;
            NextSceneRefresh = Environment.TickCount64 + SceneRefreshMilliseconds;
        }
        CaptureOverview(radius);
        if (Worker != null || Uploads.Count != 0) return;
        // Keep visible and overview tiles, evict offscreen detail before allocating.
        if (Building.Tiles.Count > 250)
        {
            var keep = new HashSet<Key>(prefetched);
            keep.UnionWith(Building.Overview);
            var remove = new List<Key>();
            foreach (Key key in Building.Tiles.Keys) if (!keep.Contains(key)) remove.Add(key);
            foreach (Key key in remove) { Building.Tiles[key].Dispose(); Building.Tiles.Remove(key); }
        }
        const int tilesPerJob = 1;
        var missing = new List<Key>(tilesPerJob);
        foreach (Key key in Building.Overview)
            if (!Building.Tiles.ContainsKey(key) && missing.Count < tilesPerJob) missing.Add(key);
        if (missing.Count == 0)
            foreach (Key key in detail)
                if (!Building.Tiles.ContainsKey(key) && missing.Count < tilesPerJob) missing.Add(key);
        // Visible detail takes precedence over the offscreen prefetch ring.
        if (prefetched.Count <= 240)
            foreach (Key key in prefetched)
                if (missing.Count < tilesPerJob && !Building.Tiles.ContainsKey(key) && !missing.Contains(key)) missing.Add(key);
        if (missing.Count == 0) return;
        BorderScene scene = Building.Scene;
        var overviewKeys = Building.Overview;
        Worker = BorderWorker.TryRunVisual(() =>
        {
            scene.SaveKey();
            WorkerScratch ??= new();
            var result = new BorderVisualTile[missing.Count];
            for (int i = 0; i < result.Length; ++i)
            {
                result[i] = new(scene, missing[i], WorkerScratch);
                if (overviewKeys.Contains(missing[i])) result[i].SavedPixels = result[i].Save();
            }
            return result;
        });
        if (Worker != null) ++JobsStarted;
    }
    public void Draw(SpriteRenderer renderer, Matrix matrix, RectF view, float worldPerPixel)
    {
        if (Displayed == null || HideStaleKnowledge) return;
        List<Key> detail = Keys(view, Level ?? 0,240);
        List<Key> selected = detail != null && Displayed.Complete(detail) ? detail : null;
        if (selected == null)
        {
            // A zoom request must not immediately discard the last useful LOD.
            // Prefer the closest complete resident level, including its prefetched
            // ring, before falling back to the much coarser galaxy overview.
            var levels = new HashSet<int>();
            foreach (Key key in Displayed.Tiles.Keys) levels.Add(key.Level);
            float best = float.MaxValue;
            foreach (int level in levels)
            {
                float error = Math.Abs(MathF.Log2(new Key(level,0,0).Cell / worldPerPixel));
                if (error >= best) continue;
                List<Key> keys = Keys(view,level,240);
                if (keys != null && Displayed.Complete(keys)) { selected = keys; best = error; }
            }
            selected ??= Displayed.Overview;
        }
        if (StripeCell == 0 || LastDrawLevel != selected[0].Level)
        {
            StripeTarget = selected[0].Cell;
            StripeFrom = StripeCell == 0 ? StripeTarget : StripeCell;
            StripeChanged = Environment.TickCount64;
            LastDrawLevel = selected[0].Level;
        }
        float transition = Math.Clamp((Environment.TickCount64-StripeChanged)/200f,0,1);
        StripeCell = StripeFrom + (StripeTarget-StripeFrom)*transition;
        foreach (Key key in selected) DrawTile(renderer, matrix, Displayed.Tiles[key], key.Bounds, key.Cell / worldPerPixel, false, 1);
    }
    public string HoverText(Vector2 point, float worldPerPixel)
    {
        if (Displayed == null || HideStaleKnowledge) return null;
        float cell = new Key(LastDrawLevel,0,0).Cell;
        var key = new Key(LastDrawLevel,BorderVisualTile.TileCoordinate(point.X,cell),BorderVisualTile.TileCoordinate(point.Y,cell));
        if (!Displayed.Tiles.TryGetValue(key,out Tile tile)) return null;
        int x = (int)MathF.Floor(point.X/cell)-key.X*256+32;
        int y = (int)MathF.Floor(point.Y/cell)-key.Y*256+32;
        Color sample = tile.Data.Territory[y*320+x];
        int id = BorderVisualTile.Decode(sample);
        if (id == 0) return null;
        var members = new SortedSet<int>(tile.Data.Regions[id].Members);
        bool contested = members.Count > 1;
        if (sample.A/255f*16*cell <= worldPerPixel*3)
        {
            int radius = Math.Min(16,Math.Max(1,(int)MathF.Ceiling(worldPerPixel*3/cell)));
            foreach (var (dx,dy) in new[] {(-radius,0),(radius,0),(0,-radius),(0,radius)})
            {
                int other = BorderVisualTile.Decode(tile.Data.Territory[(y+dy)*320+x+dx]);
                members.UnionWith(tile.Data.Regions[other].Members);
            }
        }
        var names = new List<string>();
        foreach (int member in members) names.Add(Displayed.Scene.Empires[member].Name);
        string prefix = contested ? "Contested territory\n" : members.Count > 1 ? "Shared frontier\n" : "";
        return prefix + string.Join("\n",names);
    }
    public void DrawMinimap(RectF map, Vector2 zero, float scale, float alpha)
    {
        if (Displayed == null || HideStaleKnowledge) return;
        // This renderer is private to the minimap, so ScreenManager's shared
        // renderer recycling does not cover it. Reuse its buffers every frame.
        MiniRenderer.RecycleBuffers();
        var viewport = Device.Viewport;
        Matrix matrix = Matrix.CreateOrthographicOffCenter(0, viewport.Width, viewport.Height, 0, -10, 10);
        // Overview tiles extend beyond galaxy bounds. Clip to the actual minimap.
        var oldRaster = Device.RasterizerState;
        var oldScissor = Device.ScissorRectangle;
        using var raster = new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true };
        Device.RasterizerState = raster;
        Device.ScissorRectangle = new((int)map.X, (int)map.Y, (int)map.W, (int)map.H);
        try
        {
            foreach (Key key in Displayed.Overview)
            {
                RectF b = key.Bounds;
                DrawTile(MiniRenderer, matrix, Displayed.Tiles[key], new(zero.X+b.X*scale,zero.Y+b.Y*scale,b.W*scale,b.H*scale), key.Cell*scale, true, alpha);
            }
        }
        finally { Device.RasterizerState = oldRaster; Device.ScissorRectangle = oldScissor; }
    }
    void DrawTile(SpriteRenderer renderer, Matrix matrix, Tile tile, RectF bounds, float pixelsPerCell, bool mini, float alpha)
    {
        var shader = Effect.Shader;
        shader["Neighbors"].SetValue(tile.Neighbor);
        shader["Regions"].SetValue(tile.Metadata);
        shader["ClaimantColors"].SetValue(tile.Colors);
        shader["RegionSize"].SetValue(new XnaVector2(tile.Metadata.Width,tile.Metadata.Height));
        shader["PaletteSize"].SetValue(new XnaVector2(tile.Colors.Width,tile.Colors.Height));
        shader["CellOrigin"].SetValue(new XnaVector2(tile.Data.Address.X*256-32,tile.Data.Address.Y*256-32));
        shader["PixelsPerCell"].SetValue(pixelsPerCell);
        shader["Minimap"].SetValue(mini ? 1f : 0f);
        shader["StripeScale"].SetValue(mini ? 1f : tile.Data.Address.Cell / StripeCell);
        Device.BlendState = BlendState.AlphaBlend;
        Device.DepthStencilState = DepthStencilState.None;
        renderer.Begin(matrix, Effect);
        renderer.Draw(tile.Territory, new Quad3D(bounds,0), new Quad2D(new RectF(0.1f,0.1f,0.8f,0.8f)), new Color(alpha,alpha,alpha,alpha));
        // End flushes deferred vertices BEFORE secondary texture bindings change.
        renderer.End();
    }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        Displayed?.Dispose();
        if (Building != Displayed) Building?.Dispose();
        Uploads.Clear();
        PaletteUpdates.Clear(); PaletteScene = null;
        Worker = null;
        Effect.Dispose(); MiniRenderer.Dispose();
    }
}
