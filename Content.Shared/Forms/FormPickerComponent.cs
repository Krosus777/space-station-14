using System;
using System.Collections.Generic;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared.Forms;

/// <summary>
/// Allows selecting a document template from a list when the form is first used with a pen.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class FormPickerComponent : Component
{
    [DataField("forms")]
    public List<FormOption> Forms = new();

    [DataField("basePrototype")]
    public ProtoId<EntityPrototype>? BasePrototype;

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
