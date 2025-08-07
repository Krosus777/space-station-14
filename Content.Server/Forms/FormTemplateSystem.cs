using Content.Shared.Forms;
using Robust.Shared.Localization;

namespace Content.Server.Forms;

/// <summary>
/// Loads template text from localization files onto a form when editing begins.
/// </summary>
public sealed class FormTemplateSystem : EntitySystem
{
    [Dependency] private readonly FormDocumentSystem _formSystem = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FormTemplateComponent, FormFillRequestEvent>(OnFillRequest);
    }

    private void OnFillRequest(Entity<FormTemplateComponent> entity, ref FormFillRequestEvent args)
    {
        if (entity.Comp.Loaded)
            return;
        if (!TryComp(entity.Owner, out FormDocumentComponent? form))
            return;
        var text = Loc.GetString(entity.Comp.Template);
        _formSystem.SetContent((entity.Owner, form), text);
        entity.Comp.Loaded = true;
        Dirty(entity.Owner, entity.Comp);
    }
}
