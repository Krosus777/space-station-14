using System.Collections.Generic;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Paper;

/// <summary>
/// Allows selecting a document template from a list when the paper is first used with a pen.
/// After selection the paper is converted into the chosen form.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class PaperFormPickerComponent : Component
{
    /// <summary>
    /// Available form options.
    /// </summary>
    [DataField("forms")]
    public List<FormOption> Forms = new();

    /// <summary>
    /// Whether a form has already been chosen for this paper.
    /// </summary>
    [DataField("selected")]
    public bool Selected;

    [Serializable, NetSerializable, DataRecord]
    public sealed partial class FormOption
    {
        [DataField("name", required: true)]
        public string Name = string.Empty;

        [DataField("template", required: true)]
        public string Template = string.Empty;
    }
}
