using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.CrewManifest;
using Content.Server.GameTicking;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Systems;
using Content.Shared.CrewManifest;
using Content.Shared.Forms;
using Content.Shared.Mind.Components;
using Content.Shared.Paper;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Station;
using Content.Shared.StationRecords;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.Forms;

/// <summary>
/// Handles placeholder substitution for form templates and copies the result onto a paper.
/// </summary>
public sealed class PaperFormSystem : EntitySystem
{
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;
    [Dependency] private readonly SharedJobSystem _jobSystem = default!;
    [Dependency] private readonly CrewManifestSystem _crewManifest = default!;
    [Dependency] private readonly StationRecordsSystem _recordsSystem = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PaperFormComponent, FormFillRequestEvent>(OnFillRequest);
        SubscribeLocalEvent<PaperFormComponent, FormFieldSelectedMessage>(OnFieldSelected);
    }

    private void OnFillRequest(Entity<PaperFormComponent> entity, ref FormFillRequestEvent args)
    {
        if (entity.Comp.Filled)
            return;
        if (!HasComp<PaperComponent>(entity.Owner))
            return;
        if (string.IsNullOrEmpty(entity.Comp.Template))
            return;

        var text = entity.Comp.Template!;

        // station identifier (e.g. EV-123)
        var stationId = string.Empty;
        var station = _stationSystem.GetCurrentStation(args.User);
        if (station != null)
        {
            var full = MetaData(station.Value).EntityName;
            var lastSpace = full.LastIndexOf(' ');
            stationId = lastSpace >= 0 ? full[(lastSpace + 1)..] : full;
        }
        text = text.Replace("[STATION]", stationId);

        // future date with round time
        var future = DateTime.UtcNow.AddYears(1000);
        var round = _gameTicker.RoundDuration();
        var date = future.ToString("yyyy-MM-dd") + " " + round.ToString("hh\\:mm");
        text = text.Replace("[DATE]", date);

        entity.Comp.CurrentText = text;
        entity.Comp.Pending.Clear();
        entity.Comp.Dropdown.Clear();
        entity.Comp.Selection.Clear();

        if (text.Contains("[FIO]"))
            entity.Comp.Pending.Add("[FIO]");

        if (text.Contains("[JOB]"))
            entity.Comp.Pending.Add("[JOB]");

        if (entity.Comp.Pending.Count == 0)
        {
            entity.Comp.Filled = true;
            _paperSystem.SetContent(entity.Owner, entity.Comp.CurrentText);
            _ui.OpenUi(entity.Owner, PaperComponent.PaperUiKey.Key, args.User);
            return;
        }

        var placeholder = entity.Comp.Pending[0];
        var options = GetOptions(entity, placeholder, args.User);
        _ui.SetUiState(entity.Owner, FormFieldUiKey.Key, new FormFieldUiState(placeholder, options));
        _ui.OpenUi(entity.Owner, FormFieldUiKey.Key, args.User);
    }

    private void OnFieldSelected(Entity<PaperFormComponent> entity, ref FormFieldSelectedMessage msg)
    {
        if (!entity.Comp.Pending.Contains(msg.Placeholder))
            return;

        entity.Comp.Selection[msg.Placeholder] = msg.Selection;
        entity.Comp.CurrentText = entity.Comp.CurrentText.Replace(msg.Placeholder, msg.Selection);
        entity.Comp.Pending.Remove(msg.Placeholder);

        if (entity.Comp.Pending.Count > 0)
        {
            var next = entity.Comp.Pending[0];
            var options = GetOptions(entity, next, msg.Actor);
            _ui.SetUiState(entity.Owner, FormFieldUiKey.Key, new FormFieldUiState(next, options));
            return;
        }

        entity.Comp.Filled = true;
        _ui.CloseUi(entity.Owner, FormFieldUiKey.Key, msg.Actor);
        _paperSystem.SetContent(entity.Owner, entity.Comp.CurrentText);
        _ui.OpenUi(entity.Owner, PaperComponent.PaperUiKey.Key, msg.Actor);
    }

    private List<string> GetOptions(Entity<PaperFormComponent> entity, string placeholder, EntityUid actor)
    {
        if (entity.Comp.Dropdown.TryGetValue(placeholder, out var options))
            return options;

        return placeholder switch
        {
            "[FIO]" => BuildCrewList(actor),
            "[JOB]" => BuildJobList(actor),
            _ => new List<string>()
        };
    }

    private List<string> BuildCrewList(EntityUid actor)
    {
        var names = new List<string>();
        var selfName = MetaData(actor).EntityName;
        var station = _stationSystem.GetCurrentStation(actor);

        if (station != null)
        {
            var (_, manifest) = _crewManifest.GetCrewManifest(station.Value);
            if (manifest != null)
            {
                foreach (var entry in manifest.Entries)
                    names.Add(entry.Name);
            }

            if (names.Count == 0)
            {
                foreach (var (_, record) in _recordsSystem.GetRecordsOfType<GeneralStationRecord>(station.Value))
                    names.Add(record.Name);
            }

            if (names.Count == 0)
            {
                var minds = EntityQueryEnumerator<MindContainerComponent>();
                while (minds.MoveNext(out var uid, out _))
                {
                    if (_stationSystem.GetOwningStation(uid) != station)
                        continue;

                    names.Add(MetaData(uid).EntityName);
                }
            }
        }

        names = names.Distinct().ToList();
        names.Remove(selfName);
        names.Insert(0, selfName);
        return names;
    }

    private List<string> BuildJobList(EntityUid actor)
    {
        TryComp(actor, out MindContainerComponent? mind);
        _jobSystem.MindTryGetJobName(mind?.Mind, out var jobName);
        var jobs = new List<string>();
        if (!string.IsNullOrEmpty(jobName))
            jobs.Add(jobName);
        foreach (var proto in _prototypeManager.EnumeratePrototypes<JobPrototype>())
        {
            var name = proto.LocalizedName;
            if (name == jobName)
                continue;
            jobs.Add(name);
        }
        return jobs;
    }
}
