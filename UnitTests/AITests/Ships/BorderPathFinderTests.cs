using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SDGraphics;
using Ship_Game;
using Ship_Game.AI;
using Ship_Game.Universe;
using UnitTests.Ships;

namespace UnitTests.AITests.Ships;

[TestClass]
public class BorderPathFinderTests
{
    [TestMethod]
    public void EscapesConcaveBayByInitiallyMovingAwayFromDestination()
    {
        Vector2 from = new(0, 0), to = new(250000, 0);
        bool Open(Vector2 p) => !(p.X >= -100000 && p.X <= 100000 && Math.Abs(p.Y) >= 80000 && Math.Abs(p.Y) <= 100000)
                            && !(p.X >= 80000 && p.X <= 100000 && Math.Abs(p.Y) <= 100000);
        bool Travel(Vector2 a, Vector2 b) => SegmentClear(a, b, Open);
        Assert.IsTrue(BorderPathFinder.TryFind(from, to, new Vector2(-300000), new Vector2(300000), 10000, Open, Travel, out Vector2[] route));
        Assert.IsTrue(route.Length > 0 && route[0].X < 0, "The first leg must head toward the bay mouth, away from the goal");
        AssertRoute(from, to, route, Open);
        int index = 0;
        Assert.AreEqual(route[0], GravityWellRouter.GetThrustTarget(route, ref index, new Vector2(10000, 0), from));
        Assert.AreEqual(0, index, "A farther waypoint must not be skipped");
    }

    [TestMethod]
    public void AlternatingBarriersRequireMoreThanOldRecursionLimit()
    {
        bool Open(Vector2 p)
        {
            for (int i = 0; i < 6; ++i)
                if (Math.Abs(p.X - (i * 70000 + 60000)) <= 10000
                    && (i % 2 == 0 ? p.Y < 50000 : p.Y > -50000)) return false;
            return true;
        }
        Vector2 from = new(0, 0), to = new(500000, 0);
        Assert.IsTrue(BorderPathFinder.TryFind(from, to, new Vector2(-20000, -100000), new Vector2(520000, 100000),
            10000, Open, (a, b) => SegmentClear(a, b, Open), out Vector2[] route));
        Assert.IsTrue(route.Length > 6);
        AssertRoute(from, to, route, Open);
    }

    [TestMethod]
    public void EnclosedLegalDestinationReturnsFailureWithoutPartialRoute()
    {
        bool Open(Vector2 p) => !(Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) >= 80000
                              && Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) <= 110000);
        Assert.IsFalse(BorderPathFinder.TryFind(new Vector2(-200000, 0), Vector2.Zero,
            new Vector2(-250000), new Vector2(250000), 15000, Open,
            (a, b) => SegmentClear(a, b, Open), out Vector2[] route));
        Assert.AreEqual(0, route.Length);
    }

    static bool SegmentClear(Vector2 a, Vector2 b, Func<Vector2, bool> open)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(a.Distance(b) / 1000f));
        for (int i = 0; i <= steps; ++i)
            if (!open(a + (b - a) * (i / (float)steps))) return false;
        return true;
    }

    static void AssertRoute(Vector2 from, Vector2 to, Vector2[] route, Func<Vector2, bool> open)
    {
        foreach (Vector2 next in route.Append(to))
        {
            Assert.IsTrue(SegmentClear(from, next, open), $"Route crosses a wall: {from} -> {next}");
            from = next;
        }
    }
}

[TestClass]
public class ColonyBorderRoutingTests : StarDriveTest
{
    readonly TestShip Colony;
    public ColonyBorderRoutingTests()
    {
        LoadStarterShips("Colony Ship");
        CreateUniverseAndPlayerEmpire();
        Player.GetRelations(Enemy).AtWar = false;
        Enemy.GetRelations(Player).AtWar = false;
        Player.AutoColonize = true;
        Colony = SpawnShip("Colony Ship", Player, new Vector2(1000, 1000), Vectors.Right);
    }

    void SetBorders(params Vector2[] positions)
    {
        var nodes = new Empire.InfluenceNode[positions.Length];
        var samples = new BorderField.Node[positions.Length];
        for (int i = 0; i < nodes.Length; ++i)
        {
            Planet p = AddDummyPlanetToEmpire(positions[i], Enemy);
            nodes[i] = new(p.System, 90000, true);
            samples[i] = new(positions[i], 90000, p.System.Id * 0.754877666f + Enemy.Id * 1.618033989f, 1f);
        }
        Enemy.BorderNodes = nodes;
        Enemy.PreparedBorders = new BorderSnapshot(nodes, samples, Enemy.GetProjectorRadius());
        Enemy.BorderConnections = Enemy.PreparedBorders.Connections;
    }

    [TestMethod]
    public void ColonyMissionAndQueuedDestinationSurviveRerouting()
    {
        Planet target = AddDummyPlanet(new Vector2(650000, 1000));
        Colony.AI.OrderColonization(target);
        Assert.IsTrue(Colony.AI.FindGoal(ShipAI.Plan.Colonize, out ShipAI.ShipGoal original));
        SetBorders(new Vector2(300000, -70000), new Vector2(300000, 70000));
        Assert.IsTrue(Colony.AI.TryRerouteCurrentLeg());
        Assert.IsTrue(Colony.AI.FindGoal(ShipAI.Plan.Colonize, out ShipAI.ShipGoal retained));
        Assert.AreSame(original, retained);
        Assert.IsFalse(original.IsDisposed);
        AssertLegalWaypoints();
        Colony.AI.OnSystemNewlyExplored(target.System);
        Assert.IsTrue(Colony.AI.FindGoal(ShipAI.Plan.Colonize, out retained));
        Assert.AreSame(original, retained);
        AssertLegalWaypoints();

        Colony.AI.OrderMoveTo(new Vector2(650000, 1000), Vectors.Up);
        Colony.AI.OrderMoveTo(new Vector2(650000, 350000), Vectors.Up, order: MoveOrder.AddWayPoint);
        Vector2[] destinations = Colony.AI.CopyWayPoints().Where(w => !w.IsDetour).Select(w => w.Position).ToArray();
        Assert.IsTrue(Colony.AI.TryRerouteCurrentLeg());
        CollectionAssert.AreEqual(destinations, Colony.AI.CopyWayPoints().Where(w => !w.IsDetour).Select(w => w.Position).ToArray());
    }

    [TestMethod]
    public void ColonyShipActuallyTravelsAroundConnectedBorders()
    {
        SetBorders(new Vector2(300000, -70000), new Vector2(300000, 70000));
        Planet target = AddDummyPlanet(new Vector2(650000, 1000));
        Colony.AI.OrderColonization(target);
        AssertLegalWaypoints();
        for (int frame = 0; frame < 60 * 900 && Colony.Position.Distance(target.Position) > 2000f; ++frame)
        {
            Colony.AI.Update(TestSimStep);
            Colony.Update(TestSimStep);
            Assert.IsFalse(Enemy.IsInBorderTerritory(Colony.Position), "Colony ship crossed a closed border");
        }
        Assert.IsTrue(Colony.Position.Distance(target.Position) <= 2000f,
            $"Colony ship did not reach its destination: {Colony.Position}, target {target.Position}, state {Colony.AI.State}");
    }

    void AssertLegalWaypoints()
    {
        Vector2 previous = Colony.Position;
        foreach (var waypoint in Colony.AI.CopyWayPoints())
        {
            Assert.IsFalse(GravityWellRouter.CrossesClosedBorder(Colony, previous, waypoint.Position),
                $"Invalid route leg: {previous} -> {waypoint.Position}");
            previous = waypoint.Position;
        }
    }
}
