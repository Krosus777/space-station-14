using System;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Content.Shared.Paper;

namespace Content.Client.Paper.UI;

[UsedImplicitly]
public sealed class PaperFieldPickerBoundUserInterface : BoundUserInterface
{
    private PaperFieldPickerWindow? _window;
    private string _placeholder = string.Empty;

    public PaperFieldPickerBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<PaperFieldPickerWindow>();
        _window.OnConfirmed += text => SendMessage(new PaperFieldSelectedMessage(_placeholder, text));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not PaperFieldUiState cast)
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
