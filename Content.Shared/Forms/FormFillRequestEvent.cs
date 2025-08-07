namespace Content.Shared.Forms;

/// <summary>
/// Raised when a user begins editing a form document so that other systems can populate fields.
/// </summary>
[ByRefEvent]
public record struct FormFillRequestEvent(EntityUid User);
