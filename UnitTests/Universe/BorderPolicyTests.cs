using Microsoft.VisualStudio.TestTools.UnitTesting;
using SDGraphics;
using Ship_Game;
using Ship_Game.AI;
using Ship_Game.Gameplay;
using Ship_Game.Ships;
using Ship_Game.Ships.Components;
using Ship_Game.Universe;

namespace UnitTests.Universe;

[TestClass]
public class BorderPolicyTests : StarDriveTest
{
    public BorderPolicyTests()
    {
        CreateUniverseAndPlayerEmpire();
        Player.GetRelations(Enemy).AtWar = false;
        Enemy.GetRelations(Player).AtWar = false;
    }

    void Border(Empire empire, Vector2 center, float radius = 100000)
    {
        if (center == Vector2.Zero) center = new Vector2(1000, 1000);
        Planet planet = AddDummyPlanetToEmpire(center, empire);
        empire.BorderNodes = new[] { new Empire.InfluenceNode(planet.System, radius, true) };
        empire.PreparedBorders = null;
    }

    [TestMethod]
    public void AiExoticStationsRequireOwnTerritoryEvenWithOpenBorders()
    {
        Border(Player, new Vector2(1000, 1000));
        Border(Enemy, new Vector2(300000, 0));
        Enemy.SignTreatyWith(Player, TreatyType.OpenBorders);
        Assert.IsFalse(ExplorableGameObject.CanBuildExoticStationAt(Enemy, Vector2.Zero));
        Assert.IsFalse(ExplorableGameObject.CanBuildExoticStationAt(Enemy, new Vector2(600000, 0)));
        Assert.IsTrue(ExplorableGameObject.CanBuildExoticStationAt(Enemy, new Vector2(300000, 0)));
        UState.P.DisablePoliticalBorders = true;
        Assert.IsTrue(ExplorableGameObject.CanBuildExoticStationAt(Enemy, Vector2.Zero));
    }

    [TestMethod]
    public void ExoticStationLimitsCountOtherOwnersAndAllowRefitReplacement()
    {
        Planet planet = AddDummyPlanet(new Vector2(1000, 1000));
        Ship research = SpawnShip(Player.data.DefaultResearchStation, Player, planet.Position);
        research.TetherToPlanet(planet);
        Ship mining = SpawnShip(Player.data.DefaultMiningStation, Player, planet.Position);
        mining.TetherToPlanet(planet);
        Assert.IsTrue(planet.HasExoticStation(Enemy, mining: false));
        Assert.IsTrue(planet.HasExoticStation(Enemy, mining: true));
        Assert.IsFalse(planet.HasExoticStation(Player, mining: false, except: research));
        Assert.IsFalse(planet.HasExoticStation(Player, mining: true, except: mining));
        Assert.IsFalse(planet.System.HasExoticStation(Enemy, mining: false), "Planet research must not occupy the star's slot");
        Assert.AreEqual(1, Mineable.MaximumMiningStations);
    }

    [TestMethod]
    public void PendingStationGoalsReserveTheBodyAcrossEmpires()
    {
        Planet planet = AddDummyPlanet(new Vector2(1000, 1000));
        var goal = new Ship_Game.Commands.Goals.ProcessResearchStation(Player, planet);
        Player.AI.AddGoal(goal);
        Assert.IsTrue(planet.HasExoticStationGoal(Enemy, mining: false));
        Assert.IsFalse(planet.HasExoticStationGoal(Player, mining: false, except: goal));
        Assert.IsFalse(planet.HasExoticStationGoal(Enemy, mining: true));
    }

    [TestMethod]
    public void StationDeploymentRechecksBordersAndExistingStations()
    {
        Vector2 site = new Vector2(300000, 0);
        Border(Enemy, site);
        Planet planet = AddDummyPlanet(site);
        var goal = new Ship_Game.Commands.Goals.DeepSpaceBuildGoal(GoalType.BuildOrbital, Enemy)
        {
            Build = new Ship_Game.Commands.Goals.BuildableShip(Player.data.DefaultResearchStation),
            TetherPlanet = planet,
            TargetSystem = planet.System
        };
        Assert.IsTrue(goal.ExoticStationSiteValid());
        Enemy.BorderNodes = System.Array.Empty<Empire.InfluenceNode>();
        Enemy.PreparedBorders = null;
        Assert.IsFalse(goal.ExoticStationSiteValid(), "Losing territory invalidates an in-flight build");
        Border(Enemy, site);
        Ship station = SpawnShip(Player.data.DefaultResearchStation, Player, site);
        station.TetherToPlanet(planet);
        Assert.IsFalse(goal.ExoticStationSiteValid(), "Another owner's deployed station occupies the slot");
    }

    [TestMethod]
    public void DisabledBordersRemoveMovementAndColonizationRestrictions()
    {
        Border(Enemy, new Vector2(300000, 0));
        Ship scout = SpawnShip("Vulcan Scout", Player, Vector2.Zero);
        Assert.IsFalse(GravityWellRouter.IsDestinationAccessible(scout, new Vector2(300000, 0)));
        UState.HidePoliticalBorders = true;
        Assert.IsFalse(GravityWellRouter.IsDestinationAccessible(scout, new Vector2(300000, 0)), "Visibility toggle must not change rules");
        UState.P.DisablePoliticalBorders = true;
        Assert.IsTrue(GravityWellRouter.IsDestinationAccessible(scout, new Vector2(300000, 0)));
        Assert.IsFalse(GravityWellRouter.CrossesClosedBorder(scout, Vector2.Zero, new Vector2(600000, 0)));
        Assert.IsFalse(GravityWellRouter.IsInsideClosedBorderClaim(Player, new Vector2(300000, 0)));
    }

    [TestMethod]
    public void TransitTreatiesDoNotGrantColonizationRights()
    {
        Border(Player, Vector2.Zero);
        Enemy.SignTreatyWith(Player, TreatyType.OpenBorders);
        Enemy.GetRelations(Player).Trust = 80;
        Assert.IsTrue(Enemy.HasBorderAccessTo(Player));
        Assert.IsTrue(GravityWellRouter.IsInsideClosedBorderClaim(Enemy, Vector2.Zero));
    }

    [TestMethod]
    public void OpenBordersGraceAndNeutralAccessPreventImmediateCancellation()
    {
        var rel = Player.GetRelations(Enemy);
        Player.SignTreatyWith(Enemy,TreatyType.OpenBorders);
        rel.Trust = 0;
        rel.AddAngerDiplomaticConflict(50);
        rel.TotalAnger = 50;
        Assert.IsFalse(rel.ShouldCancelOpenBordersByTrust,"Newly signed access needs a grace period");
        for (int i = 0; i < 50; ++i) rel.AdvanceRelationshipTurn(Player,Enemy);
        rel.Trust = 0;
        rel.TotalAnger = 50;
        Assert.IsTrue(rel.ShouldCancelOpenBordersByTrust,"Sustained hostility can end mature access");
        rel.Anger_DiplomaticConflict = 0;
        rel.Anger_MilitaryConflict = 0;
        rel.Anger_TerritorialConflict = 0;
        rel.Anger_FromShipsInOurBorders = 0;
        rel.TotalAnger = 0;
        Assert.IsFalse(rel.ShouldCancelOpenBordersByTrust,"Neutral passage does not require friendship");
        Player.BreakTreatyWith(Enemy,TreatyType.OpenBorders);
        Player.SignTreatyWith(Enemy,TreatyType.OpenBorders);
        Assert.AreEqual(0,rel.TurnsInOpenBorders);
    }

    [TestMethod]
    public void ProvocationRequiresStrengthAndLowTrustWithoutProtectedTreaties()
    {
        Enemy.data.DiplomaticPersonality = new DTrait { Name = "Aggressive" };
        var rel = Enemy.GetRelations(Player);
        rel.Trust = 0; rel.Threat = -50;
        Enemy.CurrentMilitaryStrength = 2000;
        Player.CurrentMilitaryStrength = 1000;
        Assert.IsTrue(Enemy.CanProvokeBorderOwner(Player));
        Assert.IsFalse(Enemy.ShouldRespectColonialClaim(Player));
        rel.Trust = 80;
        Assert.IsFalse(Enemy.CanProvokeBorderOwner(Player));
        rel.Trust = 0; rel.Threat = 50;
        Assert.IsFalse(Enemy.CanProvokeBorderOwner(Player));
        rel.Threat = -50;
        Enemy.SignTreatyWith(Player, TreatyType.OpenBorders);
        Assert.IsFalse(Enemy.CanProvokeBorderOwner(Player),"Transit partners must not provoke colonial disputes");
        Assert.IsTrue(Enemy.ShouldRespectColonialClaim(Player));
        rel.SetTreaty(Enemy, TreatyType.OpenBorders, false);
        Player.GetRelations(Enemy).SetTreaty(Player, TreatyType.OpenBorders, false);
        Enemy.SignTreatyWith(Player, TreatyType.NonAggression);
        Assert.IsFalse(Enemy.CanProvokeBorderOwner(Player));
    }

    [TestMethod]
    public void EmergencyPassageIsShipSpecificAndExpires()
    {
        Border(Player, Vector2.Zero);
        Ship stranded = SpawnShip("Vulcan Scout", Enemy, Vector2.Zero);
        Ship other = SpawnShip("Vulcan Scout", Enemy, new Vector2(300000, 0));
        stranded.RequestBorderPassage(Player);
        Assert.IsTrue(Enemy.GetRelations(Player).BorderAccessRequested);
        Assert.IsFalse(stranded.HasBorderAccessTo(Player));
        Enemy.GetRelations(Player).BorderAccessRefusals = 1;
        stranded.RequestBorderPassage(Player);
        Assert.IsTrue(stranded.HasBorderAccessTo(Player));
        Assert.IsFalse(other.HasBorderAccessTo(Player));
        Assert.IsFalse(Enemy.HasBorderAccessTo(Player));
        UState.StarDate += 2;
        Assert.IsFalse(stranded.HasBorderAccessTo(Player));
    }

    [TestMethod]
    public void BorderOwnershipDoesNotRevealUnexploredSystems()
    {
        AddDummyPlanetToEmpire(new Vector2(1000, 1000), Player);
        Planet hidden = AddDummyPlanet(new Vector2(100000, 0));
        Player.UpdateContactsAndBorders(Universe, FixedSimTime.Zero);
        Assert.IsTrue(Player.IsInBorderTerritory(hidden.System.Position));
        Assert.IsFalse(hidden.System.IsExploredBy(Player));
        Assert.IsFalse(hidden.IsExploredBy(Player));
    }

    [TestMethod]
    public void StationsTransferOnlyAfterStableUncontestedControl()
    {
        Border(Enemy, Vector2.Zero);
        Ship station = SpawnShip(Player.data.DefaultMiningStation, Player, Vector2.Zero);
        station.LoyaltyTracker.Update(station);
        UState.Objects.UpdateLists(true);
        var transfers = new BorderStationOwnership();
        transfers.Update(UState, 4);
        Assert.AreEqual(LoyaltyChanges.Type.None, station.LoyaltyTracker.ChangeType);
        transfers.Update(UState, 1);
        Assert.AreEqual(LoyaltyChanges.Type.AbsorbedNotify, station.LoyaltyTracker.ChangeType);
        station.LoyaltyTracker.Update(station);
        Assert.AreSame(Enemy, station.Loyalty);
    }

    [TestMethod]
    public void ContestedClaimsAllowAiTransitButDoNotTransferStations()
    {
        Border(Player, new Vector2(1000, 1000));
        Border(Enemy, new Vector2(2000, 1000));
        Player.GetRelations(Enemy).AtWar = true;
        Enemy.GetRelations(Player).AtWar = true;
        Empire visitor = UState.CreateEmpire(ResourceManager.MajorRaces[2], isPlayer: false);
        Empire.SetRelationsAsKnown(visitor, Player);
        Empire.SetRelationsAsKnown(visitor, Enemy);
        Ship scout = SpawnShip("Vulcan Scout", visitor, Vector2.Zero);
        Assert.IsTrue(Player.IsContestedBorderAt(Vector2.Zero));
        Assert.IsFalse(visitor.HasBorderAccessTo(Player));
        Assert.IsTrue(GravityWellRouter.IsDestinationAccessible(scout, Vector2.Zero));
        Assert.IsFalse(GravityWellRouter.CrossesClosedBorder(scout, Vector2.Zero, new Vector2(10000, 0)));
        Ship station = SpawnShip(Player.data.DefaultResearchStation, Player, Vector2.Zero);
        UState.Objects.UpdateLists(true);
        new BorderStationOwnership().Update(UState, 10);
        Assert.AreEqual(LoyaltyChanges.Type.None, station.LoyaltyTracker.ChangeType);
        Assert.AreSame(Player, station.Loyalty);
    }

    [TestMethod]
    public void DisablingBordersCancelsPendingStationTransfer()
    {
        Border(Enemy, Vector2.Zero);
        Ship station = SpawnShip(Player.data.DefaultResearchStation, Player, Vector2.Zero);
        UState.Objects.UpdateLists(true);
        var transfers = new BorderStationOwnership();
        transfers.Update(UState, 4);
        UState.P.DisablePoliticalBorders = true;
        transfers.Update(UState, 10);
        Assert.AreEqual(LoyaltyChanges.Type.None, station.LoyaltyTracker.ChangeType);
        UState.P.DisablePoliticalBorders = false;
        transfers.Update(UState, 1);
        Assert.AreEqual(LoyaltyChanges.Type.None, station.LoyaltyTracker.ChangeType);
        transfers.Update(UState, 4);
        station.LoyaltyTracker.Update(station);
        Assert.AreSame(Enemy, station.Loyalty);
    }

    [TestMethod]
    public void PassageRequestsNegotiateTreatiesBetweenAiEmpires()
    {
        Empire recipient = UState.CreateEmpire(ResourceManager.MajorRaces[2], isPlayer: false);
        Empire.SetRelationsAsKnown(Enemy, recipient);
        var rel = Enemy.GetRelations(recipient);
        rel.Trust = 100;
        recipient.GetRelations(Enemy).Trust = 100;
        rel.BorderAccessRequested = true;
        Enemy.AI.UpdateBorderDiplomacy();
        Assert.IsTrue(Enemy.IsOpenBordersTreaty(recipient));
        Assert.IsTrue(Enemy.IsTradeTreaty(recipient));
        Assert.IsFalse(rel.BorderAccessRequested);
        Assert.AreEqual(0, rel.BorderAccessRefusals);
    }

    [TestMethod]
    public void BorderInfluenceDoesNotModifyWarpSpeed()
    {
        Ship scout = SpawnShip("Vulcan Scout", Player, Vector2.Zero);
        Player.data.Traits.InBordersSpeedBonus = 5;
        UState.P.EnemyFTLModifier = 0.1f;
        var influence = typeof(Ship).GetField("CurrentInfluenceStatus",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        influence.SetValue(scout, InfluenceStatus.Neutral);
        float neutral = scout.SetMaxFTLSpeed();
        influence.SetValue(scout, InfluenceStatus.Friendly);
        Assert.AreEqual(neutral, scout.SetMaxFTLSpeed());
        influence.SetValue(scout, InfluenceStatus.Enemy);
        Assert.AreEqual(neutral, scout.SetMaxFTLSpeed());
    }

    [TestMethod]
    public void RepeatedRefusalsCanEscalateAnEnclaveButFriendshipPreventsWar()
    {
        Planet capital = AddDummyPlanetToEmpire(new Vector2(-1500000, 1000), Enemy);
        Enemy.SetCapital(capital);
        AddDummyPlanetToEmpire(new Vector2(1000, 1000), Enemy);
        Border(Player, new Vector2(1000, 1000), 800000);
        Enemy.data.DiplomaticPersonality = new DTrait { Name = "Aggressive" };
        var rel = Enemy.GetRelations(Player);
        rel.Trust = 80; rel.Threat = -50;
        Enemy.CurrentMilitaryStrength = 2000;
        Player.CurrentMilitaryStrength = 1000;
        rel.BorderAccessRefusals = 3;
        Enemy.AI.UpdateBorderDiplomacy();
        Assert.IsFalse(rel.PreparingForWar);
        rel.Trust = 0;
        Enemy.AI.UpdateBorderDiplomacy();
        Assert.IsTrue(rel.PreparingForWar);
        Assert.AreEqual(WarType.BorderConflict, rel.PreparingForWarType);
    }

    [TestMethod]
    public void ResearchStationsKeepOperatingAfterBorderHandover()
    {
        Planet target = AddDummyPlanet(new Vector2(1000, 1000));
        target.SetResearchable(true, UState);
        Ship station = SpawnShip(Player.data.DefaultResearchStation, Player, target.Position);
        station.TetherToPlanet(target);
        var oldGoal = new Ship_Game.Commands.Goals.ProcessResearchStation(Player, station);
        // The new owner already has research rights here. Border acquisitions
        // must not be discarded as duplicate captured stations.
        UState.AddEmpireToResearchableList(Enemy, target);
        Assert.IsFalse(target.CanBeResearchedBy(Enemy));
        Border(Enemy, new Vector2(1000, 1000));
        UState.Objects.UpdateLists(true);
        new BorderStationOwnership().Update(UState, 5);
        station.LoyaltyTracker.Update(station);
        oldGoal.Evaluate();
        Assert.IsTrue(station.Active);
        Assert.AreSame(Enemy, station.Loyalty);
        Assert.IsTrue(Enemy.AI.HasGoal(g => g.IsResearchStationGoal(target)));
        Assert.IsFalse(station.ResearchStationAcquiredByBorder);
    }
}
