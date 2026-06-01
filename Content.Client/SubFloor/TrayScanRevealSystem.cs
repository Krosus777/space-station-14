using System.Linq;
using Content.Shared.SubFloor;
using Content.Shared.Tag;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Client.SubFloor;

public sealed class TrayScanRevealSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly TagSystem _tag = default!;

    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    public bool IsUnderRevealingEntity(EntityUid uid)
    {
        var gridUid = _transform.GetGrid(uid);
        if (gridUid is null)
            return false;

        var gridComp = Comp<MapGridComponent>(gridUid.Value);
        var position = _transform.GetGridOrMapTilePosition(uid);

        var grid = ((EntityUid)gridUid, gridComp);
        if (HasTrayScanReveal(grid, position))
            return true;

        return HasNearbyTrayScanReveal(uid);
    }

    private bool HasTrayScanReveal(Entity<MapGridComponent> ent, Vector2i position)
    {
        var anchoredEnum = _map.GetAnchoredEntities(ent, position);
        return anchoredEnum.Any(HasComp<TrayScanRevealComponent>);
    }

    private bool HasNearbyTrayScanReveal(EntityUid uid)
    {
        var mapPos = _transform.GetMapCoordinates(uid);
        var nearby = _lookup.GetEntitiesInRange(
            mapPos,
            0.75f,
            LookupFlags.Static | LookupFlags.Sundries | LookupFlags.Approximate);

        return nearby.Any(IsRevealSource);
    }

    private bool IsRevealSource(EntityUid entity)
    {
        return HasComp<TrayScanRevealComponent>(entity) &&
               (HasComp<OccluderComponent>(entity) || _tag.HasTag(entity, WallTag));
    }
}
