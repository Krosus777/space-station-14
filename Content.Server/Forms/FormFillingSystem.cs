using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.Forms;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.UserInterface;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Localization;

namespace Content.Server.Forms;

/// <summary>
/// Handles automatic filling of templated forms.
/// </summary>
public sealed class FormFillingSystem : EntitySystem
{
    [Dependency] private readonly SharedStationSystem _stationSystem = default!;
    [Dependency] private readonly FormDocumentSystem _formSystem = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FormDocumentComponent, FormDocumentComponent.FormFieldRequestMessage>(OnFieldRequest);
        SubscribeLocalEvent<FormDocumentComponent, FormFieldSelectedMessage>(OnFieldSelected);
        SubscribeLocalEvent<FormDocumentComponent, FormFillRequestEvent>(OnFillRequest, after: new[] { typeof(FormTemplateSystem) });
    }

    private void OnFillRequest(Entity<FormDocumentComponent> entity, ref FormFillRequestEvent args)
    {
        if (entity.Comp.Filled)
            return;

        entity.Comp.Pending.Clear();

        var text = entity.Comp.Content;

        if (text.Contains("[FIO]"))
        {
            var options = new List<string>();
            foreach (var session in _playerManager.Sessions)
            {
                if (session.AttachedEntity is { Valid: true } attached)
                    options.Add(MetaData(attached).EntityName);
            }
            entity.Comp.Dropdown["[FIO]"] = options;
            entity.Comp.Pending.Add("[FIO]");
        }

        if (text.Contains("[JOB]"))
        {
            var options = _prototypeManager.EnumeratePrototypes<JobPrototype>()
                .Select(p => p.LocalizedName).ToList();
            entity.Comp.Dropdown["[JOB]"] = options;
            entity.Comp.Pending.Add("[JOB]");
        }

        var stationName = string.Empty;
        var station = _stationSystem.GetCurrentStation(args.User);
        if (station != null)
            stationName = MetaData(station.Value).EntityName;
        text = text.Replace("[STATION]", stationName);
        entity.Comp.Selection["[STATION]"] = stationName;

        var future = DateTime.UtcNow.AddYears(1000);
        var date = future.ToString("yyyy-MM-dd");
        text = text.Replace("[DATE]", date);
        entity.Comp.Selection["[DATE]"] = date;

        _formSystem.SetContent((entity.Owner, entity.Comp), text);
        Dirty(entity.Owner, entity.Comp);

        entity.Comp.Mode = FormDocumentComponent.FormAction.Write;
        _uiSystem.OpenUi(entity.Owner, FormDocumentComponent.FormUiKey.Key, args.User);
    }

    private void OnFieldRequest(Entity<FormDocumentComponent> entity, ref FormDocumentComponent.FormFieldRequestMessage args)
    {
        if (!entity.Comp.Dropdown.ContainsKey(args.Placeholder))
            return;

        var options = entity.Comp.Dropdown[args.Placeholder];

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

            entity.Comp.Dropdown[args.Placeholder] = options;
            Dirty(entity.Owner, entity.Comp);
        }

        _uiSystem.OpenUi(entity.Owner, FormFieldUiKey.Key, args.Actor);
        _uiSystem.SetUiState(entity.Owner, FormFieldUiKey.Key, new FormFieldUiState(args.Placeholder, options));
    }

    private void OnFieldSelected(Entity<FormDocumentComponent> entity, ref FormFieldSelectedMessage msg)
    {
        var text = entity.Comp.Content;

        if (text.Contains(msg.Placeholder))
            text = text.Replace(msg.Placeholder, msg.Selection);
        else if (entity.Comp.Selection.TryGetValue(msg.Placeholder, out var oldVal) && text.Contains(oldVal))
            text = text.Replace(oldVal, msg.Selection);

        entity.Comp.Selection[msg.Placeholder] = msg.Selection;

        _formSystem.SetContent((entity.Owner, entity.Comp), text);
        Dirty(entity.Owner, entity.Comp);

        if (entity.Comp.Pending.Count > 0 && entity.Comp.Pending[0] == msg.Placeholder)
            entity.Comp.Pending.RemoveAt(0);

        if (entity.Comp.Pending.Count > 0)
        {
            var next = entity.Comp.Pending[0];
            var options = entity.Comp.Dropdown[next];
            _uiSystem.SetUiState(entity.Owner, FormFieldUiKey.Key, new FormFieldUiState(next, options));
            return;
        }

        entity.Comp.Filled = true;
        entity.Comp.Mode = FormDocumentComponent.FormAction.Write;
        _uiSystem.CloseUi(entity.Owner, FormFieldUiKey.Key, msg.Actor);
        _uiSystem.OpenUi(entity.Owner, FormDocumentComponent.FormUiKey.Key, msg.Actor);
    }
}
