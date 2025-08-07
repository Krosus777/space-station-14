using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Paper;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.UserInterface;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.Paper;

/// <summary>
///     Handles automatic filling of templated paper forms.
/// </summary>
public sealed class PaperFormSystem : EntitySystem
{
    [Dependency] private readonly SharedStationSystem _stationSystem = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PaperFormComponent, PaperFillRequestEvent>(OnFillRequest, after: new[] { typeof(PaperTemplateSystem) });
        SubscribeLocalEvent<PaperComponent, PaperComponent.PaperFieldRequestMessage>(OnFieldRequest);
        SubscribeLocalEvent<PaperFormComponent, PaperFieldSelectedMessage>(OnFieldSelected);
    }

    private void OnFillRequest(Entity<PaperFormComponent> entity, ref PaperFillRequestEvent args)
    {
        if (!TryComp(entity.Owner, out PaperComponent? paper))
            return;

        var text = paper.Content;

        // ensure dropdown placeholders are tracked
        if (text.Contains("[JOB]"))
            entity.Comp.Dropdown.TryAdd("[JOB]", new List<string>());
        if (text.Contains("[FIO]"))
            entity.Comp.Dropdown.TryAdd("[FIO]", new List<string>());

        // Player name / FIO
        var name = MetaData(args.User).EntityName;
        text = text.Replace("[FIO]", name).Replace("[NAME]", name);
        entity.Comp.Selection["[FIO]"] = name;

        // Station number / name
        var stationName = string.Empty;
        var station = _stationSystem.GetCurrentStation(args.User);
        if (station != null)
            stationName = MetaData(station.Value).EntityName;
        text = text.Replace("[STATION]", stationName);
        entity.Comp.Selection["[STATION]"] = stationName;

        // Current date + 1000 years
        var future = DateTime.UtcNow.AddYears(1000);
        var date = future.ToString("yyyy-MM-dd");
        text = text.Replace("[DATE]", date);
        entity.Comp.Selection["[DATE]"] = date;

        // Replace any previously chosen dropdown selections
        foreach (var (placeholder, chosen) in entity.Comp.Selection)
        {
            text = text.Replace(placeholder, chosen);
        }

        _paperSystem.SetContent((entity.Owner, paper), text);
        entity.Comp.Filled = true;
        Dirty(entity.Owner, entity.Comp);
    }

    private void OnFieldRequest(Entity<PaperComponent> entity, ref PaperComponent.PaperFieldRequestMessage args)
    {
        if (!TryComp(entity.Owner, out PaperFormComponent? form))
            return;

        if (!form.Dropdown.ContainsKey(args.Placeholder))
            return;

        var options = form.Dropdown[args.Placeholder];

        if (options.Count == 0)
        {
            if (args.Placeholder == "[JOB]")
            {
                options = _prototypeManager.EnumeratePrototypes<JobPrototype>()
                    .Select(p => p.LocalizedName).ToList();
            }
            else if (args.Placeholder == "[FIO]")
            {
                options = new List<string>();
                foreach (var session in _playerManager.Sessions)
                {
                    if (session.AttachedEntity is { Valid: true } attached)
                        options.Add(MetaData(attached).EntityName);
                }
            }

            form.Dropdown[args.Placeholder] = options;
            Dirty(entity.Owner, form);
        }

        _uiSystem.OpenUi(entity.Owner, PaperFieldUiKey.Key, args.Actor);
        _uiSystem.SetUiState(entity.Owner, PaperFieldUiKey.Key, new PaperFieldUiState(args.Placeholder, options));
    }

    private void OnFieldSelected(Entity<PaperFormComponent> entity, ref PaperFieldSelectedMessage msg)
    {
        if (!TryComp(entity.Owner, out PaperComponent? paper))
            return;

        var text = paper.Content;

        if (text.Contains(msg.Placeholder))
            text = text.Replace(msg.Placeholder, msg.Selection);
        else if (entity.Comp.Selection.TryGetValue(msg.Placeholder, out var oldVal) && text.Contains(oldVal))
            text = text.Replace(oldVal, msg.Selection);

        entity.Comp.Selection[msg.Placeholder] = msg.Selection;

        _paperSystem.SetContent((entity.Owner, paper), text);
        Dirty(entity.Owner, entity.Comp);

        _uiSystem.CloseUi(entity.Owner, PaperFieldUiKey.Key, msg.Actor);
    }
}
