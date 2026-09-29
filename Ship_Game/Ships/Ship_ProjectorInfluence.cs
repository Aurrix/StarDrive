using System;
using System.Collections.Generic;
using Ship_Game.Empires;
using Ship_Game.Gameplay;
using Ship_Game.Universe;
using Ship_Game.Data.Serialization;

namespace Ship_Game.Ships
{
    public partial class Ship
    {
        InfluenceStatus CurrentInfluenceStatus;
        [StarData] Empire EmergencyBorderOwner;
        [StarData] float EmergencyBorderUntil;
        [StarData] public bool ResearchStationAcquiredByBorder;

        public void RequestBorderPassage(Empire owner)
        {
            if (owner == null || Loyalty.isPlayer || Loyalty.IsFaction || owner.IsFaction
                || Loyalty.HasBorderAccessTo(owner)) return;
            var rel = Loyalty.GetRelations(owner);
            rel.BorderAccessRequested = true;
            // One refusal permits emergency escape; repeated refusals permit a
            // necessary passage. Exceptions are ship-specific and time-limited.
            // Never use emergency passage to claim a colony.
            if ((rel.BorderAccessRefusals > 0 && owner.IsInBorderTerritory(Position)
                    || rel.BorderAccessRefusals >= 2)
                && !ShipData.IsColonyShip)
            {
                EmergencyBorderOwner = owner;
                EmergencyBorderUntil = Universe.StarDate + 1f;
            }
        }

        void UpdateInfluenceStatus()
        {
            CurrentInfluenceStatus = Universe.Influence.GetInfluenceStatus(Loyalty, Position);
        }

        public bool IsInBordersOf(Empire empire)
        {
            return Universe.Influence.IsInInfluenceOf(empire, Position);
        }

        public IEnumerable<Empire> GetProjectorInfluenceEmpires()
        {
            return Universe.Influence.GetEmpireInfluences(Position);
        }

        /// <summary>
        /// Whether this ship may cross another empire's projected border.
        /// An undiscovered empire cannot enforce a border the ship does not know
        /// exists; crossing it enables the normal first-contact sensor flow.
        /// Warships may enter an enemy's territory during a declared war; peaceful
        /// access requires an alliance or open-borders treaty. Trade treaties grant
        /// the narrower access their civilian freighters need, without opening the
        /// border to the rest of the empire's navy. AI can deliberately trespass
        /// when sufficiently strong or after failed emergency access negotiations.
        /// </summary>
        public bool HasBorderAccessTo(Empire borderOwner)
        {
            return Loyalty.HasBorderAccessTo(borderOwner, civilianFreighter: IsFreighter)
                || !Loyalty.isPlayer && EmergencyBorderOwner == borderOwner
                && EmergencyBorderUntil > Universe.StarDate;
        }

        public bool IsInFriendlyProjectorRange => CurrentInfluenceStatus == InfluenceStatus.Friendly;
        public bool IsInHostileProjectorRange => CurrentInfluenceStatus == InfluenceStatus.Enemy;
    }
}
