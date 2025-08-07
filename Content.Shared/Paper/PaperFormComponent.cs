using System.Collections.Generic;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Paper;

/// <summary>
///     Marks a piece of paper as a template form. The form contains
///     placeholders that can be automatically filled with information
///     such as the player's name or the current station.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PaperFormComponent : Component
{
    /// <summary>
    ///     Whether this form has already been processed and all automatic
    ///     fields have been filled. Prevents repeatedly overwriting user
    ///     edits when the UI is reopened.
    /// </summary>
    [DataField("filled"), AutoNetworkedField]
    public bool Filled = false;

    /// <summary>
    ///     Optional mapping of placeholder strings to a set of options.
    ///     When present the first option will be used as a default value.
    ///     If no options are supplied the placeholder will be left for
    ///     freeform editing by the player.
    /// </summary>
    [DataField("dropdown"), AutoNetworkedField]
    public Dictionary<string, List<string>> Dropdown = new();

    /// <summary>
    ///     Tracks the currently selected value for each placeholder so that
    ///     players can reopen and change their choice later by clicking the
    ///     filled text.
    /// </summary>
    [DataField("selection"), AutoNetworkedField]
    public Dictionary<string, string> Selection = new();
}
