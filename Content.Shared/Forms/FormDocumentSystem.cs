using Content.Shared.Interaction;
using Content.Shared.Tag;
using Content.Shared.UserInterface;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Shared.Forms;

public sealed class FormDocumentSystem : EntitySystem
{
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly TagSystem _tags = default!;

    private static readonly ProtoId<TagPrototype> WriteTag = "Write";

    public override void Initialize()
    {
        SubscribeLocalEvent<FormDocumentComponent, BeforeActivatableUIOpenEvent>(OnBeforeOpen);
        SubscribeLocalEvent<FormDocumentComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FormDocumentComponent, FormDocumentComponent.FormInputTextMessage>(OnInput);
    }

    private void OnBeforeOpen(Entity<FormDocumentComponent> entity, ref BeforeActivatableUIOpenEvent args)
    {
        entity.Comp.Mode = FormDocumentComponent.FormAction.Read;
        UpdateUi(entity);
    }

    private void OnInteractUsing(Entity<FormDocumentComponent> entity, ref InteractUsingEvent args)
    {
        if (!_tags.HasTag(args.Used, WriteTag))
            return;
        if (entity.Comp.EditingDisabled)
            return;

        entity.Comp.Mode = FormDocumentComponent.FormAction.Write;
        _ui.OpenUi(entity.Owner, FormDocumentComponent.FormUiKey.Key, args.User);
        UpdateUi(entity);
        args.Handled = true;
    }

    private void OnInput(Entity<FormDocumentComponent> entity, ref FormDocumentComponent.FormInputTextMessage msg)
    {
        if (msg.Text.Length > entity.Comp.ContentSize)
            return;
        SetContent(entity, msg.Text);
    }

    public void SetContent(Entity<FormDocumentComponent> entity, string text)
    {
        entity.Comp.Content = text;
        Dirty(entity);
        UpdateUi(entity);
        _audio.PlayPredicted(entity.Comp.Sound, entity.Owner);
    }

    private void UpdateUi(Entity<FormDocumentComponent> entity)
    {
        _ui.SetUiState(entity.Owner, FormDocumentComponent.FormUiKey.Key,
            new FormDocumentComponent.FormBoundUserInterfaceState(entity.Comp.Content, entity.Comp.Mode,
                entity.Comp.Dropdown, entity.Comp.Selection));
    }
}
