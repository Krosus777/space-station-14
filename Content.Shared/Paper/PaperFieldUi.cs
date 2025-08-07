using System.Collections.Generic;
using Robust.Shared.Serialization;

namespace Content.Shared.Paper;

[Serializable, NetSerializable]
public enum PaperFieldUiKey
{
    Key
}

[Serializable, NetSerializable]
public sealed class PaperFieldUiState : BoundUserInterfaceState
{
    public readonly string Placeholder;
    public readonly List<string> Options;

    public PaperFieldUiState(string placeholder, List<string> options)
    {
        Placeholder = placeholder;
        Options = options;
    }
}

[Serializable, NetSerializable]
public sealed class PaperFieldSelectedMessage : BoundUserInterfaceMessage
{
    public readonly string Placeholder;
    public readonly string Selection;

    public PaperFieldSelectedMessage(string placeholder, string selection)
    {
        Placeholder = placeholder;
        Selection = selection;
    }
}
