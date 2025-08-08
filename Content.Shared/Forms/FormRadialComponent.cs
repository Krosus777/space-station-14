using System.Collections.Generic;
using Robust.Shared.GameStates;

namespace Content.Shared.Forms;

/// <summary>
/// Provides dropdown options for the form radial menu.
/// Stored on humanoid players so the menu can be opened anywhere.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class FormRadialComponent : Component
{
    /// <summary>
    /// Dropdown options keyed by placeholder tags (e.g. "[FIO]", "[JOB]").
    /// </summary>
    [DataField, AutoNetworkedField]
    public Dictionary<string, List<string>> Dropdown = new();
}
