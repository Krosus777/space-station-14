using System.Numerics;
using System.Threading;

using Content.Server.Atmos.Piping.Trinary.Components;
using Content.Server.Atmos.Piping.Unary.Components;
using Content.Server.Body.Systems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Popups;
using Content.Shared.ActionBlocker;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Corvax.VentCrawl;
using Content.Shared.Database;
using Content.Shared.Eye;
using Content.Shared.Interaction.Events;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.NodeContainer;
using Content.Shared.SubFloor;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Timer = Robust.Shared.Timing.Timer;

namespace Content.Server.Corvax.VentCrawl;

public sealed class CorvaxVentCrawlSystem : EntitySystem
{
    private const string CrawlContainerId = "CorvaxVentCrawl";
    private static readonly TimeSpan CrawlStepInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CrawlTransitionDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan CrawlSafetyInterval = TimeSpan.FromMilliseconds(500);

    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedInternalsSystem _internals = default!;
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    [Dependency] private readonly EntityQuery<TransformComponent> _xformQuery = default!;
    [Dependency] private readonly EntityQuery<PhysicsComponent> _physicsQuery = default!;
    [Dependency] private readonly EntityQuery<NodeContainerComponent> _nodeContainerQuery = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Подписываемся на события, которые управляют входом, движением и выходом из вентиляции.
        SubscribeLocalEvent<CorvaxVentCrawlableComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, MoveInputEvent>(OnMoveInput);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, UpdateCanMoveEvent>(OnUpdateCanMove);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, InhaleLocationEvent>(OnInhaleLocation);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, ExhaleLocationEvent>(OnExhaleLocation);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, AtmosExposedGetAirEvent>(OnGetAir);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, CanAttackFromContainerEvent>(OnCanAttackFromContainer);
        SubscribeLocalEvent<CorvaxVentCrawlableComponent, EntityTerminatingEvent>(OnSegmentTerminating);
        SubscribeLocalEvent<CorvaxVentCrawlingComponent, ComponentShutdown>(OnCrawlingShutdown);
    }

    // Добавляет вербы входа/выхода только если сущность действительно может работать с этим сегментом.
    private void OnGetVerbs(EntityUid uid, CorvaxVentCrawlableComponent component, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !args.CanComplexInteract)
            return;

        var user = args.User;
        var isCrawling = TryComp(user, out CorvaxVentCrawlingComponent? crawling) && crawling.CurrentSegment == uid;

        if (isCrawling)
        {
            if (!IsCrawlEntryPoint(uid))
                return;

            if (!_container.TryGetContainingContainer((user, null, null), out var container) ||
                container.Owner != uid ||
                container.ID != CrawlContainerId)
            {
                return;
            }

            args.Verbs.Add(new Verb
            {
                Category = VerbCategory.Eject,
                Text = Loc.GetString("corvax-vent-crawl-exit-verb"),
                Impact = LogImpact.Low,
                DoContactInteraction = true,
                Act = () => TryExit(user),
            });

            return;
        }

        if (!HasComp<CorvaxVentCrawlerComponent>(user))
            return;

        if (_mobState.IsIncapacitated(user))
            return;

        if (!IsCrawlEntryPoint(uid))
            return;

        if (IsTraversalBlockedByDisabledDevice(uid))
            return;

        if (TryComp(uid, out AccessReaderComponent? accessReader) &&
            !_accessReader.IsAllowed(user, uid, accessReader))
            return;

        if (!TryGetCurrentPipeNode(uid, out _))
            return;

        args.Verbs.Add(new Verb
        {
            Category = VerbCategory.Insert,
            Text = Loc.GetString("corvax-vent-crawl-enter-verb"),
            Impact = LogImpact.Low,
            DoContactInteraction = true,
            Act = () => TryEnter(user, uid),
        });
    }

    // Отслеживает ввод движения во время crawl и решает, двигаться дальше или завершать состояние.
    private void OnMoveInput(EntityUid uid, CorvaxVentCrawlingComponent component, ref MoveInputEvent args)
    {
        if (component.Transitioning)
            return;

        if (!TryComp(uid, out InputMoverComponent? mover))
            return;

        if (mover.HeldMoveButtons == MoveButtons.None)
        {
            CancelCrawlTimer(component);

            if (!TryGetCurrentSegment(component, out _))
                TryExit(uid);

            return;
        }

        if (!TryGetCurrentSegment(component, out _))
        {
            TryExit(uid);
            return;
        }

        ProcessMovement(uid, component, mover);
    }

    // Полностью блокирует обычное перемещение, пока сущность находится в вентиляции.
    private void OnUpdateCanMove(EntityUid uid, CorvaxVentCrawlingComponent component, UpdateCanMoveEvent args)
    {
        args.Cancel();
    }

    // Обрабатывает попытку сделать шаг в вентиляционной сети и ставит таймер на следующую проверку.
    private void ProcessMovement(EntityUid uid, CorvaxVentCrawlingComponent component, InputMoverComponent mover)
    {
        if (component.Transitioning || mover.HeldMoveButtons == MoveButtons.None)
            return;

        CancelCrawlTimer(component);

        if (!TryGetCurrentSegment(component, out _))
        {
            TryExit(uid);
            return;
        }

        if (AttemptMove(uid, component, mover))
        {
            ScheduleCrawlTimer(uid, component, CrawlTransitionDuration);
            return;
        }

        if (!HasComp<CorvaxVentCrawlingComponent>(uid))
            return;

        ScheduleCrawlTimer(uid, component, CrawlStepInterval);
    }

    // Запускает защитный таймер, который вытащит игрока из вентиляции, если что-то пошло не так.
    private void ScheduleCrawlSafetyTimer(EntityUid uid, CorvaxVentCrawlingComponent component)
    {
        CancelCrawlSafetyTimer(component);

        component.CrawlSafetyTimerCancel = new CancellationTokenSource();
        Timer.Spawn(CrawlSafetyInterval, () => OnCrawlSafetyTimerFired(uid), component.CrawlSafetyTimerCancel.Token);
    }

    // Отменяет защитный таймер перед новой попыткой движения или выходом.
    private void CancelCrawlSafetyTimer(CorvaxVentCrawlingComponent component)
    {
        if (component.CrawlSafetyTimerCancel == null)
            return;

        component.CrawlSafetyTimerCancel.Cancel();
        component.CrawlSafetyTimerCancel.Dispose();
        component.CrawlSafetyTimerCancel = null;
    }

    // Проверяет, не пора ли завершить crawl из-за потери сегмента или заблокированного устройства.
    private void OnCrawlSafetyTimerFired(EntityUid uid)
    {
        if (!TryComp(uid, out CorvaxVentCrawlingComponent? component))
            return;

        if (component.Transitioning)
        {
            ScheduleCrawlSafetyTimer(uid, component);
            return;
        }

        if (!TryGetCurrentSegment(component, out var currentSegment) || IsTraversalBlockedByDisabledDevice(currentSegment))
        {
            TryExit(uid);
            return;
        }

        ScheduleCrawlSafetyTimer(uid, component);
    }

    // Основной таймер шага: завершает переход между сегментами или повторяет попытку движения.
    private void OnCrawlTimerFired(EntityUid uid)
    {
        if (!TryComp(uid, out CorvaxVentCrawlingComponent? component))
            return;

        if (!TryComp(uid, out InputMoverComponent? mover))
        {
            TryExit(uid);
            return;
        }

        if (_mobState.IsIncapacitated(uid))
        {
            CancelCrawlTimer(component);
            return;
        }

        if (component.Transitioning)
        {
            if (CompleteTransition(uid, component) &&
                TryComp(uid, out InputMoverComponent? afterMove) &&
                afterMove.HeldMoveButtons != MoveButtons.None)
            {
                ProcessMovement(uid, component, afterMove);
            }
            return;
        }

        if (mover.HeldMoveButtons == MoveButtons.None)
        {
            CancelCrawlTimer(component);
            return;
        }

        ProcessMovement(uid, component, mover);
    }

    // Ставит таймер следующего шага, чтобы движение происходило с анимационной задержкой.
    private void ScheduleCrawlTimer(EntityUid uid, CorvaxVentCrawlingComponent component, TimeSpan delay)
    {
        CancelCrawlTimer(component);

        component.CrawlTimerCancel = new CancellationTokenSource();
        Timer.Spawn(delay, () => OnCrawlTimerFired(uid), component.CrawlTimerCancel.Token);
    }

    // Сбрасывает таймер движения, чтобы старые попытки не конфликтовали с новыми.
    private void CancelCrawlTimer(CorvaxVentCrawlingComponent component)
    {
        if (component.CrawlTimerCancel == null)
            return;

        component.CrawlTimerCancel.Cancel();
        component.CrawlTimerCancel.Dispose();
        component.CrawlTimerCancel = null;
    }

    // Подставляет воздух текущего сегмента для вдоха, если внутренности не перекрывают вентиляцию.
    private void OnInhaleLocation(EntityUid uid, CorvaxVentCrawlingComponent component, ref InhaleLocationEvent args)
    {
        if (_internals.AreInternalsWorking(uid))
            return;

        if (TryGetCurrentPipeAir(component, out var air))
            args.Gas = air;
    }

    // Подставляет воздух текущего сегмента для выдоха по тем же правилам.
    private void OnExhaleLocation(EntityUid uid, CorvaxVentCrawlingComponent component, ref ExhaleLocationEvent args)
    {
        if (_internals.AreInternalsWorking(uid))
            return;

        if (TryGetCurrentPipeAir(component, out var air))
            args.Gas = air;
    }

    // Отдаёт системам, запрашивающим воздух, смесь из текущего pipe node.
    private void OnGetAir(EntityUid uid, CorvaxVentCrawlingComponent component, ref AtmosExposedGetAirEvent args)
    {
        if (_internals.AreInternalsWorking(uid))
            return;

        if (TryGetCurrentPipeAir(component, out var air))
        {
            args.Gas = air;
            args.Handled = true;
        }
    }

    // Запрещает атаки из контейнера вентиляции, чтобы crawl не давал атаковать сквозь стены.
    private void OnCanAttackFromContainer(EntityUid uid, CorvaxVentCrawlingComponent component, CanAttackFromContainerEvent args)
    {
        if (!TryGetCurrentSegment(component, out _))
            return;

        if (!_container.TryGetContainingContainer((uid, null, null), out var container) ||
            container.ID != CrawlContainerId)
        {
            return;
        }

        args.CanAttack = false;
    }

    // Если сегмент вентиляции удаляется, вытаскивает всех ползущих из этого сегмента.
    private void OnSegmentTerminating(EntityUid uid, CorvaxVentCrawlableComponent component, ref EntityTerminatingEvent args)
    {
        var query = EntityQueryEnumerator<CorvaxVentCrawlingComponent>();
        while (query.MoveNext(out var crawlingUid, out var crawling))
        {
            if (crawling.CurrentSegment != uid)
                continue;

            TryExit(crawlingUid);
        }
    }

    // При выгрузке компонента аккуратно отменяет таймеры и восстанавливает состояние сущности.
    private void OnCrawlingShutdown(EntityUid uid, CorvaxVentCrawlingComponent component, ComponentShutdown args)
    {
        CancelCrawlTimer(component);
        CancelCrawlSafetyTimer(component);

        if (Terminating(uid))
            return;

        if (component.Transitioning)
            return;

        RestoreAfterExit(uid, component, removeComponent: false);
    }

    // Проводит все проверки и помещает игрока в первый сегмент вентиляции.
    private bool TryEnter(EntityUid user, EntityUid target)
    {
        if (!HasComp<CorvaxVentCrawlerComponent>(user))
        {
            _popup.PopupEntity(Loc.GetString("corvax-vent-crawl-enter-fail-crawler"), user, user);
            return false;
        }

        if (!TryComp(target, out TransformComponent? targetXform))
            return false;

        if (HasComp<CorvaxVentCrawlingComponent>(user))
            return false;

        if (!IsCrawlEntryPoint(target))
            return false;

        if (!_actionBlocker.CanMove(user) || !_actionBlocker.CanInteract(user, target))
            return false;

        if (!TryComp(target, out AccessReaderComponent? accessReader))
            accessReader = null;

        if (accessReader != null && !_accessReader.IsAllowed(user, target, accessReader))
        {
            _popup.PopupEntity(Loc.GetString("corvax-vent-crawl-enter-fail-access"), user, user);
            return false;
        }

        if (IsTraversalBlockedByDisabledDevice(target))
        {
            _popup.PopupEntity(Loc.GetString("corvax-vent-crawl-enter-fail-blocked"), user, user);
            return false;
        }

        if (!TryGetCurrentPipeNode(target, out var targetPipeNode))
        {
            return false;
        }

        var container = _container.EnsureContainer<Container>(target, CrawlContainerId);
        var comp = EnsureComp<CorvaxVentCrawlingComponent>(user);
        comp.Transitioning = true;
        comp.CurrentSegment = target;
        comp.PreviousSegment = null;
        comp.CurrentPipeNodeName = targetPipeNode.Name;
        comp.LastKnownCoordinates = _xform.ToMapCoordinates(targetXform.Coordinates);

        if (!_container.Insert((user, null, null), container, targetXform))
        {
            CancelCrawlTimer(comp);
            CancelCrawlSafetyTimer(comp);
            comp.Transitioning = false;
            RemComp<CorvaxVentCrawlingComponent>(user);
            _popup.PopupEntity(Loc.GetString("corvax-vent-crawl-enter-fail-blocked"), user, user);
            return false;
        }

        comp.Transitioning = false;
        if (_physicsQuery.TryGetComponent(user, out var physics))
            _physics.SetCanCollide(user, false, body: physics);

        SetSubfloorVisible(user, true);
        _actionBlocker.UpdateCanMove(user);
        ScheduleCrawlSafetyTimer(user, comp);

        if (TryComp(user, out InputMoverComponent? mover) && mover.HeldMoveButtons != MoveButtons.None)
            ProcessMovement(user, comp, mover);

        return true;
    }

    // Завершает crawl и возвращает игрока из вентиляции в обычный мир.
    private bool TryExit(EntityUid user)
    {
        if (!TryComp(user, out CorvaxVentCrawlingComponent? component))
            return false;

        CancelCrawlTimer(component);
        CancelCrawlSafetyTimer(component);
        ResetTransitionState(component);
        RestoreAfterExit(user, component, removeComponent: true);
        return true;
    }

    // Общая логика восстановления позиции, коллизий и видимости после входа или выхода.
    private void RestoreAfterExit(EntityUid user, CorvaxVentCrawlingComponent component, bool removeComponent)
    {
        if (component.Transitioning)
            return;

        CancelCrawlTimer(component);
        CancelCrawlSafetyTimer(component);
        component.Transitioning = true;
        component.CurrentPipeNodeName = null;

        if (_container.TryGetContainingContainer((user, null, null), out var container) &&
            TryComp(user, out TransformComponent? userXform) &&
            TryComp(user, out MetaDataComponent? userMeta))
        {
            var destination = _xform.ToCoordinates(component.LastKnownCoordinates);
            _container.Remove((user, userXform, userMeta), container, reparent: false, force: true);
            _xform.SetCoordinates((user, userXform, userMeta), destination);
        }

        if (_physicsQuery.TryGetComponent(user, out var physics))
            _physics.SetCanCollide(user, true, body: physics);

        SetSubfloorVisible(user, false);

        if (removeComponent)
            RemComp<CorvaxVentCrawlingComponent>(user);

        _actionBlocker.UpdateCanMove(user);
    }

    // Сбрасывает промежуточное состояние перехода между сегментами.
    private void ResetTransitionState(CorvaxVentCrawlingComponent component)
    {
        component.Transitioning = false;
        component.TransitionFromSegment = null;
        component.TransitionToSegment = null;
    }

    // Делает подпол видимым и подключает tray scanner, пока сущность находится в вентиляции.
    private void SetSubfloorVisible(EntityUid user, bool enabled)
    {
        if (enabled)
        {
            var trayScannerUserEnabled = EnsureComp<TrayScannerUserComponent>(user);
            trayScannerUserEnabled.Count++;

            SetTrayScannerEnabled(user, true);

            if (trayScannerUserEnabled.Count == 1)
                _eye.RefreshVisibilityMask(user);
            return;
        }

        if (!TryComp(user, out TrayScannerUserComponent? trayScannerUserDisabled))
        {
            SetTrayScannerEnabled(user, false);
            return;
        }

        trayScannerUserDisabled.Count--;
        SetTrayScannerEnabled(user, false);

        if (trayScannerUserDisabled.Count > 0)
            return;

        RemComp<TrayScannerUserComponent>(user);
        _eye.RefreshVisibilityMask(user);
    }

    // Включает или выключает tray scanner, который нужен для подсвечивания подполовых объектов.
    private void SetTrayScannerEnabled(EntityUid user, bool enabled)
    {
        if (enabled)
        {
            var scanner = EnsureComp<TrayScannerComponent>(user);
            if (scanner.Enabled)
                return;

            scanner.Enabled = true;
            scanner.Range = 4f;
            Dirty(user, scanner);
            return;
        }

        if (HasComp<TrayScannerComponent>(user))
            RemComp<TrayScannerComponent>(user);
    }

    // Проверяет возможность перехода между двумя сегментами и переводит состояние в режим transition.
    private bool TryMoveBetweenSegments(EntityUid user, CorvaxVentCrawlingComponent component, EntityUid currentSegment, EntityUid nextSegment)
    {
        if (!TryComp(nextSegment, out TransformComponent? nextXform))
        {
            TryExit(user);
            return false;
        }

        if (!TryComp(user, out MetaDataComponent? userMeta))
        {
            TryExit(user);
            return false;
        }

        if (!_container.TryGetContainingContainer((user, null, null), out var currentContainer) || currentContainer.Owner != currentSegment)
        {
            TryExit(user);
            return false;
        }

        var nextContainer = _container.EnsureContainer<Container>(nextSegment, CrawlContainerId);
        if (!_container.CanInsert(user, nextContainer, containerXform: nextXform))
            return false;

        component.Transitioning = true;
        component.TransitionFromSegment = currentSegment;
        component.TransitionToSegment = nextSegment;
        return true;
    }

    // Финализирует переход: убирает сущность из старого сегмента и вставляет в новый.
    private bool CompleteTransition(EntityUid user, CorvaxVentCrawlingComponent component)
    {
        if (component.TransitionFromSegment is not { } fromSegment ||
            component.TransitionToSegment is not { } toSegment)
        {
            TryExit(user);
            return false;
        }

        if (!TryComp(user, out TransformComponent? userXform) ||
            !TryComp(toSegment, out TransformComponent? toXform))
        {
            TryExit(user);
            return false;
        }

        if (!_container.TryGetContainingContainer((user, null, null), out var currentContainer) || currentContainer.Owner != fromSegment)
        {
            TryExit(user);
            return false;
        }

        if (!TryComp(user, out MetaDataComponent? userMeta))
        {
            TryExit(user);
            return false;
        }

        if (!_container.Remove((user, userXform, userMeta), currentContainer, reparent: false, force: true))
        {
            TryExit(user);
            return false;
        }

        if (!TryGetPipeNodeForConnection(toSegment, fromSegment, component, out var nextPipeNode))
        {
            TryExit(user);
            return false;
        }

        var nextContainer = _container.EnsureContainer<Container>(toSegment, CrawlContainerId);
        if (!_container.Insert((user, userXform, userMeta), nextContainer, toXform, force: true))
        {
            TryExit(user);
            return false;
        }

        component.PreviousSegment = fromSegment;
        component.CurrentSegment = toSegment;
        component.CurrentPipeNodeName = nextPipeNode.Name;
        component.LastKnownCoordinates = _xform.ToMapCoordinates(toXform.Coordinates);
        ResetTransitionState(component);
        return true;
    }

    // Делает один шаг crawl по выбранному направлению, если есть доступный следующий сегмент.
    private bool AttemptMove(EntityUid user, CorvaxVentCrawlingComponent component, InputMoverComponent mover)
    {
        if (component.Transitioning || mover.HeldMoveButtons == MoveButtons.None)
            return false;

        if (!TryGetCurrentSegment(component, out var currentSegment))
        {
            TryExit(user);
            return false;
        }

        if (!TryResolveDirection(mover, mover.HeldMoveButtons, out var direction))
            return false;

        if (IsTraversalBlockedByDisabledDevice(currentSegment))
            return false;

        if (!TryGetNextSegment(currentSegment, component, direction, out var nextSegment))
            return false;

        return TryMoveBetweenSegments(user, component, currentSegment, nextSegment);
    }

    // Ищет следующий сегмент вентиляции в заданном направлении.
    private bool TryGetNextSegment(EntityUid currentSegment, CorvaxVentCrawlingComponent component, Direction direction, out EntityUid nextSegment)
    {
        nextSegment = EntityUid.Invalid;

        var currentMap = _xform.ToMapCoordinates(Transform(currentSegment).Coordinates);

        if (!_nodeContainerQuery.TryGetComponent(currentSegment, out var nodeContainer))
            return false;

        foreach (var currentPipeNode in EnumerateCurrentPipeNodeCandidates(currentSegment, component, nodeContainer))
        {
            if (TryGetReachableSegmentInDirection(currentSegment, currentMap, currentPipeNode, direction, out var reachableSegment))
            {
                component.CurrentPipeNodeName = currentPipeNode.Name;
                nextSegment = reachableSegment;
                return true;
            }
        }

        return false;
    }

    // Проверяет, не заблокировано ли прохождение через этот сегмент выключенным устройством.
    private bool IsTraversalBlockedByDisabledDevice(EntityUid uid)
    {
        return (TryComp(uid, out GasVentPumpComponent? ventPump) && !ventPump.Enabled)
            || (TryComp(uid, out GasVentScrubberComponent? ventScrubber) && !ventScrubber.Enabled)
            || (TryComp(uid, out GasOutletInjectorComponent? outletInjector) && !outletInjector.Enabled)
            || (TryComp(uid, out GasPressurePumpComponent? pressurePump) && !pressurePump.Enabled)
            || (TryComp(uid, out GasFilterComponent? filter) && !filter.Enabled)
            || (TryComp(uid, out GasMixerComponent? mixer) && !mixer.Enabled);
    }

    // Возвращает газовую смесь текущего pipe node, чтобы использовать её как воздух сущности.
    private bool TryGetCurrentPipeAir(CorvaxVentCrawlingComponent component, out GasMixture air)
    {
        air = null!;

        if (!TryGetCurrentSegment(component, out var currentSegment))
            return false;

        if (!TryGetCurrentPipeNode(currentSegment, component, out var currentPipeNode))
            return false;

        air = currentPipeNode.Air;
        return true;
    }

    // Подбирает pipe node, который соединяет сегмент с уже известным соседом.
    private bool TryGetPipeNodeForConnection(EntityUid segment, EntityUid connectedSegment, CorvaxVentCrawlingComponent component, out PipeNode pipeNode)
    {
        pipeNode = null!;

        if (!_nodeContainerQuery.TryGetComponent(segment, out var nodeContainer))
            return false;

        PipeNode? namedPipeNode = null;
        PipeNode? connectedPipeNode = null;

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is not PipeNode currentPipeNode)
                continue;

            if (component.CurrentPipeNodeName != null && currentPipeNode.Name == component.CurrentPipeNodeName)
            {
                namedPipeNode = currentPipeNode;
            }

            foreach (var reachable in currentPipeNode.ReachableNodes)
            {
                if (reachable is not PipeNode reachablePipe)
                    continue;

                if (reachablePipe.Owner == connectedSegment && reachablePipe.NodeGroup == currentPipeNode.NodeGroup)
                {
                    if (namedPipeNode != null && namedPipeNode.Name == currentPipeNode.Name)
                    {
                        pipeNode = currentPipeNode;
                        return true;
                    }

                    connectedPipeNode ??= currentPipeNode;
                }
            }
        }

        if (connectedPipeNode != null)
        {
            pipeNode = connectedPipeNode;
            return true;
        }

        if (namedPipeNode != null)
        {
            pipeNode = namedPipeNode;
            return true;
        }

        return false;
    }

    // Возвращает текущий pipe node для сегмента, опираясь на имя, связь с прошлым сегментом и запасной поиск.
    private bool TryGetCurrentPipeNode(EntityUid currentSegment, CorvaxVentCrawlingComponent component, out PipeNode currentPipeNode)
    {
        currentPipeNode = null!;

        if (!_nodeContainerQuery.TryGetComponent(currentSegment, out var nodeContainer))
            return false;

        if (component.CurrentPipeNodeName != null &&
            nodeContainer.Nodes.TryGetValue(component.CurrentPipeNodeName, out var currentNode) &&
            currentNode is PipeNode namedPipeNode)
        {
            currentPipeNode = namedPipeNode;
            return true;
        }

        if (component.PreviousSegment is { } previousSegment &&
            TryGetPipeNodeForConnection(currentSegment, previousSegment, component, out currentPipeNode))
        {
            component.CurrentPipeNodeName = currentPipeNode.Name;
            return true;
        }

        if (component.CurrentSegment == currentSegment)
        {
            var found = false;
            foreach (var node in nodeContainer.Nodes.Values)
            {
                if (node is not PipeNode pipeNode)
                    continue;

                if (found)
                    return false;

                currentPipeNode = pipeNode;
                found = true;
            }

            if (found)
            {
                component.CurrentPipeNodeName = currentPipeNode.Name;
                return true;
            }
        }

        return false;
    }

    // Перечисляет кандидатов на текущий pipe node в порядке от наиболее вероятного к запасному.
    private IEnumerable<PipeNode> EnumerateCurrentPipeNodeCandidates(
        EntityUid currentSegment,
        CorvaxVentCrawlingComponent component,
        NodeContainerComponent nodeContainer)
    {
        var yielded = new HashSet<string>();

        if (component.CurrentPipeNodeName != null &&
            nodeContainer.Nodes.TryGetValue(component.CurrentPipeNodeName, out var currentNode) &&
            currentNode is PipeNode namedPipeNode)
        {
            yielded.Add(namedPipeNode.Name);
            yield return namedPipeNode;
        }

        if (component.PreviousSegment is { } previousSegment &&
            TryGetPipeNodeForConnection(currentSegment, previousSegment, component, out var connectedPipeNode) &&
            yielded.Add(connectedPipeNode.Name))
        {
            yield return connectedPipeNode;
        }

        AtmosPipeLayersComponent? atmosPipeLayers = null;
        AtmosPipeLayer? preferredLayer = null;
        if (TryComp(currentSegment, out atmosPipeLayers))
            preferredLayer = atmosPipeLayers.CurrentPipeLayer;

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is not PipeNode pipeNode)
                continue;

            if (!yielded.Add(pipeNode.Name))
                continue;

            if (preferredLayer != null && pipeNode.CurrentPipeLayer != preferredLayer.Value)
                continue;

            yield return pipeNode;
        }

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is not PipeNode pipeNode)
                continue;

            if (!yielded.Add(pipeNode.Name))
                continue;

            yield return pipeNode;
        }
    }

    // Проверяет, есть ли среди reachable nodes ровно та ветка, которая соответствует заданному направлению.
    private bool TryGetReachableSegmentInDirection(
        EntityUid currentSegment,
        MapCoordinates currentMap,
        PipeNode currentPipeNode,
        Direction direction,
        out EntityUid nextSegment)
    {
        nextSegment = EntityUid.Invalid;

        foreach (var reachable in currentPipeNode.ReachableNodes)
        {
            if (reachable is not PipeNode reachablePipe)
                continue;

            if (reachablePipe.Owner == currentSegment)
                continue;

            if (reachablePipe.NodeGroup != currentPipeNode.NodeGroup)
                continue;

            if (!_xformQuery.TryGetComponent(reachablePipe.Owner, out var reachableXform))
                continue;

            var targetMap = _xform.ToMapCoordinates(reachableXform.Coordinates);
            var delta = targetMap.Position - currentMap.Position;

            var deltaXi = new Vector2i((int) Math.Round(delta.X), (int) Math.Round(delta.Y));
            Direction reachableDirection;
            try
            {
                reachableDirection = deltaXi.AsDirection();
            }
            catch
            {
                continue;
            }

            if (reachableDirection != direction)
                continue;

            if (IsTraversalBlockedByDisabledDevice(reachablePipe.Owner))
                continue;

            nextSegment = reachablePipe.Owner;
            return true;
        }

        return false;
    }

    // Находит текущий pipe node без привязки к конкретному соединению с соседним сегментом.
    private bool TryGetCurrentPipeNode(EntityUid currentSegment, out PipeNode currentPipeNode)
    {
        currentPipeNode = null!;

        if (!_nodeContainerQuery.TryGetComponent(currentSegment, out var nodeContainer))
            return false;

        AtmosPipeLayersComponent? atmosPipeLayers = null;
        AtmosPipeLayer? preferredLayer = null;
        if (TryComp(currentSegment, out atmosPipeLayers))
            preferredLayer = atmosPipeLayers.CurrentPipeLayer;

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is not PipeNode pipeNode)
                continue;

            if (preferredLayer != null && pipeNode.CurrentPipeLayer != preferredLayer.Value)
                continue;

            currentPipeNode = pipeNode;
            return true;
        }

        foreach (var node in nodeContainer.Nodes.Values)
        {
            if (node is PipeNode pipeNode)
            {
                currentPipeNode = pipeNode;
                return true;
            }
        }

        return false;
    }

    // Переводит нажатые кнопки движения в мировое cardinal направление с учётом поворота сущности.
    private bool TryResolveDirection(InputMoverComponent mover, MoveButtons buttons, out Direction direction)
    {
        direction = Direction.Invalid;

        var normalized = SharedMoverController.GetNormalizedMovement(buttons);

        var count =
            ((normalized & MoveButtons.Up) != 0 ? 1 : 0) +
            ((normalized & MoveButtons.Down) != 0 ? 1 : 0) +
            ((normalized & MoveButtons.Left) != 0 ? 1 : 0) +
            ((normalized & MoveButtons.Right) != 0 ? 1 : 0);

        if (count != 1)
            return false;

        var screenVec = Vector2.Zero;
        if ((normalized & MoveButtons.Left) != 0)
            screenVec.X -= 1;
        if ((normalized & MoveButtons.Right) != 0)
            screenVec.X += 1;
        if ((normalized & MoveButtons.Down) != 0)
            screenVec.Y -= 1;
        if ((normalized & MoveButtons.Up) != 0)
            screenVec.Y += 1;

        if (screenVec.LengthSquared() <= 0.0f)
            return false;

        var rotation = mover.RelativeRotation;
        if (mover.RelativeEntity is { } relativeEntity && _xformQuery.TryGetComponent(relativeEntity, out var relativeXform))
            rotation += _xform.GetWorldRotation(relativeXform);

        var worldVec = rotation.RotateVec(screenVec);
        var worldCardinal = new Vector2i((int) Math.Round(worldVec.X), (int) Math.Round(worldVec.Y));

        try
        {
            direction = worldCardinal.AsDirection();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Проверяет, что сущность действительно находится в ожидаемом контейнере вентиляции.
    private bool IsInsideExpectedContainer(EntityUid user, EntityUid expectedSegment)
    {
        return _container.TryGetContainingContainer((user, null, null), out var container) &&
               container.Owner == expectedSegment &&
               container.ID == CrawlContainerId;
    }

    // Возвращает текущий сегмент crawl, если он уже сохранён в компоненте.
    private bool TryGetCurrentSegment(CorvaxVentCrawlingComponent component, out EntityUid currentSegment)
    {
        currentSegment = EntityUid.Invalid;

        if (component.CurrentSegment is not { } segment)
            return false;

        currentSegment = segment;
        return true;
    }

    // Определяет, является ли сущность подходящей точкой входа в вентиляцию.
    private bool IsCrawlEntryPoint(EntityUid uid)
    {
        return HasComp<GasVentPumpComponent>(uid) ||
               HasComp<GasPassiveVentComponent>(uid) ||
               HasComp<GasVentScrubberComponent>(uid);
    }

}
