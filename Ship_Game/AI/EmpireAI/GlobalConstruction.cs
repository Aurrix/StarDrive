using System;
using System.Collections.Generic;
using System.Linq;

namespace Ship_Game;

// All snapshots, ranking and queue mutations run on the simulation thread.
public static class GlobalConstruction
{
    public sealed class CatalogEntry
    {
        public Building Building;
        public int Built;
        public int Queued;
        public Candidate Best;
        public int EligibleColonies;
    }

    public sealed class CatalogSnapshot
    {
        public CatalogEntry[] Entries;
        public int Colonies;
        public int TotalBuilt;
        public int TotalQueued;
    }

    public static CatalogSnapshot GetCatalog(Empire empire)
    {
        var entries = new Dictionary<int, CatalogEntry>();
        CatalogEntry Entry(Building building)
        {
            if (!entries.TryGetValue(building.BID, out CatalogEntry entry))
                entries.Add(building.BID, entry = new CatalogEntry { Building = ResourceManager.GetBuildingTemplate(building.BID) });
            return entry;
        }
        foreach (Building building in empire.GetUnlockedBuildings()) Entry(building);
        foreach (Planet planet in empire.GetPlanets())
        {
            foreach (Building building in planet.Buildings) ++Entry(building).Built;
            foreach (QueueItem item in planet.ConstructionQueue)
                if (item.isBuilding && !item.IsCancelled) ++Entry(item.Building).Queued;
        }
        var eligibility = Eligibility(empire);
        var reserved = new List<Placement>();
        foreach (CatalogEntry entry in entries.Values)
        {
            Candidate[] candidates = Rank(empire, entry.Building, reserved, eligibility);
            entry.Best = candidates.FirstOrDefault();
            entry.EligibleColonies = candidates.Length;
        }
        return new CatalogSnapshot
        {
            Entries = entries.Values.OrderBy(e => e.Building.TranslatedName.Text, StringComparer.CurrentCulture).ToArray(),
            Colonies = empire.GetPlanets().Count,
            TotalBuilt = entries.Values.Sum(e => e.Built), TotalQueued = entries.Values.Sum(e => e.Queued)
        };
    }

    // Rank again at execution time. No UI snapshot is trusted to reserve a tile.
    // Consecutive double-clicks see previous enqueues and can choose another planet.
    public static Candidate QueueBest(Empire empire, Building building)
    {
        Placement[] plan = Plan(empire, new[] { new Order(building) });
        return Submit(empire, plan).Length == 0 ? plan[0].Destination : null;
    }

    static Dictionary<Planet, HashSet<int>> Eligibility(Empire empire)
        => empire.GetPlanets().ToDictionary(p => p, p => p.GetBuildingsWeCanBuildHere().Select(b => b.BID).ToHashSet());

    public sealed class Order
    {
        public readonly Building Building;
        public readonly Planet PreferredPlanet;
        public Order(Building building, Planet preferredPlanet = null)
        {
            Building = building;
            PreferredPlanet = preferredPlanet;
        }
    }

    public sealed class Candidate
    {
        public Planet Planet;
        public PlanetGridSquare Tile;
        public float Score;
        public float Turns;
        public float Production;
        public int FreeSlots;
        public int QueueDepth;
        public bool AtRisk;
        public string Reason;
    }

    public sealed class Placement
    {
        public Order Order;
        public Candidate Destination;
        public Candidate[] Candidates;
        public string Problem;
    }

    public static Placement[] Plan(Empire empire, IEnumerable<Order> orders)
    {
        var placements = new List<Placement>();
        var eligibility = Eligibility(empire);
        foreach (Order order in orders)
        {
            Candidate[] candidates = Rank(empire, order.Building, placements, eligibility);
            Candidate destination = order.PreferredPlanet == null ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(c => c.Planet == order.PreferredPlanet);
            placements.Add(new Placement
            {
                Order = order, Candidates = candidates, Destination = destination,
                Problem = destination != null ? "" : order.PreferredPlanet != null
                    ? "Selected colony is no longer eligible" : "No eligible colony or free tile"
            });
        }
        return placements.ToArray();
    }

    static Candidate[] Rank(Empire empire, Building building, List<Placement> reserved,
        Dictionary<Planet, HashSet<int>> eligibility)
    {
        var candidates = new List<Candidate>();
        foreach (Planet planet in empire.GetPlanets())
        {
            if (!eligibility[planet].Contains(building.BID))
                continue;
            Placement[] local = reserved.Where(p => p.Destination?.Planet == planet).ToArray();
            if (building.Unique && local.Any(p => p.Order.Building.BID == building.BID))
                continue;
            if (building.BuildOnlyOnce && reserved.Any(p => p.Destination != null && p.Order.Building.BID == building.BID))
                continue;
            if (building.IsCapitalOrOutpost && local.Any(p => p.Order.Building.IsCapitalOrOutpost))
                continue;
            if (building.IsTerraformer && planet.TerraformersHere
                + planet.ConstructionQueue.Count(q => q.isBuilding && q.Building.IsTerraformer)
                + local.Count(p => p.Order.Building.IsTerraformer) >= planet.TerraformerLimit)
                continue;
            PlanetGridSquare[] slots = planet.TilesList.Where(t => t.CanEnqueueBuildingHere(building)
                && !local.Any(p => p.Destination.Tile == t)).ToArray();
            // Preserve habitable land when an anywhere-building can use bare rock.
            PlanetGridSquare tile = slots.OrderBy(t => building.CanBuildAnywhere && t.Habitable ? 1 : 0).FirstOrDefault();
            if (tile == null)
                continue;

            float output = planet.GlobalConstructionOutput(building);
            float turns = planet.GlobalConstructionTurns(building, local.Select(p => p.Order.Building));
            float rawBenefit = planet.GlobalBuildingBenefit(building);
            // Newly initialized colonies may not yet have empire income totals.
            // Their governor ratios can be non-finite; rank by queue time until
            // those statistics are available instead of propagating NaN to UI.
            float benefit = float.IsFinite(rawBenefit) ? Math.Max(0, rawBenefit) : 0;
            bool specialized = building.ProducesProduction && planet.CType == Planet.ColonyType.Industrial
                || building.ProducesResearch && planet.CType == Planet.ColonyType.Research
                || building.ProducesFood && planet.CType == Planet.ColonyType.Agricultural
                || building.IsMilitary && planet.CType == Planet.ColonyType.Military;
            bool atRisk = planet.SpaceCombatNearPlanet || planet.RecentCombat || planet.IsCrippled;
            // Log scaling prevents a large governor need from overwhelming delivery
            // time. Prefer spare land and matching specialization; penalize combat.
            float spaceFactor = 0.8f + 0.2f * Math.Min(slots.Length, 5) / 5;
            float value = (1 + MathF.Log(1 + benefit)) * spaceFactor * (specialized ? 1.2f : 1) * (atRisk ? 0.35f : 1);
            float score = float.IsPositiveInfinity(turns) ? 0 : value / (1 + turns / 12);
            int queueDepth = planet.ConstructionQueue.Count(q => !q.IsCancelled) + local.Length;
            candidates.Add(new Candidate
            {
                Planet = planet, Tile = tile, Turns = turns, Score = score,
                Production = output, FreeSlots = slots.Length, QueueDepth = queueDepth, AtRisk = atRisk,
                Reason = $"{planet.CType} colony{(specialized ? ": specialization match" : "")}. "
                    + $"{output:0.#} projected production/turn; {slots.Length} valid slots; {queueDepth} queued items. "
                    + (atRisk ? "Combat or sabotage reduces suitability." : "No current combat or sabotage.")
            });
        }
        return candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Turns)
            .ThenBy(c => c.Planet.Name, StringComparer.Ordinal).ToArray();
    }

    // Returns only failed orders. Successful copies now belong to real colony queues.
    public static Order[] Submit(Empire empire, IEnumerable<Placement> placements)
    {
        var failed = new List<Order>();
        foreach (Placement placement in placements)
        {
            Candidate target = placement.Destination;
            Building building = placement.Order.Building;
            if (target == null || target.Planet.Owner != empire
                || !target.Planet.GetBuildingsWeCanBuildHere().Any(b => b.BID == building.BID)
                || !target.Tile.CanEnqueueBuildingHere(building)
                || !target.Planet.Construction.Enqueue(building, target.Tile, playerAdded: true))
                failed.Add(placement.Order);
        }
        return failed.ToArray();
    }
}
