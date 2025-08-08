using System.Linq;
using Content.Shared.Forms;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Content.Shared.Paper;
using Robust.Server.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.Forms;

/// <summary>
/// Handles selecting a template for blank forms that can turn into various documents.
/// </summary>
public sealed class FormPickerSystem : EntitySystem
{
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;

    private static readonly ProtoId<TagPrototype> WriteTag = "Write";

    public override void Initialize()
    {
        SubscribeLocalEvent<FormPickerComponent, InteractUsingEvent>(OnInteractUsing, before: new[] { typeof(PaperSystem) });
        SubscribeLocalEvent<FormPickerComponent, FormPickerSelectFormMessage>(OnSelectForm);
    }

    private void OnInteractUsing(Entity<FormPickerComponent> entity, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!_tagSystem.HasTag(args.Used, WriteTag))
            return;

        EnsureComp<PaperFormComponent>(entity.Owner);

        if (entity.Comp.Forms.Count == 0 && entity.Comp.BasePrototype != null)
        {
            var baseId = entity.Comp.BasePrototype.Value.Id;
            foreach (var proto in _prototypeManager.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract)
                    continue;
                if (proto.Parents == null || !proto.Parents.Contains(baseId))
                    continue;
                if (!proto.Components.TryGetValue("Paper", out var comp))
                    continue;
                if (comp.Component is not PaperComponent paper)
                    continue;
                if (string.IsNullOrEmpty(paper.Content))
                    continue;

                entity.Comp.Forms.Add(new FormPickerComponent.FormOption
                {
                    Name = $"ent-{proto.ID}",
                    Template = paper.Content
                });
            }
        }

        var options = entity.Comp.Forms.Select(f => Loc.GetString(f.Name)).ToList();
        _uiSystem.SetUiState(entity.Owner, FormPickerUiKey.Key, new FormPickerBoundUserInterfaceState(options));
        _uiSystem.TryOpenUi(entity.Owner, FormPickerUiKey.Key, args.User);
        args.Handled = true;
    }

    private void OnSelectForm(Entity<FormPickerComponent> entity, ref FormPickerSelectFormMessage args)
    {
        var user = args.Actor;
        if (user == EntityUid.Invalid)
            return;

        if (args.Index < 0 || args.Index >= entity.Comp.Forms.Count)
            return;

        var choice = entity.Comp.Forms[args.Index];
        var form = EnsureComp<PaperFormComponent>(entity.Owner);
        form.Template = Loc.GetString(choice.Template);
        form.Filled = false;

        // Clear any existing paper content so the new template fully replaces it.
        _paperSystem.SetContent(entity.Owner, string.Empty);

        entity.Comp.Selected = true;
        Dirty(entity.Owner, entity.Comp);

        var fillEv = new FormFillRequestEvent(user);
        RaiseLocalEvent(entity.Owner, ref fillEv);

        _uiSystem.CloseUi(entity.Owner, FormPickerUiKey.Key, user);
    }
}
