using System.Threading;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.Server.Corvax.VentCrawl;

[RegisterComponent]
public sealed partial class CorvaxVentCrawlingComponent : Component
{
    [ViewVariables]
    public EntityUid? CurrentSegment;

    [ViewVariables]
    public EntityUid? PreviousSegment;

    [ViewVariables]
    public MapCoordinates LastKnownCoordinates = MapCoordinates.Nullspace;

    [ViewVariables]
    public bool Transitioning;

    [ViewVariables]
    public CancellationTokenSource? CrawlTimerCancel;

    [ViewVariables]
    public CancellationTokenSource? CrawlSafetyTimerCancel;

    [ViewVariables]
    public string? CurrentPipeNodeName;

    [ViewVariables]
    public EntityUid? TransitionFromSegment;

    [ViewVariables]
    public EntityUid? TransitionToSegment;
}
