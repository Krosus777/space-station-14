using Content.Shared.Paper;
using Robust.Shared.Localization;

namespace Content.Server.Paper;

/// <summary>
///     Loads template text from localization files onto a paper when editing begins.
/// </summary>
public sealed class PaperTemplateSystem : EntitySystem
{
    [Dependency] private readonly PaperSystem _paperSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PaperTemplateComponent, PaperFillRequestEvent>(OnFillRequest);
    }

    private void OnFillRequest(Entity<PaperTemplateComponent> entity, ref PaperFillRequestEvent args)
    {
        if (entity.Comp.Loaded)
            return;

        if (!TryComp(entity.Owner, out PaperComponent? paper))
            return;

        var text = Loc.GetString(entity.Comp.Template);
        _paperSystem.SetContent((entity.Owner, paper), text);
        entity.Comp.Loaded = true;
        Dirty(entity.Owner, entity.Comp);
    }
}
