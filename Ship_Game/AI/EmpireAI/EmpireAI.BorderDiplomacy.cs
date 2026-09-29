using System;
using SDGraphics;
using SDUtils;
using Ship_Game.Gameplay;
using Ship_Game.GameScreens.DiplomacyScreen;

namespace Ship_Game.AI;

public sealed partial class EmpireAI
{
    // Called on the strategic turn, never from parallel ship movement or rendering.
    public void UpdateBorderDiplomacy()
    {
        Empire us = OwnerEmpire;
        if (us.isPlayer || us.IsFaction || us.Universe.P.DisablePoliticalBorders) return;
        foreach (Relationship rel in us.AllRelations)
        {
            Empire them = rel.Them;
            if (DoNotInteract(rel, them) || rel.AtWar) continue;
            if (rel.Treaty_OpenBorders || rel.Treaty_Alliance)
            {
                rel.BorderAccessRequested = false;
                rel.BorderAccessRefusals = 0;
                continue;
            }
            if (us.Universe.StarDate < rel.BorderAccessNextRequest)
                continue;

            Planet pocket = FindBorderPocket(them);
            if (!rel.BorderAccessRequested && pocket == null)
            {
                rel.BorderAccessNextRequest = us.Universe.StarDate + 1f;
                continue;
            }
            // Only sustained isolation and repeated refusals can justify a war.
            if (pocket != null && rel.BorderAccessRefusals >= 3 && IsEnclosedBy(pocket, them)
                && us.CanProvokeBorderOwner(them) && !us.IsAtWarWithMajorEmpire && !rel.PreparingForWar)
            {
                rel.PrepareForWar(WarType.BorderConflict, us);
                rel.BorderAccessNextRequest = us.Universe.StarDate + 10f;
                continue;
            }

            if (them.isPlayer && !us.Universe.Screen.CanShowDiplomacyScreen) continue;

            var ours = new Offer { OpenBorders = true, TradeTreaty = !rel.Treaty_Trade };
            var theirs = new Offer
            {
                OpenBorders = true, TradeTreaty = !rel.Treaty_Trade,
                AcceptDL = "Open Borders Accepted", RejectDL = "Open Borders Rejected"
            };
            // Offer a reciprocal treaty first; after a refusal, add a technology
            // they can actually research as a concrete incentive.
            if (rel.BorderAccessRefusals > 0)
            {
                if (TradableTechs(them, out var techs, forceAllTechs: true))
                {
                    TechEntry incentive = null;
                    foreach (var tech in techs)
                        if (!tech.IsMilitary() && (incentive == null || tech.Tech.Cost < incentive.Tech.Cost))
                            incentive = tech;
                    if (incentive != null) ours.TechnologiesOffered.Add(incentive.UID);
                }
            }
            if (pocket != null && rel.BorderAccessRefusals >= 2)
            {
                // Exchange outlying colonies only when both sides reconnect land.
                Planet exchange = FindExchangeColony(them);
                if (exchange != null && IsEnclosedBy(pocket, them))
                {
                    ours.ColoniesOffered.Add(pocket.Name);
                    theirs.ColoniesOffered.Add(exchange.Name);
                }
            }

            rel.BorderAccessRequested = false;
            rel.BorderAccessNextRequest = us.Universe.StarDate + 5f;
            if (them.isPlayer)
            {
                ours.AcceptDL = theirs.AcceptDL;
                ours.RejectDL = theirs.RejectDL;
                ours.ValueToModify = new Ref<bool>(() => rel.HaveRejected_OpenBorders,
                    rejected => FinishBorderOffer(rel, rejected));
                // The screen uses the recipient player's perspective.
                DiplomacyScreen.Show(us, "Offer Open Borders", theirs, ours);
            }
            else
            {
                them.AI.AnalyzeOffer(ours, theirs, us, Offer.Attitude.Pleading);
                FinishBorderOffer(rel, !rel.Treaty_OpenBorders);
            }
        }
    }

    static void FinishBorderOffer(Relationship rel, bool rejected)
    {
        rel.HaveRejected_OpenBorders = rejected;
        rel.BorderAccessRefusals = rejected ? rel.BorderAccessRefusals + 1 : 0;
    }

    Planet FindBorderPocket(Empire blocker)
    {
        Empire us = OwnerEmpire;
        Planet capital = us.Capital;
        if (capital == null) return null;
        foreach (Planet colony in us.GetPlanets())
        {
            if (colony == capital || colony.HasCapital) continue;
            Vector2 route = capital.Position - colony.Position;
            int steps = Math.Min(128, Math.Max(2, (int)(route.Length() / 10000f)));
            for (int i = 1; i < steps; ++i)
            {
                Vector2 point = colony.Position + route * (i / (float)steps);
                if (blocker.IsInBorderTerritory(point) && !blocker.IsContestedBorderAt(point))
                    return colony;
            }
        }
        return null;
    }

    bool IsEnclosedBy(Planet colony, Empire blocker)
    {
        float radius = OwnerEmpire.GetProjectorRadius() * Empire.MatureBorderRadiusMultiplier * 1.2f;
        if (OwnerEmpire.Capital == null || colony.Position.Distance(OwnerEmpire.Capital.Position) <= radius)
            return false;
        // Sample more finely than movement's border-crossing checks so ordinary
        // gaps in the enclosing ring do not become grounds for war.
        int samples = Math.Max(64, (int)Math.Ceiling(2 * Math.PI * radius
            / Math.Max(1000f, OwnerEmpire.GetProjectorRadius() * 0.1f)));
        for (int i = 0; i < samples; ++i)
        {
            float angle = i * ((float)Math.PI * 2 / samples);
            Vector2 point = colony.Position + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius;
            if (!blocker.IsInBorderTerritory(point) || blocker.IsContestedBorderAt(point)) return false;
        }
        return true;
    }

    Planet FindExchangeColony(Empire them)
    {
        foreach (Planet colony in them.GetPlanets())
            if (colony != them.Capital && !colony.HasCapital
                && OwnerEmpire.GetBorderClaimStrength(colony.Position) >= 0f)
                return colony;
        return null;
    }
}
