using System.Collections.Generic;
using Ship_Game.Data.Serialization;
using Ship_Game.Utils;
using SDGraphics;
using Ship_Game.AI;
using Ship_Game.Ships;

namespace Ship_Game.Universe
{
    [StarDataType]
    public class ExplorableGameObject : GameObject
    {
        // this is a tiny bitset where Empire.Id is the bit index for Explored:true|false
        [StarData] SmallBitSet ExploredBy;

        [StarData] public bool IsResearchable { get; private set; }
        public ExplorableGameObject(int id, GameObjectType type) : base(id, type)
        {
        }

        public void SetExploredBy(Empire empire) => ExploredBy.Set(empire.Id);
        public bool IsExploredBy(Empire empire)  => ExploredBy.IsSet(empire.Id);

        public void SetResearchable(bool value, UniverseState universe)
        {
            IsResearchable = value;
            if (value)
                universe.AddResearchableSolarBody(this);
            else
                universe.RemoveResearchableSolarBody(this);
        }

        public bool CanBeResearchedBy(Empire empire)
        {
            if (!IsResearchable || IsResearchStationDeployedBy(empire))
                return false;

            if (!empire.AI.HasGoal(g => g.IsResearchStationGoal(this)))
                return true;

            return false;
        }

        public static bool CanBuildExoticStationAt(Empire empire, Vector2 position)
            => empire.isPlayer || empire.Universe.P.DisablePoliticalBorders
                || empire.IsInBorderTerritory(position);

        // A resource belongs to a solar body, not to each empire separately.
        public bool HasExoticStation(Empire empire, bool mining, Ship except = null)
        {
            IEnumerable<Ship> ships = this is Planet planet ? planet.OrbitalStations : ((SolarSystem)this).ShipList;
            foreach (Ship ship in ships)
            {
                if (ship == except || !ship.Active || ship.Dying) continue;
                if (this is SolarSystem && ship.GetTether() != null) continue;
                if (mining ? ship.IsMiningStation : ship.IsResearchStation) return true;
            }
            return false;
        }

        public bool HasExoticStationGoal(Empire empire, bool mining, Goal except = null)
        {
            foreach (Empire owner in empire.Universe.Empires)
                if (owner.AI.HasGoal(g => g != except && (mining
                    ? g.IsMiningOpsGoal(this as Planet) : g.IsResearchStationGoal(this))))
                    return true;
            return false;
        }

        public bool IsResearchStationDeployedBy(Empire empire)
        {
            // TryGetValue, not the throwing indexer: a save crossing a patch can restore
            // IsResearchable=true on a body whose ResearchableSolarBodies entry is absent.
            // Not in the map means no station is tracked there, so none is deployed.
            return IsResearchable
                && empire.Universe.ResearchableSolarBodies.TryGetValue(this, out HashSet<int> deployedBy)
                && deployedBy.Contains(empire.Id);
        }
    }
}
