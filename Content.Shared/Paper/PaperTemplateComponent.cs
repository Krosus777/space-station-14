using Robust.Shared.GameStates;

namespace Content.Shared.Paper;

/// <summary>
///     Stores an FTL key for loading a paper's template text.
///     The template will be applied the first time the paper is edited.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class PaperTemplateComponent : Component
{
    /// <summary>
    ///     FTL key of the template to load.
    /// </summary>
    [DataField("template", required: true), AutoNetworkedField]
    public string Template = string.Empty;

    /// <summary>
    ///     Whether the template has already been loaded onto the paper.
    /// </summary>
    [DataField("loaded"), AutoNetworkedField]
    public bool Loaded;
}
