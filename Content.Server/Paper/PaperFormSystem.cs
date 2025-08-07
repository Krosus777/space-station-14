using System;
using Content.Shared.Paper;
using Content.Shared.Station;

namespace Content.Server.Paper;

/// <summary>
///     Handles automatic filling of templated paper forms.
/// </summary>
public sealed class PaperFormSystem : EntitySystem
{
    [Dependency] private readonly SharedStationSystem _stationSystem = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PaperFormComponent, PaperFillRequestEvent>(OnFillRequest, after: new[] { typeof(PaperTemplateSystem) });
    }

    private void OnFillRequest(Entity<PaperFormComponent> entity, ref PaperFillRequestEvent args)
    {
        if (entity.Comp.Filled || !TryComp(entity.Owner, out PaperComponent? paper))
            return;

        var text = paper.Content;

        // Player name / FIO
        var name = MetaData(args.User).EntityName;
        text = text.Replace("[FIO]", name).Replace("[NAME]", name);

        // Station number / name
        var stationName = string.Empty;
        var station = _stationSystem.GetCurrentStation(args.User);
        if (station != null)
            stationName = MetaData(station.Value).EntityName;
        text = text.Replace("[STATION]", stationName);

        // Current date + 1000 years
        var future = DateTime.UtcNow.AddYears(1000);
        text = text.Replace("[DATE]", future.ToString("yyyy-MM-dd"));

        // Handle dropdown fields
        foreach (var (placeholder, options) in entity.Comp.Dropdown)
        {
            if (!text.Contains(placeholder))
                continue;

            var replacement = options.Count > 0 ? options[0] : string.Empty;
            text = text.Replace(placeholder, replacement);
        }

        _paperSystem.SetContent((entity.Owner, paper), text);
        entity.Comp.Filled = true;
        Dirty(entity.Owner, entity.Comp);
    }
}
