using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.CrewManifest;
using Content.Server.Station.Systems;
using Content.Server.StationRecords.Systems;
using Content.Shared.Forms;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server.Forms;

/// <summary>
/// Populates <see cref="FormRadialComponent"/> on humanoids with crew and job lists
/// so the radial menu can display choices client-side.
/// </summary>
public sealed class FormRadialSystem : EntitySystem
{
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly CrewManifestSystem _crewManifest = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedJobSystem _jobSystem = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<FormRadialComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<AfterGeneralRecordCreatedEvent>(OnRecordChanged);
        SubscribeLocalEvent<RecordModifiedEvent>(OnRecordChanged);
        SubscribeLocalEvent<RecordRemovedEvent>(OnRecordChanged);
    }

    private void OnStartup(EntityUid uid, FormRadialComponent component, ComponentStartup args)
    {
        Refresh(uid, component);
    }

    private void OnRecordChanged<T>(T ev) where T : EntityEventArgs
    {
        RefreshAll();
    }

    private void RefreshAll()
    {
        var query = EntityQueryEnumerator<FormRadialComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            Refresh(uid, comp);
        }
    }

    private void Refresh(EntityUid uid, FormRadialComponent component)
    {
        component.Dropdown["[FIO]"] = BuildCrewList(uid);
        component.Dropdown["[JOB]"] = BuildJobList(uid);
        Dirty(uid, component);
    }

    private List<string> BuildCrewList(EntityUid actor)
    {
        var names = new List<string>();
        var selfName = MetaData(actor).EntityName;
        var station = _stationSystem.GetOwningStation(actor);

        if (station != null)
        {
            var (_, manifest) = _crewManifest.GetCrewManifest(station.Value);
            if (manifest != null)
                names.AddRange(manifest.Entries.Select(e => e.Name));
        }

        names = names.Distinct().ToList();
        names.Remove(selfName);
        names.Sort(StringComparer.CurrentCultureIgnoreCase);
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
