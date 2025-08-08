using System;
using System.Collections.Generic;
using Robust.Shared.GameStates;

namespace Content.Shared.Forms;

/// <summary>
/// Stores temporary form data before it is committed to a paper.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PaperFormComponent : Component
{
    /// <summary>
    /// Raw template text awaiting placeholder substitution.
    /// </summary>
    [DataField]
    public string? Template;

    /// <summary>
    /// Working text as placeholders are replaced.
    /// </summary>
    [DataField]
    public string CurrentText = string.Empty;

    /// <summary>
    /// Dropdown options for each placeholder.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Dictionary<string, List<string>> Dropdown = new();

    /// <summary>
    /// Selections made for each placeholder.
    /// </summary>
    [DataField]
    public Dictionary<string, string> Selection = new();

    /// <summary>
    /// Placeholders that still need values.
    /// </summary>
    [DataField]
    public List<string> Pending = new();

    /// <summary>
    /// Whether all placeholders have been filled and content copied to the paper.
    /// </summary>
    [DataField]
    public bool Filled;
}
