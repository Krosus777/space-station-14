using System.Collections.Generic;
using Content.Client.Gameplay;
using Content.Client.Paper.UI;
using Content.Client.UserInterface.Controls;
using Content.Shared.Forms;
using Content.Shared.Input;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Client.Player;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input.Binding;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.GameObjects;

namespace Content.Client.Forms.UI;

/// <summary>
/// Opens a small radial menu for inserting form fields into the paper editor.
/// </summary>
[UsedImplicitly]
public sealed class FormRadialMenuUIController : UIController, IOnStateChanged<GameplayState>
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    private SimpleRadialMenu? _menu;
    private PaperWindow? _paperWindow;

    public void OnStateEntered(GameplayState state)
    {
        CommandBinds.Builder
            .Bind(ContentKeyFunctions.OpenPaperRadialMenu,
                InputCmdHandler.FromDelegate(_ => ToggleMenu()))
            .Register<FormRadialMenuUIController>();

        PaperBoundUserInterface.WindowOpened += OnPaperOpened;
        PaperBoundUserInterface.WindowClosed += OnPaperClosed;
    }

    public void OnStateExited(GameplayState state)
    {
        CommandBinds.Unregister<FormRadialMenuUIController>();
        PaperBoundUserInterface.WindowOpened -= OnPaperOpened;
        PaperBoundUserInterface.WindowClosed -= OnPaperClosed;
        CloseMenu();
    }

    private void OnPaperOpened(EntityUid uid, PaperWindow window)
    {
        _paperWindow = window;
    }

    private void OnPaperClosed(PaperWindow window)
    {
        if (_paperWindow == window)
        {
            _paperWindow = null;
        }
    }

    private void ToggleMenu()
    {
        if (_paperWindow == null)
            return;

        if (_menu != null)
        {
            CloseMenu();
            return;
        }

        var options = new RadialMenuOption[]
        {
            new RadialMenuActionOption<string>(OpenPicker, "[FIO]")
            {
                ToolTip = Loc.GetString("paper-radial-name")
            },
            new RadialMenuActionOption<string>(OpenPicker, "[JOB]")
            {
                ToolTip = Loc.GetString("paper-radial-job")
            }
        };

        _menu = new SimpleRadialMenu();
        _menu.SetButtons(options);
        _menu.OnClose += CloseMenu;
        _menu.OpenOverMouseScreenPosition();
    }

    private void CloseMenu()
    {
        if (_menu == null)
            return;
        _menu.Dispose();
        _menu = null;
    }

    private void OpenPicker(string placeholder)
    {
        CloseMenu();
        if (_paperWindow == null)
            return;

        var options = GetOptions(placeholder);
        var picker = new FormFieldPickerWindow();
        picker.Populate(options);
        picker.OnConfirmed += text =>
        {
            _paperWindow.Input.InsertAtCursor(text);
            picker.Close();
        };
        picker.OpenCentered();
    }

    private List<string> GetOptions(string placeholder)
    {
        var list = new List<string>();
        var player = _players.LocalEntity;
        if (player != null &&
            _entMan.TryGetComponent<FormRadialComponent>(player.Value, out var radial) &&
            radial.Dropdown.TryGetValue(placeholder, out var opts))
        {
            list.AddRange(opts);
        }

        if (list.Count > 0)
            return list;

        if (placeholder == "[JOB]")
        {
            foreach (var proto in _prototypeManager.EnumeratePrototypes<JobPrototype>())
                list.Add(proto.LocalizedName);
        }
        else if (placeholder == "[FIO]" && player != null)
        {
            list.Add(_entMan.GetComponent<MetaDataComponent>(player.Value).EntityName);
        }

        return list;
    }
}
