using System;
using System.Collections.Generic;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Forms;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class FormDocumentComponent : Component
{
    public FormAction Mode;

    [DataField("content"), AutoNetworkedField]
    public string Content { get; set; } = "";

    [DataField("contentSize")]
    public int ContentSize { get; set; } = 10000;

    [DataField, AutoNetworkedField]
    public bool EditingDisabled;

    /// <summary>
    ///     Tracks available dropdown options for placeholders.
    /// </summary>
    [DataField("dropdown"), AutoNetworkedField]
    public Dictionary<string, List<string>> Dropdown = new();

    /// <summary>
    ///     Current selections for any placeholders that have been filled.
    /// </summary>
    [DataField("selection"), AutoNetworkedField]
    public Dictionary<string, string> Selection = new();

    /// <summary>
    ///     Placeholders that still require user selection before the form can open.
    /// </summary>
    [DataField]
    public List<string> Pending = new();

    /// <summary>
    ///     Whether automatic fields have already been filled.
    /// </summary>
    [DataField("filled"), AutoNetworkedField]
    public bool Filled = false;

    [DataField("sound")]
    public SoundSpecifier? Sound { get; private set; } = new SoundCollectionSpecifier("PaperScribbles", AudioParams.Default.WithVariation(0.1f));

    [Serializable, NetSerializable]
    public sealed class FormBoundUserInterfaceState : BoundUserInterfaceState
    {
        public readonly string Text;
        public readonly FormAction Mode;
        public readonly Dictionary<string, List<string>> Dropdown;
        public readonly Dictionary<string, string> Selection;

        public FormBoundUserInterfaceState(string text, FormAction mode,
            Dictionary<string, List<string>>? dropdown = null,
            Dictionary<string, string>? selection = null)
        {
            Text = text;
            Mode = mode;
            Dropdown = dropdown ?? new();
            Selection = selection ?? new();
        }
    }

    [Serializable, NetSerializable]
    public sealed class FormInputTextMessage : BoundUserInterfaceMessage
    {
        public readonly string Text;
        public FormInputTextMessage(string text)
        {
            Text = text;
        }
    }

    [Serializable, NetSerializable]
    public sealed class FormFieldRequestMessage : BoundUserInterfaceMessage
    {
        public readonly string Placeholder;
        public FormFieldRequestMessage(string placeholder)
        {
            Placeholder = placeholder;
        }
    }

    [Serializable, NetSerializable]
    public enum FormUiKey
    {
        Key
    }

    [Serializable, NetSerializable]
    public enum FormAction
    {
        Read,
        Write,
    }
}
