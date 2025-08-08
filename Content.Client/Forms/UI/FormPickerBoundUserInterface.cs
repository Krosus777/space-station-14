using System;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Content.Shared.Forms;

namespace Content.Client.Forms.UI;

[UsedImplicitly]
public sealed class FormPickerBoundUserInterface : BoundUserInterface
{
    private FormPickerWindow? _window;

    public FormPickerBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<FormPickerWindow>();
        _window.OnFormSelected += i => SendMessage(new FormPickerSelectFormMessage(i));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not FormPickerBoundUserInterfaceState cast)
            return;
        _window?.Update(cast.Options);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        _window?.Dispose();
    }
}
