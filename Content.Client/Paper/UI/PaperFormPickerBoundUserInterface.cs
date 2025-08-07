using System;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Content.Shared.Paper;

namespace Content.Client.Paper.UI;

[UsedImplicitly]
public sealed class PaperFormPickerBoundUserInterface : BoundUserInterface
{
    private PaperFormPickerWindow? _window;

    public PaperFormPickerBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<PaperFormPickerWindow>();
        _window.OnFormSelected += i => SendMessage(new PaperFormPickerSelectFormMessage(i));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not PaperFormPickerBoundUserInterfaceState cast)
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
