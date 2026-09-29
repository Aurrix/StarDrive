using SDGraphics;

namespace Ship_Game;

public sealed partial class Empire
{
    // Transit treaties do not grant permission to settle someone else's territory.
    public bool CanProvokeBorderOwner(Empire owner)
    {
        if (isPlayer || IsFaction || owner == null || owner == this || !IsKnown(owner))
            return false;
        var rel = GetRelations(owner);
        return !IsPacifist && !rel.Treaty_Alliance && !rel.Treaty_NAPact && !rel.Treaty_Peace && !rel.Treaty_OpenBorders
            && rel.Trust < 50 && rel.Threat < 0
            && CurrentMilitaryStrength > 0
            && CurrentMilitaryStrength > owner.CurrentMilitaryStrength * 1.5f;
    }

    public bool ShouldRespectColonialClaim(Empire owner)
        => !Universe.P.DisablePoliticalBorders && owner != null && owner != this
        && !owner.IsFaction && !owner.IsDefeated && owner.InfluenceActive && IsKnown(owner)
        && !IsAtWarWith(owner) && !CanProvokeBorderOwner(owner);

    public bool IsContestedBorderAt(Vector2 point)
    {
        if (Universe.P.DisablePoliticalBorders || !IsInBorderTerritory(point)) return false;
        foreach (Empire other in Universe.Empires)
            if (other != this && !other.IsDefeated && other.InfluenceActive
                && BordersOverlapAt(other, point) && other.IsInBorderTerritory(point))
                return true;
        return false;
    }
}
