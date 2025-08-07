using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;
using Content.Shared.Forms;
using static Content.Shared.Forms.FormDocumentComponent;

namespace Content.Client.Forms.UI;

[UsedImplicitly]
public sealed class FormBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private FormWindow? _window;

    public FormBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<FormWindow>();
        _window.OnSaved += InputOnTextEntered;
        _window.OnPlaceholderClicked += placeholder => SendMessage(new FormFieldRequestMessage(placeholder));

        if (EntMan.TryGetComponent<FormDocumentComponent>(Owner, out var doc))
        {
            _window.MaxInputLength = doc.ContentSize;
        }
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        _window?.Populate((FormBoundUserInterfaceState)state);
    }

    private void InputOnTextEntered(string text)
    {
        SendMessage(new FormInputTextMessage(text));

        if (_window != null)
        {
            _window.Input.TextRope = Rope.Leaf.Empty;
            _window.Input.CursorPosition = new TextEdit.CursorPos(0, TextEdit.LineBreakBias.Top);
        }
    }
}
