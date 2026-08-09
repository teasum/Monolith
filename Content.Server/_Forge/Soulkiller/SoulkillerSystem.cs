// Author: @lenta313. Все права не защищены / No rights reserved.
using Content.Server.Storage.Components;
using Content.Shared._CorvaxNext.Silicons.Borgs;
using Content.Shared._CorvaxNext.Silicons.Borgs.Components;
using Content.Shared._Forge.Soulkiller;
using Content.Shared.Actions;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.DoAfter;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Forge.Soulkiller;

/// <summary>
/// Implements the "Душегуб" mechanic: an entity enters a cryo-style capsule connector.
/// Closing the capsule moves their mind into a Station-AI core, turning them into a fully-functional
/// station AI while their real body is sealed inside the capsule.
/// </summary>
public sealed class SoulkillerSystem : SharedSoulkillerSystem
{
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedAiRemoteControlSystem _aiRemote = default!;
    [Dependency] private readonly SharedEntityStorageSystem _entityStorage = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;

    private const string SoulkillerLinkPort = "SoulkillerLink";

    /// <summary>Connectors currently being opened from code (disconnect / extract) — skip the delay.</summary>
    private readonly HashSet<EntityUid> _instantOpenConnectors = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SoulkillerConnectorComponent, StorageAfterCloseEvent>(OnPodClosed);
        SubscribeLocalEvent<SoulkillerConnectorComponent, StorageOpenAttemptEvent>(OnPodOpenAttempt);
        SubscribeLocalEvent<SoulkillerConnectorComponent, StorageBeforeOpenEvent>(OnPodOpening);
        SubscribeLocalEvent<SoulkillerConnectorComponent, StorageAfterOpenEvent>(OnPodOpened);
        SubscribeLocalEvent<SoulkillerConnectorComponent, SoulkillerExtractDoAfterEvent>(OnExtractDoAfter);
        SubscribeLocalEvent<SoulkillerConnectorComponent, EntityTerminatingEvent>(OnConnectorTerminating);

        SubscribeLocalEvent<SoulkillerConnectorComponent, NewLinkEvent>(OnConnectorLinked);
        SubscribeLocalEvent<SoulkillerConnectorComponent, PortDisconnectedEvent>(OnConnectorUnlinked);

        SubscribeLocalEvent<SoulkillerInhabitantComponent, SoulkillerReturnToBodyEvent>(OnReturnToBody);
        SubscribeLocalEvent<SoulkillerInhabitantComponent, SoulkillerJumpToServerEvent>(OnJumpToServer);

        SubscribeLocalEvent<SoulkillerComponent, EntityTerminatingEvent>(OnCoreTerminating);
        SubscribeLocalEvent<SoulkillerComponent, PowerChangedEvent>(OnCorePowerChanged);

        SubscribeLocalEvent<SoulkillerTetheredBodyComponent, MobStateChangedEvent>(OnBodyMobStateChanged);

        SubscribeLocalEvent<AiRemoteControllerComponent, MobStateChangedEvent>(OnControlledBorgMobState);
    }

    private void OnPodClosed(Entity<SoulkillerConnectorComponent> ent, ref StorageAfterCloseEvent args)
    {
        var connected = false;
        var hasValidOccupant = false;

        if (TryComp<EntityStorageComponent>(ent, out var storage))
        {
            foreach (var occupant in storage.Contents.ContainedEntities)
            {
                if (ent.Comp.Whitelist == null || _whitelist.IsValid(ent.Comp.Whitelist, occupant))
                {
                    hasValidOccupant = true;
                }

                if (TryConnect(ent, occupant))
                {
                    connected = true;
                    break;
                }
            }
        }

        if (!connected || !hasValidOccupant)
        {
            OpenCapsule(ent.Owner);
            return;
        }

        SetConnectorVisual(ent, SoulkillerConnectorState.Active);
    }

    private void OnPodOpening(Entity<SoulkillerConnectorComponent> ent, ref StorageBeforeOpenEvent args)
    {
        if (TryGetConnectedCore(ent, out var core))
            Disconnect(core, openPod: false);
    }

    private void OnPodOpened(Entity<SoulkillerConnectorComponent> ent, ref StorageAfterOpenEvent args)
    {
        SetConnectorVisual(ent, SoulkillerConnectorState.Open);
    }

    private void OnPodOpenAttempt(Entity<SoulkillerConnectorComponent> ent, ref StorageOpenAttemptEvent args)
    {
        if (args.Cancelled || _instantOpenConnectors.Contains(ent.Owner))
            return;

        if (!TryComp<EntityStorageComponent>(ent, out var storage) || storage.Contents.ContainedEntities.Count == 0)
            return;

        args.Cancelled = true;

        var doAfter = new DoAfterArgs(EntityManager, args.User, ent.Comp.ExtractTime, new SoulkillerExtractDoAfterEvent(), ent.Owner, target: ent.Owner)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            CancelDuplicate = true,
            DuplicateCondition = DuplicateConditions.SameEvent,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return;

        if (!args.Silent)
            _popup.PopupEntity(Loc.GetString("soulkiller-capsule-extracting"), ent, args.User);

        if (TryGetConnectedCore(ent, out var core) && core.Comp.SpawnedBrain is { } brain)
        {
            var at = TryComp<StationAiCoreComponent>(core, out var aiCore) && aiCore.RemoteEntity is { } eye
                ? eye
                : brain;
            _popup.PopupEntity(Loc.GetString("soulkiller-being-extracted"), at, brain, PopupType.LargeCaution);
        }
    }

    private void OnExtractDoAfter(Entity<SoulkillerConnectorComponent> ent, ref SoulkillerExtractDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        args.Handled = true;
        OpenCapsule(ent.Owner);
    }

    private void OpenCapsule(EntityUid connector)
    {
        if (!TryComp<EntityStorageComponent>(connector, out var storage) || storage.Open)
            return;

        _instantOpenConnectors.Add(connector);
        _entityStorage.OpenStorage(connector, storage);
        _instantOpenConnectors.Remove(connector);
    }

    private void OnConnectorTerminating(Entity<SoulkillerConnectorComponent> ent, ref EntityTerminatingEvent args)
    {
        if (TryGetConnectedCore(ent, out var core))
            Disconnect(core, openPod: false);
    }

    private void OnConnectorLinked(EntityUid uid, SoulkillerConnectorComponent component, NewLinkEvent args)
    {
        if (args.SourcePort != SoulkillerLinkPort || !HasComp<SoulkillerComponent>(args.Sink))
            return;

        component.LinkedSoulkiller = args.Sink;
    }

    private void OnConnectorUnlinked(EntityUid uid, SoulkillerConnectorComponent component, PortDisconnectedEvent args)
    {
        if (args.Port == SoulkillerLinkPort)
            component.LinkedSoulkiller = null;
    }

    private bool TryConnect(Entity<SoulkillerConnectorComponent> connector, EntityUid user)
    {
        if (!_mind.TryGetMind(user, out var mindId, out var mind))
            return false;

        if (mind.VisitingEntity != null)
            return false;

        if (connector.Comp.Whitelist is { } whitelist && !_whitelist.IsValid(whitelist, user))
        {
            _popup.PopupEntity(Loc.GetString("soulkiller-connector-wrong-species"), connector, user);
            return false;
        }

        if (!TryResolveSoulkiller(connector, out var core))
        {
            _popup.PopupEntity(Loc.GetString("soulkiller-connector-no-shell"), connector, user);
            return false;
        }

        var container = _container.EnsureContainer<ContainerSlot>(core, core.Comp.MindSlotContainerId);
        if (core.Comp.InhabitingMind != null || container.ContainedEntity != null)
        {
            _popup.PopupEntity(Loc.GetString("soulkiller-connector-occupied"), connector, user);
            return false;
        }
        var brain = Spawn(core.Comp.BrainProto, Transform(core).Coordinates);
        if (!_container.Insert(brain, container))
        {
            Del(brain);
            _popup.PopupEntity(Loc.GetString("soulkiller-connector-occupied"), connector, user);
            return false;
        }

        SetupBrainIdentity(user, brain, core.Comp);
        var inhabitant = EnsureComp<SoulkillerInhabitantComponent>(brain);
        inhabitant.Core = core;

        core.Comp.SpawnedBrain = brain;
        core.Comp.InhabitingMind = mindId;
        core.Comp.TetheredBody = user;
        core.Comp.Connector = connector;
        Dirty(core);

        TagBody(user, core);

        _mind.TransferTo(mindId, brain, mind: mind);

        _actions.AddAction(brain, ref core.Comp.ReturnActionEntity, core.Comp.ReturnAction);

        _popup.PopupEntity(Loc.GetString("soulkiller-connector-connected"), core, user);
        return true;
    }

    /// <summary>
    /// Настраивает имя создаваемого мозга ИИ: копирует имя борга
    /// или использует имя из прототипа с захардкоженным суффиксом PB-XX для остальных.
    /// </summary>
    private void SetupBrainIdentity(EntityUid user, EntityUid brain, SoulkillerComponent coreComp)
    {
        if (HasComp<BorgChassisComponent>(user))
        {
            _metaData.SetEntityName(brain, Name(user));
        }
        else
        {
            var randomDigits = _random.Next(10, 99);
            // Базовое имя из прототипа + жестко зафиксированный в C# суффикс " PB-XX"
            var formattedName = $"{coreComp.DefaultDigitizedName} PB-{randomDigits}";
            _metaData.SetEntityName(brain, formattedName);
        }
    }

    private void TagBody(EntityUid body, Entity<SoulkillerComponent> core)
    {
        var tag = EnsureComp<SoulkillerTetheredBodyComponent>(body);
        tag.Core = core;
    }

    private void ReleaseBody(EntityUid body)
    {
        RemComp<SoulkillerTetheredBodyComponent>(body);
    }

    private void OnReturnToBody(Entity<SoulkillerInhabitantComponent> ent, ref SoulkillerReturnToBodyEvent args)
    {
        args.Handled = true;

        if (TryComp<SoulkillerComponent>(ent.Comp.Core, out var core))
            Disconnect((ent.Comp.Core, core));
    }

    private void OnJumpToServer(Entity<SoulkillerInhabitantComponent> ent, ref SoulkillerJumpToServerEvent args)
    {
        args.Handled = true;

        var core = ent.Comp.Core;

        EntityUid? server = null;
        if (TryComp<DeviceLinkSinkComponent>(core, out var sink))
        {
            foreach (var source in sink.LinkedSources)
            {
                if (!Deleted(source) && HasComp<SoulkillerServerComponent>(source))
                {
                    server = source;
                    break;
                }
            }
        }

        if (server is not { } target)
        {
            _popup.PopupEntity(Loc.GetString("soulkiller-no-server"), ent, ent);
            return;
        }

        if (!TryComp<StationAiCoreComponent>(core, out var aiCore) || aiCore.RemoteEntity is not { } eye)
            return;

        _xform.DropNextTo(eye, target);
    }

    private void OnCoreTerminating(Entity<SoulkillerComponent> ent, ref EntityTerminatingEvent args)
    {
        Disconnect((ent, ent.Comp), coreTerminating: true);
    }

    private void OnCorePowerChanged(Entity<SoulkillerComponent> ent, ref PowerChangedEvent args)
    {
        if (!args.Powered && ent.Comp.InhabitingMind != null)
            Disconnect((ent, ent.Comp));
    }

    private void OnBodyMobStateChanged(Entity<SoulkillerTetheredBodyComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState is not (MobState.Critical or MobState.Dead))
            return;

        if (TryComp<SoulkillerComponent>(ent.Comp.Core, out var core))
            Disconnect((ent.Comp.Core, core));
    }

    private void OnControlledBorgMobState(Entity<AiRemoteControllerComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState is not (MobState.Critical or MobState.Dead))
            return;

        if (ent.Comp.LinkedMind == null
            || ent.Comp.AiHolder is not { } holder
            || !HasComp<SoulkillerInhabitantComponent>(holder))
            return;

        _aiRemote.ReturnMindIntoAi(ent);
    }

    private void Disconnect(Entity<SoulkillerComponent> core, bool coreTerminating = false, bool openPod = true)
    {
        var mindId = core.Comp.InhabitingMind;
        var body = core.Comp.TetheredBody;
        var brain = core.Comp.SpawnedBrain;
        var connector = core.Comp.Connector;

        if (mindId is { } mind)
        {
            if (TryComp<MindComponent>(mind, out var mindComp)
                && mindComp.CurrentEntity is { } current
                && current != brain
                && TryComp<AiRemoteControllerComponent>(current, out var remote))
            {
                remote.AiHolder = null;
                remote.LinkedMind = null;
            }

            if (body is { } bodyUid && !Deleted(bodyUid))
                _mind.TransferTo(mind, bodyUid, ghostCheckOverride: true);
        }

        if (body is { } b)
            ReleaseBody(b);

        if (brain is { } br && !Deleted(br))
            QueueDel(br);

        core.Comp.ReturnActionEntity = null;
        core.Comp.SpawnedBrain = null;
        core.Comp.InhabitingMind = null;
        core.Comp.TetheredBody = null;
        core.Comp.Connector = null;

        if (!coreTerminating && !Terminating(core))
            Dirty(core);

        if (openPod
            && connector is { } conn
            && !Deleted(conn)
            && !Terminating(conn))
        {
            OpenCapsule(conn);
        }
    }

    private void SetConnectorVisual(EntityUid connector, SoulkillerConnectorState state)
    {
        _appearance.SetData(connector, SoulkillerConnectorVisuals.State, state);
    }

    private bool TryGetConnectedCore(EntityUid connector, out Entity<SoulkillerComponent> core)
    {
        core = default;

        if (!TryComp<SoulkillerConnectorComponent>(connector, out var conn)
            || conn.LinkedSoulkiller is not { } linked
            || !TryComp<SoulkillerComponent>(linked, out var comp))
            return false;

        if (comp.InhabitingMind == null || comp.Connector != connector)
            return false;

        core = (linked, comp);
        return true;
    }

    private bool TryResolveSoulkiller(Entity<SoulkillerConnectorComponent> connector, out Entity<SoulkillerComponent> core)
    {
        core = default;

        if (connector.Comp.LinkedSoulkiller is { } linked
            && !Deleted(linked)
            && TryComp<SoulkillerComponent>(linked, out var linkedComp))
        {
            core = (linked, linkedComp);
            return true;
        }

        return false;
    }
}
