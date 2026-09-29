using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Xna.Framework.Graphics;
using SDGraphics;
using SDGraphics.Rendering;
using SDGraphics.Sprites;
using Ship_Game;
using Ship_Game.Universe;
using Ship_Game.Graphics;
using Color = Microsoft.Xna.Framework.Color;

namespace UnitTests.Universe;

[TestClass]
public partial class BorderVisualTests : StarDriveTest
{
    public TestContext TestContext { get; set; }
    public BorderVisualTests()
    {
        CreateUniverseAndPlayerEmpire();
        Player.GetRelations(Enemy).AtWar = false;
        Enemy.GetRelations(Player).AtWar = false;
    }

    internal void SetField(Empire empire, params (Vector2 Position, float Radius)[] points)
    {
        var nodes = new Empire.InfluenceNode[points.Length];
        var samples = new BorderField.Node[points.Length];
        for (int i = 0; i < points.Length; ++i)
        {
            var (position, radius) = points[i];
            Planet planet = AddDummyPlanetToEmpire(position, empire);
            nodes[i] = new Empire.InfluenceNode(planet.System, radius, true);
            samples[i] = new BorderField.Node(position, radius, planet.System.Id * 0.754877666f + empire.Id * 1.618033989f, 0);
        }
        empire.BorderNodes = nodes;
        empire.PreparedBorders = new BorderSnapshot(nodes, samples, empire.GetProjectorRadius());
        empire.BorderConnections = empire.PreparedBorders.Connections;
    }

    internal Empire AddEmpire(int index, bool faction = false)
    {
        var races = faction ? ResourceManager.MinorRaces : ResourceManager.MajorRaces;
        var data = races[index % races.Count].CreateInstance();
        data.Traits.Name = "Border fixture " + index;
        Empire empire = UState.CreateEmpire(data, false);
        foreach (Empire other in UState.Empires)
            if (other != empire) Empire.SetRelationsAsKnown(empire, other);
        return empire;
    }

    internal BorderScene Fixture(bool dense = false)
    {
        int count = dense ? 16 : 5;
        var empires = new List<Empire> { Player, Enemy };
        for (int i = 2; i < count; ++i) empires.Add(AddEmpire(i, dense && i >= 12));
        Color[] colors = { Color.CornflowerBlue, Color.OrangeRed, Color.MediumSeaGreen, Color.Gold, Color.MediumPurple };
        var random = new Random(18751);
        for (int e = 0; e < count; ++e)
        {
            Empire owner = empires[e];
            owner.EmpireColor = colors[e % colors.Length];
            int nodes = dense ? (e < 8 ? 63 : 62) : 3;
            var points = new (Vector2, float)[nodes];
            float angle = e * MathF.Tau / count;
            var center = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 240000;
            for (int n = 0; n < nodes; ++n)
                points[n] = (center + new Vector2(random.Next(-100000, 100000), random.Next(-100000, 100000)), dense ? 55000 : 105000);
            SetField(owner, points);
            foreach (Empire other in empires)
                if (other != owner) owner.GetRelations(other).AtWar = (e < 3 && empires.IndexOf(other) < 3);
        }
        return new BorderScene(empires.ToArray());
    }

    internal string ResultPath(string name)
    {
        string dir = Path.GetFullPath(Path.Combine(StarDriveTestContext.StarDriveAbsolutePath, "../UnitTests/TestResults/Borders"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    internal void Save(RenderTarget2D target, string name)
    {
        string path = ResultPath(name);
        using (var file = File.Create(path)) target.SaveAsPng(file, target.Width, target.Height);
        TestContext.AddResultFile(path);
    }

    internal static void PrepareLegacy(BorderScene scene)
    {
        for (int i = 0; i < scene.Empires.Length; ++i)
            scene.Empires[i].Owner.BorderNodeCache.SetScene(scene, i);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            bool ready = true;
            foreach (var entry in scene.Empires)
            {
                entry.Owner.BorderNodeCache.Update(entry.Owner);
                ready &= entry.Owner.BorderNodeCache.OutlineSegments.Length > 0;
            }
            return ready;
        }, 30000), "Legacy fixture did not finish building");
    }

    // Keep the pre-refactor draw algorithm here for reproducible comparisons.
    internal static void DrawLegacy(BorderScene scene, SpriteRenderer renderer, Matrix matrix, float worldWidth)
    {
        GraphicsDevice device = renderer.Device;
        foreach (var entry in scene.Empires.OrderBy(e => e.Owner.isPlayer))
        {
            Empire empire = entry.Owner;
            BorderNodeCache cache = empire.BorderNodeCache;
            renderer.Begin(matrix);
            RenderStates.BasicBlendMode(device, additive: false, depthWrite: false);
            RenderStates.EnableSeparateAlphaBlend(device, Blend.SourceAlphaSaturation, Blend.One);
            RenderStates.EnableAlphaTest(device, CompareFunction.Greater);
            var quad = new Quad3D(cache.FillBounds, 0);
            renderer.Draw(cache.GetFillTexture(device), quad, SpriteRenderer.DefaultCoords, empire.EmpireColor.Alpha(0.58f));
            renderer.Draw(cache.GetOccupationTexture(device), quad, SpriteRenderer.DefaultCoords, Color.Black.Alpha(0.95f));
            for (int i = 0; i + 1 < cache.OutlineSegments.Length; i += 2)
            {
                Vector2 a = Wave(cache.OutlineSegments[i]), b = Wave(cache.OutlineSegments[i + 1]);
                Empire rival = cache.OutlineRivals[i / 2];
                if (rival != null && !empire.BordersOverlapAt(rival, (a + b) * 0.5f) && empire.Id > rival.Id) continue;
                Color color = rival == null ? empire.EmpireColor : Color.Lerp(empire.EmpireColor, rival.EmpireColor, 0.5f);
                renderer.DrawLine(new Vector3(a, 1), new Vector3(b, 1), color, Math.Max(960, worldWidth / 100));
            }
            renderer.End();
            RenderStates.DisableSeparateAlphaChannelBlend(device);
            Vector2 Wave(Vector2 p)
            {
                float radius = empire.GetProjectorRadius(), frequency = 1 / (radius * 0.45f), phase = empire.Id * 1.731f;
                return p + new Vector2(MathF.Sin(p.Y * frequency + phase), MathF.Sin(p.X * frequency * 1.13f + phase * 0.67f)) * (radius * 0.007f);
            }
        }
    }

    [TestMethod]
    public void CaptureLegacyBaseline()
    {
        BorderScene scene = Fixture();
        PrepareLegacy(scene);
        GraphicsDevice device = Game.GraphicsDevice;
        using var target = new RenderTarget2D(device, 1920, 1080, false, SurfaceFormat.Color, DepthFormat.None);
        using var renderer = new SpriteRenderer(device);
        var previous = device.GetRenderTargets();
        try
        {
            device.SetRenderTarget(target);
            device.Clear(new Color(6, 8, 16));
            DrawLegacy(scene, renderer, Matrix.CreateOrthographicOffCenter(-600000, 600000, 337500, -337500, -10, 10), 1200000);
        }
        finally { device.SetRenderTargets(previous); }
        Save(target, "legacy-1080p.png");
    }

    [TestMethod]
    public void ClassificationMatchesGameplayAndEnumerationOrder()
    {
        BorderScene scene = Fixture();
        var strengths = new float[scene.Empires.Length];
        var winners = new int[scene.Empires.Length];
        var reversed = new BorderScene(Enumerable.Reverse(scene.Empires).Select(e => e.Owner).ToArray());
        var otherWinners = new int[scene.Empires.Length];
        for (int y = -400000; y <= 400000; y += 19000)
            for (int x = -400000; x <= 400000; x += 19000)
            {
                var point = new Vector2(x, y);
                int count = scene.Classify(point, strengths, winners);
                int[] actual = winners.Take(count).Select(i => scene.Empires[i].Id).ToArray();
                int[] expected = scene.Empires.Where(e => e.Known && e.Owner.IsInBorderTerritory(point)
                    && e.Snapshot.KnownField.Strength(point) >= 0).Select(e => e.Id).OrderBy(id => id).ToArray();
                CollectionAssert.AreEqual(expected, actual, $"Ownership mismatch at {point}");
                int otherCount = reversed.Classify(point, strengths, otherWinners);
                CollectionAssert.AreEqual(actual, otherWinners.Take(otherCount).Select(i => reversed.Empires[i].Id).ToArray());
            }
    }

    [TestMethod]
    public void FiveClaimantsAndUnknownCompetition()
    {
        var empires = new[] { Player, Enemy, AddEmpire(2), AddEmpire(3), AddEmpire(4) };
        foreach (Empire empire in empires)
        {
            SetField(empire, (new Vector2(1000,1000), 100000));
            foreach (Empire other in empires)
                if (empire != other) empire.GetRelations(other).AtWar = true;
        }
        var strengths = new float[5];
        var winners = new int[5];
        Assert.AreEqual(5, new BorderScene(empires).Classify(new(1000,1000), strengths, winners));
        var hidden = new BorderScene(empires);
        hidden.Empires[1].Known = false; // captured knowledge; live empire bitset is deliberately untouched
        Assert.AreEqual(4, hidden.Classify(new(1000,1000), strengths, winners));
        // A hidden peaceful stronger rival still removes another empire's claim.
        foreach (Empire empire in empires)
            foreach (Empire other in empires)
                if (empire != other) empire.GetRelations(other).AtWar = false;
        SetField(Player, (new Vector2(90000,1000), 100000));
        var scene = new BorderScene(empires);
        int count = scene.Classify(new(1000,1000), strengths, winners);
        Assert.IsFalse(winners.Take(count).Any(i => scene.Empires[i].Id == Player.Id));
    }

    [TestMethod]
    public void TileGuttersAgreeAndLabelsRoundTrip()
    {
        BorderScene scene = Fixture();
        var a = new BorderVisualTile(scene, new(0,-1,0));
        var b = new BorderVisualTile(scene, new(0,0,0));
        for (int y = 32; y < 288; y += 7)
            for (int x = 272; x < 304; ++x)
            {
                int pa = y*320+x, pb = y*320+x-256;
                CollectionAssert.AreEqual(a.Regions[a.Labels[pa]].Members, b.Regions[b.Labels[pb]].Members);
                Assert.AreEqual(a.Territory[pa].A, b.Territory[pb].A, "Distance field seam");
            }
        foreach (int id in new[] {0,1,255,256,65535,65536,16777215})
            Assert.AreEqual(id, BorderVisualTile.Decode(BorderVisualTile.Encode(id,173)));
        Assert.AreEqual(-1, BorderVisualTile.TileCoordinate(-1,1000));
        Assert.AreEqual(-1, BorderVisualTile.TileCoordinate(-256000,1000));
        Assert.AreEqual(-2, BorderVisualTile.TileCoordinate(-256001,1000));
    }

    [TestMethod]
    public void EpsilonTiesAndNonTransitiveContests()
    {
        Empire third = AddEmpire(2);
        SetField(Player,(new Vector2(1000,1000),100000));
        Enemy.PreparedBorders = third.PreparedBorders = Player.PreparedBorders;
        var empires = new[] { Player,Enemy,third };
        var strengths = new float[3];
        var winners = new int[3];
        var scene = new BorderScene(empires);
        Assert.AreEqual(1,scene.Classify(new(1000,1000),strengths,winners));
        Assert.AreEqual(Player.Id,scene.Empires[winners[0]].Id);
        Player.GetRelations(Enemy).AtWar = Enemy.GetRelations(Player).AtWar = true;
        Enemy.GetRelations(third).AtWar = third.GetRelations(Enemy).AtWar = true;
        scene = new BorderScene(empires);
        Assert.AreEqual(2,scene.Classify(new(1000,1000),strengths,winners));
        CollectionAssert.AreEqual(new[]{Player.Id,Enemy.Id},winners.Take(2).Select(i=>scene.Empires[i].Id).ToArray());
    }

    [TestMethod]
    public void SavedOverviewRoundTripSkipsRasterizationAndRejectsChangedScene()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var scene = new BorderScene(new[]{Player});
        var view = new RectF(-300000,-300000,600000,600000);
        using var original = new BorderVisualRenderer(Game.GraphicsDevice);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            original.Update(scene,300000,view,view.W/450,overviewOnly:true);
            return original.SavedOverview != null;
        },30000));
        var saved = UnitTests.Serialization.BinarySerializerTests.SerDes(original.SavedOverview);
        using var restored = new BorderVisualRenderer(Game.GraphicsDevice);
        Assert.IsTrue(restored.RestoreOverview(saved,scene,300000));
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            restored.Update(scene,300000,view,view.W/450,overviewOnly:true);
            return restored.DisplayedScene != null;
        },30000));
        Assert.AreEqual(0,restored.JobsStarted,"Saved overview must require no rasterization jobs");
        Assert.AreEqual(original.TilesUploaded,restored.TilesUploaded);
        UState.BorderOverviewCache = saved;
        var loaded = UnitTests.Serialization.BinarySerializerTests.SerDes(UState);
        Assert.IsNotNull(loaded.BorderOverviewCache);
        Assert.IsNotNull(loaded.Player.SavedBorderCache);
        CollectionAssert.AreEqual(saved.SceneKey,loaded.BorderOverviewCache.SceneKey);
        Assert.IsTrue(loaded.Player.SavedBorderCache.Matches(Player.PreparedBorders.SaveCache.Inputs));
        using var stale = new BorderVisualRenderer(Game.GraphicsDevice);
        SetField(Player,(new Vector2(100000,1000),100000));
        Assert.IsFalse(stale.RestoreOverview(saved,new BorderScene(new[]{Player}),300000));
        saved.Version++;
        Assert.IsFalse(stale.RestoreOverview(saved,scene,300000));
        saved.Version--;
        saved.Tiles[0] = new byte[]{1,2,3};
        Assert.IsFalse(stale.RestoreOverview(saved,scene,300000));
    }

    [TestMethod]
    public void SavedGeometryRoundTripRestoresFieldsAndRejectsChangedInputs()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var snapshot = Player.PreparedBorders;
        var saved = UnitTests.Serialization.BinarySerializerTests.SerDes(snapshot.SaveCache);
        Assert.IsTrue(saved.Matches(snapshot.SaveCache.Inputs));
        for (int x = -120000; x < 120000; x += 1000)
            Assert.AreEqual(snapshot.Field.Strength(new(x,1000)),saved.Field.Strength(new(x,1000)));
        var changed = (byte[])saved.Inputs.Clone();
        changed[0] ^= 1;
        Assert.IsFalse(saved.Matches(changed));
        saved.Version++;
        Assert.IsFalse(saved.Matches(snapshot.SaveCache.Inputs));
    }

    [TestMethod]
    public void LoadingScreenPreparesAllEmpiresBeforeEntryWithoutSimulation()
    {
        AddDummyPlanetToEmpire(new Vector2(1000,1000),Player);
        AddDummyPlanetToEmpire(new Vector2(200000,1000),Enemy);
        Player.PreparedBorders = null;
        Enemy.PreparedBorders = null;
        UState.P.DisablePoliticalBorders = false;
        UState.HidePoliticalBorders = false;
        float previousAlpha = GlobalStats.InfluenceNodeAlpha;
        float date = UState.StarDate;
        try
        {
            GlobalStats.InfluenceNodeAlpha = 1;
            Assert.IsFalse(Universe.PrepareLoadedBorderVisuals(Game.GraphicsDevice));
            Assert.IsNull(Universe.PoliticalBorders?.DisplayedScene);
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                Universe.PrepareLoadedBorderVisuals(Game.GraphicsDevice),30000));
            var scene = Universe.PoliticalBorders.DisplayedScene;
            Assert.IsNotNull(scene);
            Assert.IsTrue(scene.Empires.All(e => !e.Active || e.Snapshot != null));
            Assert.AreEqual(date,UState.StarDate);
            var loaded = UnitTests.Serialization.BinarySerializerTests.SerDes(UState);
            using var loadedScreen = new UniverseScreen(loaded) { CreateSimThread = false };
            loaded.Objects.InitializeFromSave();
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                loadedScreen.PrepareLoadedBorderVisuals(Game.GraphicsDevice),30000));
            Assert.AreEqual(0,loadedScreen.PoliticalBorders.JobsStarted,
                "Save initialization must preserve the overview cache's input identity");
            loadedScreen.PoliticalBorders.Dispose();
            loadedScreen.PoliticalBorders = null;
        }
        finally
        {
            GlobalStats.InfluenceNodeAlpha = previousAlpha;
            Universe.PoliticalBorders?.Dispose();
            Universe.PoliticalBorders = null;
        }
    }

    [TestMethod]
    public void BackgroundOverviewPublishesAndDrawsWithoutSimulationTicks()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        Enemy.PreparedBorders = null;
        var scene = new BorderScene(new[]{Player,Enemy});
        var device = Game.GraphicsDevice;
        using var borders = new BorderVisualRenderer(device);
        var view = new RectF(-300000,-300000,600000,600000);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            borders.Update(scene,300000,view,view.W/512);
            return borders.DisplayedScene == scene;
        },30000));
        Assert.AreSame(scene,borders.DisplayedScene);
        using var draw = new SpriteRenderer(device);
        using var target = new RenderTarget2D(device,512,512,false,SurfaceFormat.Color,DepthFormat.None);
        var previous = device.GetRenderTargets();
        try
        {
            device.SetRenderTarget(target);
            device.Clear(Color.Transparent);
            borders.Draw(draw,Matrix.CreateOrthographicOffCenter(view.Left,view.Right,view.Bottom,view.Top,-10,10),view,view.W/512);
        }
        finally { device.SetRenderTargets(previous); }
        var pixels = new Color[512*512];
        target.GetData(pixels);
        Assert.IsTrue(Array.Exists(pixels,p => p.A >= 100),"Published overview must have visible outlines");
    }

    [TestMethod]
    public void ContinuousUrgentChangesCannotStarveFirstOverview()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var first = new BorderScene(new[]{Player});
        AddDummyPlanetToEmpire(new Vector2(400000,1000),Player);
        var second = new BorderScene(new[]{Player});
        Assert.IsTrue(second.NeedsImmediateRefresh(first));
        using var borders = new BorderVisualRenderer(Game.GraphicsDevice);
        var view = new RectF(-300000,-300000,600000,600000);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            // Change the request after every scheduled tile, before the whole
            // overview can finish. An always-restart policy never publishes.
            var latest = borders.JobsStarted % 2 == 0 ? first : second;
            borders.Update(latest,300000,view,view.W/512);
            return borders.DisplayedScene != null;
        },30000),"Live colony changes prevented an overview from ever publishing");
        Assert.IsTrue(borders.TilesUploaded > 1);
    }

    [TestMethod]
    public void ColonyChangesRefreshImmediatelyWithoutBlankingPendingBorders()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var before = new BorderScene(new[]{Player});
        using var borders = new BorderVisualRenderer(Game.GraphicsDevice);
        var view = new RectF(-150000,-150000,300000,300000);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            borders.Update(before,300000,view,1100);
            return borders.IsViewReady(view,1100);
        },30000));
        Assert.IsNotNull(borders.HoverText(new(1000,1000),1100));
        Player.PreparedBorders = null;
        Player.BorderNodes = Array.Empty<Empire.InfluenceNode>();
        var pending = new BorderScene(new[]{Player});
        borders.Update(pending,300000,view,1100);
        Assert.IsFalse(pending.KnowledgeRemovedSince(before));
        Assert.IsNotNull(borders.HoverText(new(1000,1000),1100),"Pending removal must not hide every border");
        SetField(Player,(new Vector2(200000,1000),100000));
        var changed = new BorderScene(new[]{Player});
        Assert.IsTrue(changed.NeedsImmediateRefresh(before));
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            borders.Update(changed,300000,view,1100);
            return borders.DisplayedScene == changed;
        },30000));
        Assert.IsNull(borders.HoverText(new(1000,1000),1100),"Replacement must remove the old territory");
    }

    [TestMethod]
    public void ReusedTileScratchPreservesContoursAndReducesAllocations()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var scene = new BorderScene(new[]{Player});
        var scratch = new BorderVisualTile.Scratch();
        var key = new BorderVisualTile.Key(0,0,0);
        _ = new BorderVisualTile(scene,key,scratch);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var fresh = new BorderVisualTile(scene,key);
        long freshBytes = GC.GetAllocatedBytesForCurrentThread()-before;
        // Visit a different tile before reuse to detect stale distance/seed data.
        _ = new BorderVisualTile(scene,new(0,1,0),scratch);
        before = GC.GetAllocatedBytesForCurrentThread();
        var reused = new BorderVisualTile(scene,key,scratch);
        long reusedBytes = GC.GetAllocatedBytesForCurrentThread()-before;
        CollectionAssert.AreEqual(fresh.Territory,reused.Territory);
        CollectionAssert.AreEqual(fresh.Neighbor,reused.Neighbor);
        Assert.IsTrue(freshBytes-reusedBytes > 3_000_000,
            $"Expected scratch reuse to save at least 3 MB per tile: {freshBytes} vs {reusedBytes}");
    }

    [TestMethod]
    public void CacheRecolorsWithoutRasterWorkAndDisposesDuringBuild()
    {
        BorderScene scene = Fixture();
        using var borders = new BorderVisualRenderer(Game.GraphicsDevice);
        var view = new RectF(-300000,-300000,600000,600000);
        Assert.IsTrue(SpinWait.SpinUntil(()=> { borders.Update(scene,600000,view,2500); return borders.IsViewReady(view,2500); },30000));
        int jobs = borders.JobsStarted;
        Player.EmpireColor = Color.HotPink;
        var recolored = new BorderScene(scene.Empires.Select(e=>e.Owner).ToArray());
        Assert.IsTrue(SpinWait.SpinUntil(()=> { borders.Update(recolored,600000,view,2500); return borders.DisplayedScene == recolored; },30000));
        Assert.AreEqual(jobs,borders.JobsStarted,"Color change rebuilt categorical territory");
        Assert.IsTrue(borders.GpuBytes <= 512L*1024*1024);
        Assert.IsTrue(borders.ResidentCpuBytes <= 384L*1024*1024);
        Assert.AreSame(recolored,BorderScene.Capture(recolored,scene.Empires.Select(e=>e.Owner).ToArray()));
        var hidden = new BorderScene(scene.Empires.Select(e=>e.Owner).ToArray());
        hidden.Empires[0].Known = false;
        Assert.IsTrue(hidden.KnowledgeRemovedSince(recolored));
        borders.Update(hidden,600000,view,2500);
        Assert.IsNull(borders.HoverText(new(240000,0),2500));
        borders.Dispose();
        borders.Update(scene,600000,view,2500); // no late upload after disposal
    }

    [TestMethod]
    public void BorderTooltipYieldsToForegroundInEitherOrder()
    {
        ToolTip.Clear();
        try
        {
            ToolTip.CreateLowPriorityTooltip("Contested territory");
            ToolTip.CreateTooltip("Planet details");
            Assert.AreEqual(1,ToolTip.ActiveTipCount);
            ToolTip.Clear();
            ToolTip.CreateTooltip("Planet details");
            ToolTip.CreateLowPriorityTooltip("Contested territory");
            Assert.AreEqual(1,ToolTip.ActiveTipCount);
        }
        finally { ToolTip.Clear(); }
    }

    [TestMethod]
    public void BorderOutlineSurvivesPendingZoomLevel()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var scene = new BorderScene(new[]{Player});
        var device = Game.GraphicsDevice;
        using var borders = new BorderVisualRenderer(device);
        using var draw = new SpriteRenderer(device);
        using var target = new RenderTarget2D(device,512,512,false,SurfaceFormat.Color,DepthFormat.None);
        var wide = new RectF(-300000,-300000,600000,600000);
        Assert.IsTrue(SpinWait.SpinUntil(()=> { borders.Update(scene,300000,wide,600000f/512); return borders.IsViewReady(wide,600000f/512); },30000));
        // Locate a real frontier in the resident level, then zoom directly onto
        // it without waiting for any replacement tiles to finish.
        var tile = new BorderVisualTile(scene,new(0,0,0));
        int edge = -1;
        const int row = 33;
        for (int x = 32; x < 287; ++x)
            if (tile.Labels[row*320+x] != 0 && tile.Labels[row*320+x+1] == 0) { edge = x; break; }
        Assert.IsTrue(edge >= 0);
        float leftDistance = tile.Territory[row*320+edge].A;
        float rightDistance = tile.Territory[row*320+edge+1].A;
        float crossing = leftDistance / Math.Max(1,leftDistance+rightDistance);
        var center = new Vector2((edge+0.5f+crossing-32)*1000,1500);
        var previous = device.GetRenderTargets();
        var pixels = new Color[512*512];
        try
        {
            foreach (float pixel in new[] {20f,2000f,5f,600000f/512,40f})
            {
                float span = pixel*512;
                var view = new RectF(center.X-span/2,center.Y-span/2,span,span);
                borders.Update(scene,300000,view,pixel);
                draw.RecycleBuffers();
                device.SetRenderTarget(target); device.Clear(Color.Transparent);
                borders.Draw(draw,Matrix.CreateOrthographicOffCenter(view.Left,view.Right,view.Bottom,view.Top,-10,10),view,pixel);
                device.SetRenderTargets(previous);
                target.GetData(pixels);
                int maximum = 0;
                for (int x = 0; x < 512; ++x) maximum = Math.Max(maximum,pixels[256*512+x].A);
                Assert.IsTrue(maximum >= 100,$"Outline disappeared during zoom at {pixel} world units/pixel: peak alpha {maximum}");
                Save(target,$"zoom-transition-{pixel:F0}.png");
            }
        }
        finally { device.SetRenderTargets(previous); }
    }

    [TestMethod]
    public void BorderShrinkageDoesNotBlankKnownTerritory()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var before = new BorderScene(new[]{Player});
        Empire.InfluenceNode node = Player.PreparedBorders.Nodes[0];
        node.Radius *= 0.9f;
        Player.PreparedBorders = new BorderSnapshot(new[]{node},
            new[]{new BorderField.Node(node.Position,node.Radius,0,0)},Player.GetProjectorRadius());
        var after = new BorderScene(new[]{Player});
        Assert.IsFalse(after.KnowledgeRemovedSince(before),"Geometry shrinkage must retain the previous visible generation");
        Assert.IsFalse(after.CanReuseTile(before,new RectF(-100000,-100000,200000,200000)),"Geometry must still rebuild");
        node.KnownToPlayer = false;
        Player.PreparedBorders = new BorderSnapshot(new[]{node},
            new[]{new BorderField.Node(node.Position,node.Radius,0,0)},Player.GetProjectorRadius());
        Assert.IsTrue(new BorderScene(new[]{Player}).KnowledgeRemovedSince(after),"Actual knowledge withdrawal must hide stale data");
    }

    [TestMethod]
    public void DeveloperViewRevealsUnknownBordersAndCanBeRevokedWhilePaused()
    {
        SetField(Enemy,(new Vector2(1000,1000),100000));
        Empire.InfluenceNode node = Enemy.PreparedBorders.Nodes[0];
        node.KnownToPlayer = false;
        Enemy.PreparedBorders = new BorderSnapshot(new[]{node},
            new[]{new BorderField.Node(node.Position,node.Radius,0,0)},Enemy.GetProjectorRadius());
        var normal = new BorderScene(new[]{Enemy}).WithDebugVisibility(false);
        normal.Empires[0].Known = false;
        var strengths = new float[1];
        var winners = new int[1];
        Assert.AreEqual(0,normal.Classify(new(1000,1000),strengths,winners));
        BorderScene debug = normal.WithDebugVisibility(true);
        Assert.AreEqual(1,debug.Classify(new(1000,1000),strengths,winners));
        Assert.AreSame(normal.Empires,debug.Empires,"Paused debug toggle must reuse captured data");
        Assert.IsFalse(normal.SameGeometry(debug),"Debug toggle must invalidate visibility-dependent tiles");
        BorderScene restored = debug.WithDebugVisibility(false);
        Assert.AreEqual(0,restored.Classify(new(1000,1000),strengths,winners));
        Assert.IsTrue(restored.KnowledgeRemovedSince(debug),"Debug data must disappear immediately when debug is disabled");
    }

    [TestMethod]
    public void SmoothContourTracksSubcellGameplayBoundary()
    {
        SetField(Player,(new Vector2(1000,1000),100000));
        var scene = new BorderScene(new[]{Player});
        var tile = new BorderVisualTile(scene,new(0,0,0));
        double error = 0;
        int samples = 0;
        for (int y = 40; y < 110; ++y)
            for (int x = 32; x < 287; ++x)
            {
                int p = y*320+x;
                if (tile.Labels[p] == 0 || tile.Labels[p+1] != 0) continue;
                float worldY = (y-32+0.5f)*1000;
                float low = (x-32+0.5f)*1000, high = low+1000;
                for (int step = 0; step < 20; ++step)
                {
                    float middle = (low+high)*0.5f;
                    if (Player.IsInBorderTerritory(new(middle,worldY))) low = middle;
                    else high = middle;
                }
                float a = tile.Territory[p].A, b = tile.Territory[p+1].A;
                float interpolated = (x-32+0.5f+a/Math.Max(1,a+b))*1000;
                error += Math.Abs(interpolated-(low+high)*0.5f)/1000;
                ++samples;
            }
        Assert.IsTrue(samples > 30);
        Assert.IsTrue(error/samples < 0.08,$"Contour is quantized to cell centers: mean error {error/samples:F3} cells");
    }

    [TestMethod]
    public void ContestedShaderShowsFiveColorsAndMinimapClips()
    {
        BorderScene original = Fixture();
        foreach (var entry in original.Empires)
        {
            entry.Owner.PreparedBorders = Player.PreparedBorders;
            foreach (var rival in original.Empires)
                if (entry != rival) entry.Owner.GetRelations(rival.Owner).AtWar = true;
        }
        var scene = new BorderScene(original.Empires.Select(e=>e.Owner).ToArray());
        var device = Game.GraphicsDevice;
        using var borders = new BorderVisualRenderer(device);
        using var draw = new SpriteRenderer(device);
        using var target = new RenderTarget2D(device,512,512,false,SurfaceFormat.Color,DepthFormat.None);
        var view = new RectF(140000,-100000,200000,200000);
        const float pixel = 200000f/512;
        Assert.IsTrue(SpinWait.SpinUntil(()=> { borders.Update(scene,600000,view,pixel); return borders.IsViewReady(view,pixel); },30000));
        var previous = device.GetRenderTargets();
        var pixels = new Color[512*512];
        try
        {
            device.SetRenderTarget(target); device.Clear(Color.Transparent);
            borders.Draw(draw,Matrix.CreateOrthographicOffCenter(view.Left,view.Right,view.Bottom,view.Top,-10,10),view,pixel);
            device.SetRenderTargets(previous);
            target.GetData(pixels);
            foreach (var entry in scene.Empires)
            {
                Color color = entry.Color;
                Assert.IsTrue(pixels.Any(p=>p.A >= 14 && p.A <= 17 && Math.Abs(p.R-color.R*0.06f)<2
                    && Math.Abs(p.G-color.G*0.06f)<2 && Math.Abs(p.B-color.B*0.06f)<2),"Missing claimant color " + entry.Name);
            }
            Save(target,"five-claimants.png");
            int fullAlpha = 0;
            foreach (float strength in new[] {1f,0.5f,0f})
            {
                device.SetRenderTarget(target); device.Clear(Color.Transparent);
                borders.DrawMinimap(new RectF(128,128,256,256),new Vector2(256,256),0.0003f,strength);
                device.SetRenderTargets(previous);
                target.GetData(pixels);
                int alpha = pixels.Sum(p=>(int)p.A);
                if (strength == 1) fullAlpha = alpha;
                if (strength == 0.5f) Assert.AreEqual(fullAlpha*0.5,alpha,fullAlpha*0.04,"Strength should scale opacity once");
                if (strength == 0) Assert.AreEqual(0,alpha);
                for (int y = 0; y < 512; ++y)
                    for (int x = 0; x < 512; ++x)
                        if (x<128 || x>=384 || y<128 || y>=384) Assert.AreEqual(0,pixels[y*512+x].A,"Minimap escaped its container");
            }
        }
        finally { device.SetRenderTargets(previous); }
    }

    [TestMethod]
    public void RenderBorderMaterialAtMultipleResolutions()
    {
        BorderScene scene = Fixture();
        GraphicsDevice device = Game.GraphicsDevice;
        using var renderer = new SpriteRenderer(device);
        using var borders = new BorderVisualRenderer(device);
        var previous = device.GetRenderTargets();
        try
        {
            foreach (var (width,height) in new[] { (1920,1080), (2560,1440), (3840,2160), (3440,1440) })
            {
                using var target = new RenderTarget2D(device,width,height,false,SurfaceFormat.Color,DepthFormat.None);
                foreach (int worldWidth in new[] { 300000,1200000,3000000 })
                {
                    float worldHeight = worldWidth * (float)height / width;
                    var view = new RectF(-worldWidth/2f,-worldHeight/2,worldWidth,worldHeight);
                    float pixel = (float)worldWidth/width;
                    Assert.IsTrue(SpinWait.SpinUntil(() =>
                    {
                        borders.Update(scene,600000,view,pixel);
                        return borders.IsViewReady(view,pixel);
                    }, 120000), "Border view failed to finish");
                    device.SetRenderTarget(target);
                    device.Clear(new Color(6,8,16));
                    renderer.RecycleBuffers();
                    borders.Draw(renderer,Matrix.CreateOrthographicOffCenter(view.Left,view.Right,view.Bottom,view.Top,-10,10),view,pixel);
                    device.SetRenderTargets(previous);
                    Save(target,$"borders-{width}x{height}-{worldWidth}.png");
                }
            }
        }
        finally { device.SetRenderTargets(previous); }
    }

    [TestMethod]
    public void DenseGalaxyPerformanceAndWarmCache()
    {
        BorderScene scene = Fixture(dense:true);
        PrepareLegacy(scene);
        GraphicsDevice device = Game.GraphicsDevice;
        using var target = new RenderTarget2D(device,1920,1080,false,SurfaceFormat.Color,DepthFormat.None);
        using var renderer = new SpriteRenderer(device);
        using var borders = new BorderVisualRenderer(device);
        var view = new RectF(-600000,-337500,1200000,675000);
        const float pixel = 1200000f/1920;
        Assert.IsTrue(SpinWait.SpinUntil(() => { borders.Update(scene,600000,view,pixel); return borders.IsViewReady(view,pixel); },120000));
        int jobs = borders.JobsStarted, uploads = borders.TilesUploaded;
        var matrix = Matrix.CreateOrthographicOffCenter(view.Left,view.Right,view.Bottom,view.Top,-10,10);
        var previous = device.GetRenderTargets();
        var oldTimes = new List<double>();
        var newTimes = new List<double>();
        var readback = new Color[1];
        var timer = new Stopwatch();
        long legacyAllocations = 0, visualAllocations = 0;
        try
        {
            for (int frame = 0; frame < 36; ++frame)
            {
                foreach (bool legacy in new[] { true,false })
                {
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    timer.Restart();
                    renderer.RecycleBuffers(); // mirrors ScreenManager's per-frame lifecycle
                    device.SetRenderTarget(target);
                    device.Clear(Color.Transparent);
                    if (legacy) DrawLegacy(scene,renderer,matrix,view.W);
                    else { borders.Update(scene,600000,view,pixel); borders.Draw(renderer,matrix,view,pixel); }
                    device.SetRenderTargets(previous);
                    // Readback synchronizes the GPU; this is not just command submission time.
                    target.GetData(0,new Microsoft.Xna.Framework.Rectangle(0,0,1,1),readback,0,1);
                    timer.Stop();
                    if (frame >= 6)
                    {
                        (legacy ? oldTimes : newTimes).Add(timer.Elapsed.TotalMilliseconds);
                        allocated = GC.GetAllocatedBytesForCurrentThread()-allocated;
                        if (legacy) legacyAllocations += allocated; else visualAllocations += allocated;
                    }
                }
            }
        }
        finally { device.SetRenderTargets(previous); }
        Assert.AreEqual(jobs,borders.JobsStarted,"Warm cache scheduled new work");
        Assert.AreEqual(uploads,borders.TilesUploaded,"Warm cache uploaded textures");
        oldTimes.Sort(); newTimes.Sort();
        string report = $"Adapter: {device.Adapter.Description}\nResolution: 1920x1080\nSources: 1000; majors: 12; factions: 4\n"
            + $"Synchronized border-pass milliseconds (30 samples after 6 warmup frames):\n"
            + $"Legacy median {oldTimes[15]:F3}, p95 {oldTimes[28]:F3}\nNew median {newTimes[15]:F3}, p95 {newTimes[28]:F3}\n"
            + $"Worker jobs: {jobs}; uploaded tiles: {uploads}; steady-state jobs/uploads: 0\n"
            + $"Mean render-thread allocations per measured frame: legacy {legacyAllocations/30} bytes; new {visualAllocations/30} bytes\n"
            + $"GPU cache: {borders.GpuBytes} bytes; resident label buffers: {borders.ResidentCpuBytes} bytes\n"
            + "This isolated border-pass fixture is not an end-to-end game frame benchmark.\n";
        File.WriteAllText(ResultPath("performance.txt"),report);
        TestContext.WriteLine(report);
        TestContext.AddResultFile(ResultPath("performance.txt"));
        Assert.IsTrue(borders.GpuBytes <= 512L*1024*1024,"GPU budget exceeded");
        Assert.IsTrue(borders.ResidentCpuBytes <= 384L*1024*1024,"CPU label budget exceeded");
        Assert.IsTrue(newTimes[15] <= oldTimes[15]*1.1,"Median border-pass regression exceeds 10%: " + report);
        Assert.IsTrue(newTimes[28] <= oldTimes[28]*1.1,"P95 border-pass regression exceeds 10%: " + report);
    }
}
