using System;
using System.Collections.Generic;
using Robust.Shared.Serialization;

namespace Content.Shared.Forms;

[Serializable, NetSerializable]
public enum FormFieldUiKey
{
    Key
}

[Serializable, NetSerializable]
public sealed class FormFieldUiState : BoundUserInterfaceState
{
    public readonly string Placeholder;
    public readonly List<string> Options;

    public FormFieldUiState(string placeholder, List<string> options)
    {
        Placeholder = placeholder;
        Options = options;
    }
}

[Serializable, NetSerializable]
public sealed class FormFieldSelectedMessage : BoundUserInterfaceMessage
{
    public readonly string Placeholder;
    public readonly string Selection;

    public FormFieldSelectedMessage(string placeholder, string selection)
    {
        Placeholder = placeholder;
        Selection = selection;
    }
}
