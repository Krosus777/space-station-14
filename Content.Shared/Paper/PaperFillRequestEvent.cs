namespace Content.Shared.Paper;

/// <summary>
///     Raised when a user begins editing a paper. Other systems may
///     handle this to automatically populate template fields.
/// </summary>
[ByRefEvent]
public record struct PaperFillRequestEvent(EntityUid User);
