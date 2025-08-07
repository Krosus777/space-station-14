using Robust.Shared.GameStates;

namespace Content.Shared.Forms;

/// <summary>
/// Stores an FTL key for loading a form's template text.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class FormTemplateComponent : Component
{
    [DataField("template", required: true), AutoNetworkedField]
    public string Template = string.Empty;

    [DataField("loaded"), AutoNetworkedField]
    public bool Loaded;
}
