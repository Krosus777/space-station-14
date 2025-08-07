using System;
using System.Collections.Generic;
using Robust.Shared.Serialization;

namespace Content.Shared.Paper;

/// <summary>
/// Network messages and UI keys for the paper form picker.
/// </summary>
[Serializable, NetSerializable]
public sealed class PaperFormPickerBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly List<string> Options;

    public PaperFormPickerBoundUserInterfaceState(List<string> options)
    {
        Options = options;
    }
}

[Serializable, NetSerializable]
public sealed class PaperFormPickerSelectFormMessage : BoundUserInterfaceMessage
{
    public readonly int Index;

    public PaperFormPickerSelectFormMessage(int index)
    {
        Index = index;
    }
}

[Serializable, NetSerializable]
public enum PaperFormPickerUiKey : byte
{
    Key
}
