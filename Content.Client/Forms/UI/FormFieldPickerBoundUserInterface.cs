using System;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Content.Shared.Forms;

namespace Content.Client.Forms.UI;

[UsedImplicitly]
public sealed class FormFieldPickerBoundUserInterface : BoundUserInterface
{
    private FormFieldPickerWindow? _window;
    private string _placeholder = string.Empty;

    public FormFieldPickerBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<FormFieldPickerWindow>();
        _window.OnConfirmed += text => SendMessage(new FormFieldSelectedMessage(_placeholder, text));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not FormFieldUiState cast)
            return;
        _placeholder = cast.Placeholder;
        _window?.Populate(cast.Options);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        _window?.Dispose();
    }
}
