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

        var text = entity.Comp.Content;

        if (text.Contains("[JOB]"))
            entity.Comp.Dropdown.TryAdd("[JOB]", new List<string>());
        if (text.Contains("[FIO]"))
            entity.Comp.Dropdown.TryAdd("[FIO]", new List<string>());

        var name = MetaData(args.User).EntityName;
        text = text.Replace("[FIO]", name).Replace("[NAME]", name);
        entity.Comp.Selection["[FIO]"] = name;

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

        foreach (var (placeholder, chosen) in entity.Comp.Selection)
            text = text.Replace(placeholder, chosen);

        _formSystem.SetContent((entity.Owner, entity.Comp), text);
        entity.Comp.Filled = true;
        Dirty(entity.Owner, entity.Comp);
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

        _uiSystem.CloseUi(entity.Owner, FormFieldUiKey.Key, msg.Actor);
    }
}
