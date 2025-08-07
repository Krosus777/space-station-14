using System.Linq;
using Robust.Shared.Prototypes;
using Content.Shared.Paper;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Localization;
using static Content.Shared.Paper.PaperComponent;

namespace Content.Server.Paper;

/// <summary>
/// Handles selecting a template for blank papers that can turn into various forms.
/// </summary>
public sealed class PaperFormPickerSystem : EntitySystem
{
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;

    private static readonly ProtoId<TagPrototype> WriteTag = "Write";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PaperFormPickerComponent, InteractUsingEvent>(OnInteractUsing, before: new[] { typeof(PaperSystem) });
        SubscribeLocalEvent<PaperFormPickerComponent, PaperFormPickerSelectFormMessage>(OnSelectForm);
    }

    private void OnInteractUsing(Entity<PaperFormPickerComponent> entity, ref InteractUsingEvent args)
    {
        if (args.Handled || entity.Comp.Selected)
            return;

        if (!_tagSystem.HasTag(args.Used, WriteTag))
            return;

        var options = entity.Comp.Forms.Select(f => Loc.GetString(f.Name)).ToList();
        _uiSystem.SetUiState(entity.Owner, PaperFormPickerUiKey.Key, new PaperFormPickerBoundUserInterfaceState(options));
        _uiSystem.TryOpenUi(entity.Owner, PaperFormPickerUiKey.Key, args.User);
        args.Handled = true;
    }

    private void OnSelectForm(Entity<PaperFormPickerComponent> entity, ref PaperFormPickerSelectFormMessage args)
    {
        if (entity.Comp.Selected)
            return;

        var user = args.Actor;
        if (user == EntityUid.Invalid)
            return;

        if (args.Index < 0 || args.Index >= entity.Comp.Forms.Count)
            return;

        var choice = entity.Comp.Forms[args.Index];
        var template = EnsureComp<PaperTemplateComponent>(entity.Owner);
        template.Template = choice.Template;
        template.Loaded = false;
        Dirty(entity.Owner, template);

        entity.Comp.Selected = true;
        Dirty(entity.Owner, entity.Comp);

        var fillEv = new PaperFillRequestEvent(user);
        RaiseLocalEvent(entity.Owner, ref fillEv);

        if (TryComp(entity.Owner, out PaperComponent? paper))
        {
            paper.Mode = PaperComponent.PaperAction.Write;
            _paperSystem.SetContent((entity.Owner, paper), paper.Content);
            _uiSystem.OpenUi(entity.Owner, PaperUiKey.Key, user);
        }

        _uiSystem.CloseUi(entity.Owner, PaperFormPickerUiKey.Key, user);
    }
}
