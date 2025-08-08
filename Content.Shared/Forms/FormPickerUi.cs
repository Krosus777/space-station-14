using System;
using System.Collections.Generic;
using Robust.Shared.Serialization;

namespace Content.Shared.Forms;

/// <summary>
/// Network messages and UI keys for the form template picker.
/// </summary>
[Serializable, NetSerializable]
public sealed class FormPickerBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly List<string> Options;

    public FormPickerBoundUserInterfaceState(List<string> options)
    {
        Options = options;
    }
}

[Serializable, NetSerializable]
public sealed class FormPickerSelectFormMessage : BoundUserInterfaceMessage
{
    public readonly int Index;

    public FormPickerSelectFormMessage(int index)
    {
        Index = index;
    }
}

[Serializable, NetSerializable]
public enum FormPickerUiKey : byte
{
    Key
}
