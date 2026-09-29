using System.Collections.Generic;
using Ship_Game.Ships;
using Ship_Game.Ships.Components;

namespace Ship_Game.Universe;

// Debounce control changes and queue ownership through the normal safe transfer
// path. Contested and neutral territory never confiscate a station.
public sealed class BorderStationOwnership
{
    readonly Dictionary<Ship, (Empire Owner, float Seconds)> Candidates = new();

    public void Update(UniverseState universe, float seconds)
    {
        if (universe.P.DisablePoliticalBorders)
        {
            Candidates.Clear();
            return;
        }
        var remaining = new HashSet<Ship>();
        foreach (Ship station in universe.Ships)
        {
            if (!station.Active || station.Dying || !station.IsPlatformOrStation
                || !(station.IsMiningStation || station.IsResearchStation)
                || station.LoyaltyTracker.ChangeType != LoyaltyChanges.Type.None) continue;
            Empire controller = null;
            foreach (Empire empire in universe.Empires)
            {
                if (empire.IsDefeated || !empire.InfluenceActive || !empire.IsInBorderTerritory(station.Position)) continue;
                if (controller != null) { controller = null; break; }
                controller = empire;
            }
            if (controller == null || controller == station.Loyalty) continue;
            remaining.Add(station);
            Candidates.TryGetValue(station, out var previous);
            float held = previous.Owner == controller ? previous.Seconds + seconds : seconds;
            if (held >= 5f)
            {
                station.ResearchStationAcquiredByBorder = station.IsResearchStation;
                station.LoyaltyChangeByGift(controller);
                Candidates.Remove(station);
            }
            else Candidates[station] = (controller, held);
        }
        var stale = new List<Ship>();
        foreach (Ship station in Candidates.Keys)
            if (!remaining.Contains(station)) stale.Add(station);
        foreach (Ship station in stale) Candidates.Remove(station);
    }
}
