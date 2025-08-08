using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Forms;
using Content.Shared.Paper;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Station;
using Content.Shared.UserInterface;
using Content.Shared.Mind.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Localization;

namespace Content.Server.Forms;

/// <summary>
/// Handles placeholder substitution for form templates and copies the result onto a paper.
/// </summary>
public sealed class PaperFormSystem : EntitySystem
{
    [Dependency] private readonly SharedStationSystem _stationSystem = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;
    [Dependency] private readonly SharedJobSystem _jobSystem = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PaperFormComponent, FormFillRequestEvent>(OnFillRequest);
        SubscribeLocalEvent<PaperFormComponent, FormFieldSelectedMessage>(OnFieldSelected);
    }

    private void OnFillRequest(Entity<PaperFormComponent> entity, ref FormFillRequestEvent args)
    {
        if (entity.Comp.Filled)
            return;
        if (!TryComp(entity.Owner, out PaperComponent? paper))
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

        // future date
        var future = DateTime.UtcNow.AddYears(1000);
        var date = future.ToString("yyyy-MM-dd");
        text = text.Replace("[DATE]", date);

        entity.Comp.CurrentText = text;
        entity.Comp.Pending.Clear();
        entity.Comp.Dropdown.Clear();
        entity.Comp.Selection.Clear();

        if (text.Contains("[FIO]"))
        {
            var selfName = MetaData(args.User).EntityName;
            var names = new List<string> { selfName };
            foreach (var session in _playerManager.Sessions)
            {
                if (session.AttachedEntity is { Valid: true } attached)
                {
                    var name = MetaData(attached).EntityName;
                    if (name == selfName)
                        continue;
                    names.Add(name);
                }
            }
            entity.Comp.Dropdown["[FIO]"] = names;
            entity.Comp.Pending.Add("[FIO]");
        }

        if (text.Contains("[JOB]"))
        {
            TryComp(args.User, out MindContainerComponent? mind);
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
            entity.Comp.Dropdown["[JOB]"] = jobs;
            entity.Comp.Pending.Add("[JOB]");
        }

        if (entity.Comp.Pending.Count == 0)
        {
            entity.Comp.Filled = true;
            _paperSystem.SetContent(entity.Owner, entity.Comp.CurrentText);
            _ui.OpenUi(entity.Owner, PaperComponent.PaperUiKey.Key, args.User);
            return;
        }

        var placeholder = entity.Comp.Pending[0];
        var options = entity.Comp.Dropdown[placeholder];
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
            var options = entity.Comp.Dropdown[next];
            _ui.SetUiState(entity.Owner, FormFieldUiKey.Key, new FormFieldUiState(next, options));
            return;
        }

        entity.Comp.Filled = true;
        _ui.CloseUi(entity.Owner, FormFieldUiKey.Key, msg.Actor);
        _paperSystem.SetContent(entity.Owner, entity.Comp.CurrentText);
        _ui.OpenUi(entity.Owner, PaperComponent.PaperUiKey.Key, msg.Actor);
    }
}
