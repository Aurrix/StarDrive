using System.Linq;
using System.IO;
using System.Reflection;
using System.Collections;
using SDGraphics;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ship_Game;
using Vector2 = SDGraphics.Vector2;
#pragma warning disable CA2213

namespace UnitTests.Planets;

[TestClass]
public class GlobalConstructionTests : StarDriveTest
{
    public TestContext TestContext { get; set; }
    readonly Planet First;
    readonly Planet Second;
    readonly Building Lab;
    readonly Building Factory;

    public GlobalConstructionTests()
    {
        CreateUniverseAndPlayerEmpire();
        First = AddHomeWorldToEmpire(new Vector2(1000), Player);
        Second = AddHomeWorldToEmpire(new Vector2(50000), Player);
        First.Name = "Alpha";
        Second.Name = "Beta";
        Lab = ResourceManager.GetBuildingTemplate("Research Lab");
        Factory = ResourceManager.GetBuildingTemplate("Robotic Factory");
        Player.UnlockEmpireBuilding(Lab.Name);
        Player.UnlockEmpireBuilding(Factory.Name);
        foreach (Planet planet in new[] { First, Second })
        {
            foreach (Building building in planet.TilesList.Where(t => t.Building != null).Select(t => t.Building).ToArray())
                if (building.BID == Lab.BID || building.BID == Factory.BID)
                    planet.ScrapBuilding(building);
            planet.CType = Planet.ColonyType.Core;
            planet.RefreshBuildingsWeCanBuildHere();
            planet.UpdateIncomes();
        }
    }

    [TestMethod]
    public void UniqueCopiesUseDifferentColoniesAndPreviewDoesNotEnqueue()
    {
        var preview = GlobalConstruction.Plan(Player, new[]
        {
            new GlobalConstruction.Order(Lab), new GlobalConstruction.Order(Lab), new GlobalConstruction.Order(Lab)
        });
        Assert.IsNotNull(preview[0].Destination);
        Assert.IsNotNull(preview[1].Destination);
        Assert.IsTrue(preview.SelectMany(p => p.Candidates).All(c => float.IsFinite(c.Score)),
            "New colonies without complete empire income statistics must still have finite scores");
        Assert.AreNotSame(preview[0].Destination.Planet, preview[1].Destination.Planet);
        Assert.IsNull(preview[2].Destination);
        Assert.AreEqual(0, First.ConstructionQueue.Count);
        Assert.AreEqual(0, Second.ConstructionQueue.Count);
        Assert.IsTrue(preview[0].Destination.Tile.NoQueuedBuildings);
    }

    [TestMethod]
    public void DifferentBuildingsReserveDifferentTilesAndBecomePlayerOrders()
    {
        var preview = GlobalConstruction.Plan(Player, new[]
        {
            new GlobalConstruction.Order(Lab, First), new GlobalConstruction.Order(Factory, First)
        });
        Assert.IsNotNull(preview[0].Destination);
        Assert.IsNotNull(preview[1].Destination);
        Assert.AreNotSame(preview[0].Destination.Tile, preview[1].Destination.Tile);
        Assert.AreEqual(0, GlobalConstruction.Submit(Player, preview).Length);
        Assert.AreEqual(2, First.ConstructionQueue.Count);
        Assert.IsTrue(First.ConstructionQueue.All(q => q.IsPlayerAdded && q.isBuilding));
        Assert.AreEqual(2, GlobalConstruction.Submit(Player, preview).Length, "Resubmission must not duplicate orders");
    }

    [TestMethod]
    public void OccupiedTileAtSubmissionRetainsFailedOrderWithoutRedirecting()
    {
        var order = new GlobalConstruction.Order(Lab, First);
        var preview = GlobalConstruction.Plan(Player, new[] { order });
        Assert.IsTrue(First.Construction.Enqueue(Factory, preview[0].Destination.Tile));
        var failed = GlobalConstruction.Submit(Player, preview);
        Assert.AreEqual(1, failed.Length);
        Assert.AreSame(order, failed[0]);
        Assert.IsFalse(First.BuildingInQueue(Lab.BID));
        Assert.IsFalse(Second.BuildingInQueue(Lab.BID));
    }

    [TestMethod]
    public void ManualForeignColonyIsRejected()
    {
        Planet foreign = AddHomeWorldToEmpire(new Vector2(100000), Enemy);
        var preview = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab, foreign) });
        Assert.IsNull(preview[0].Destination);
        Assert.AreEqual(1, GlobalConstruction.Submit(Player, preview).Length);
        Assert.AreEqual(0, foreign.ConstructionQueue.Count);
    }

    [TestMethod]
    public void NoValidTilesProducesBlockedOrder()
    {
        foreach (PlanetGridSquare tile in First.TilesList)
            tile.SetHabitable(false);
        var preview = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab, First) });
        Assert.IsNull(preview[0].Destination);
        Assert.IsTrue(preview[0].Candidates.All(c => c.Planet != First));
    }

    [TestMethod]
    public void ExistingQueueReducesRankForOtherwiseEqualColonies()
    {
        // Use equal snapshots to isolate queue pressure from governor preferences.
        First.Construction.Enqueue(Factory);
        First.ConstructionQueue[0].Cost = 1000000;
        var preview = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab) });
        Assert.AreSame(Second, preview[0].Destination.Planet);
    }

    [TestMethod]
    public void EmpireUniqueBuildingIsReservedAcrossColonies()
    {
        Building unique = ResourceManager.GetBuildingTemplate("Remnant Detection Array");
        Player.UnlockEmpireBuilding(unique.Name);
        var preview = GlobalConstruction.Plan(Player, new[]
        {
            new GlobalConstruction.Order(unique, First), new GlobalConstruction.Order(unique, Second)
        });
        Assert.IsNotNull(preview[0].Destination);
        Assert.IsNull(preview[1].Destination);
        Assert.AreEqual(1, GlobalConstruction.Submit(Player, preview).Length);
        var next = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(unique, Second) });
        Assert.IsNull(next[0].Destination, "Existing queues must also enforce the empire-wide limit");
    }

    [TestMethod]
    public void CancellingSubmittedBuildingMakesItsColonyEligibleAgain()
    {
        var order = new GlobalConstruction.Order(Lab, First);
        var preview = GlobalConstruction.Plan(Player, new[] { order });
        Assert.AreEqual(0, GlobalConstruction.Submit(Player, preview).Length);
        Assert.IsNull(GlobalConstruction.Plan(Player, new[] { order })[0].Destination);
        First.Construction.Cancel(First.ConstructionQueue[0]);
        Assert.IsNotNull(GlobalConstruction.Plan(Player, new[] { order })[0].Destination);
    }

    void MatchProduction()
    {
        foreach (Planet planet in new[] { First, Second })
        {
            planet.CType = Planet.ColonyType.Colony;
            planet.Population = 5000;
            planet.MineralRichness = 1;
            planet.Prod.Percent = 1;
            planet.Food.Percent = 0;
            planet.Res.Percent = 0;
            planet.Storage.Prod = 0;
            planet.UpdateIncomes();
        }
    }

    [TestMethod]
    public void DirectQueueReranksEachCopyAndRespectsUniqueLimits()
    {
        var first = GlobalConstruction.QueueBest(Player, Lab);
        var second = GlobalConstruction.QueueBest(Player, Lab);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreNotSame(first.Planet, second.Planet);
        Assert.IsNull(GlobalConstruction.QueueBest(Player, Lab));
        var entry = GlobalConstruction.GetCatalog(Player).Entries.Single(e => e.Building.BID == Lab.BID);
        Assert.AreEqual(0, entry.Built);
        Assert.AreEqual(2, entry.Queued);
        Assert.IsNull(entry.Best);
    }

    [TestMethod]
    public void CatalogCountsCompletedAndQueuedSeparatelyAndExcludesForeignColonies()
    {
        Planet foreign = AddHomeWorldToEmpire(new Vector2(100000), Enemy);
        foreach (Planet planet in new[] { First, foreign })
        {
            var tile = planet.TilesList.First(t => t.CanEnqueueBuildingHere(Lab));
            tile.PlaceBuilding(ResourceManager.CreateBuilding(planet, Lab), planet);
        }
        Assert.IsNotNull(GlobalConstruction.QueueBest(Player, Lab));
        var catalog = GlobalConstruction.GetCatalog(Player);
        var entry = catalog.Entries.Single(e => e.Building.BID == Lab.BID);
        Assert.AreEqual(1, entry.Built);
        Assert.AreEqual(1, entry.Queued);
        Assert.AreEqual(2, catalog.Colonies);
        Second.Construction.Cancel(Second.ConstructionQueue.Single(q => q.Building.BID == Lab.BID));
        entry = GlobalConstruction.GetCatalog(Player).Entries.Single(e => e.Building.BID == Lab.BID);
        Assert.AreEqual(1, entry.Built);
        Assert.AreEqual(0, entry.Queued);
        Assert.AreSame(Second, entry.Best.Planet);
    }

    [TestMethod]
    public void ProductionCapacityImprovesDeliveryButHeavyQueueCanOutweighIt()
    {
        MatchProduction();
        Second.MineralRichness = 4;
        Second.UpdateIncomes();
        var plan = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab) });
        Assert.AreSame(Second, plan[0].Destination.Planet);
        var slow = plan[0].Candidates.Single(c => c.Planet == First);
        Assert.IsTrue(plan[0].Destination.Production > slow.Production);
        Assert.IsTrue(plan[0].Destination.Turns < slow.Turns);
        Assert.IsTrue(Second.Construction.Enqueue(Factory));
        Second.ConstructionQueue[0].Cost = 1000000;
        plan = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab) });
        Assert.AreSame(First, plan[0].Destination.Planet);
    }

    [TestMethod]
    public void FreeSlotsAndSabotageAffectOtherwiseComparableDestinations()
    {
        MatchProduction();
        var firstFree = First.TilesList.Where(t => t.CanEnqueueBuildingHere(Lab)).ToArray();
        foreach (var tile in firstFree.Skip(1)) tile.SetHabitable(false);
        var plan = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab) });
        Assert.AreSame(Second, plan[0].Destination.Planet);
        Assert.AreEqual(1, plan[0].Candidates.Single(c => c.Planet == First).FreeSlots);
        Second.AddCrippledTurns(100);
        plan = GlobalConstruction.Plan(Player, new[] { new GlobalConstruction.Order(Lab) });
        Assert.AreSame(First, plan[0].Destination.Planet);
        Assert.IsTrue(plan[0].Candidates.Single(c => c.Planet == Second).AtRisk);
    }

    [TestMethod]
    public void StoredProductionCannotBeSpentInstantlyOrUsedTwice()
    {
        MatchProduction();
        First.Storage.Prod = 10000;
        var plan = GlobalConstruction.Plan(Player, new[]
        {
            new GlobalConstruction.Order(Factory, First), new GlobalConstruction.Order(Lab, First)
        });
        Assert.IsTrue(plan[0].Destination.Turns > 1, "Infrastructure must limit stockpile spending");
        Assert.IsTrue(plan[1].Destination.Turns > plan[0].Destination.Turns, "Earlier reserved work must consume time");
    }

    [TestMethod]
    [DataRow(1920, 1080)]
    [DataRow(1280, 720)]
    [DataRow(1024, 768)]
    public void DoubleClickQueuesImmediatelyAndRendersCatalog(int width, int height)
    {
        Game.Tick();
        ResourceManager.Blank ??= ResourceManager.Texture("blank");
        int oldWidth = GameBase.ScreenWidth, oldHeight = GameBase.ScreenHeight;
        void SetSize(int w, int h)
        {
            typeof(GameBase).GetProperty("ScreenWidth").SetValue(null, w);
            typeof(GameBase).GetProperty("ScreenHeight").SetValue(null, h);
            typeof(GameBase).GetProperty("ScreenSize").SetValue(null, new Vector2(w, h));
            typeof(GameBase).GetProperty("ScreenCenter").SetValue(null, new Vector2(w / 2f, h / 2f));
        }
        SetSize(width, height);
        try
        {
        var overlay = new EmpireUIOverlay(Player, Game.GraphicsDevice, Universe);
        using var management = new EmpireManagementScreen(Universe, overlay);
        using var screen = new GlobalConstructionScreen(management);
        screen.LoadContent();
        void Drain()
        {
            Universe.InvokePendingSimThreadActions();
            screen.PreUpdate(new UpdateTimes(1, 1), false, false);
            screen.Update(new UpdateTimes(0.016f, 1), true);
            screen.PerformLayout();
        }
        Drain();
        Assert.IsTrue(screen.Find<ScrollListBase>("BuildingCatalog", out var list));
        ScrollListItemBase Row(Building building)
            => ((IEnumerable)list.GetType().GetProperty("AllEntries").GetValue(list))
                .Cast<ScrollListItemBase>().Single(r => r.Name == $"Building-{building.BID}");
        list.OnItemClicked(Row(Lab));
        Assert.AreEqual(0, First.ConstructionQueue.Count + Second.ConstructionQueue.Count, "Single click only selects");
        list.OnItemDoubleClicked(Row(Lab));
        list.OnItemDoubleClicked(Row(Lab)); // Busy guard prevents duplicate handling.
        Assert.AreEqual(0, First.ConstructionQueue.Count + Second.ConstructionQueue.Count, "Queue mutations await simulation thread");
        Drain();
        Assert.AreEqual(1, First.ConstructionQueue.Count + Second.ConstructionQueue.Count);
        list.OnItemDoubleClicked(Row(Factory));
        Drain();
        Assert.AreEqual(2, First.ConstructionQueue.Count + Second.ConstructionQueue.Count);
        var snapshot = (GlobalConstruction.CatalogSnapshot)typeof(GlobalConstructionScreen)
            .GetField("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(screen);
        Assert.AreEqual(1, snapshot.Entries.Single(e => e.Building.BID == Lab.BID).Queued);
        Assert.AreEqual(1, snapshot.Entries.Single(e => e.Building.BID == Factory.BID).Queued);
        Assert.IsTrue(list.RectF.Bottom < height - 140, "Catalog must leave room for the detail panel");
        // Capture the selected row and the updated construction counts, as if
        // the player had scrolled to it before double-clicking.
        typeof(ScrollListBase).GetField("VisibleItemsBegin", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(list, 6);
        list.PerformLayout();

        var device = Game.GraphicsDevice;
        using var target = new RenderTarget2D(device, screen.ScreenWidth, screen.ScreenHeight, false, SurfaceFormat.Color, DepthFormat.None);
        var previous = device.GetRenderTargets();
        try
        {
            device.SetRenderTarget(target);
            device.Clear(Microsoft.Xna.Framework.Color.Black);
            screen.Draw(Game.Manager.SpriteBatch, new DrawTimes());
        }
        finally { device.SetRenderTargets(previous); }
        string path = Path.GetFullPath(Path.Combine(StarDriveTestContext.StarDriveAbsolutePath,
            $"../output/imagegen/empire-global-construction-v2-{width}x{height}.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var file = File.Create(path)) target.SaveAsPng(file, target.Width, target.Height);
        TestContext.AddResultFile(path);

        Assert.IsTrue(screen.Find<UITextEntry>("BuildingSearch", out var search));
        search.OnTextChanged("does-not-exist");
        Assert.AreEqual(0, ((IEnumerable)list.GetType().GetProperty("AllEntries").GetValue(list)).Cast<object>().Count());
        search.OnTextChanged("Research");
        Assert.IsNotNull(Row(Lab));
        }
        finally { SetSize(oldWidth, oldHeight); }
    }
}
