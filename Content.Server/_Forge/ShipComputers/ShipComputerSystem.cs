using Content.Shared._Forge.ShipComputers;
using Content.Shared.Power;
using Content.Shared.Power.Components;
using Content.Server._Mono.FireControl; // Forge-Change
using Content.Server.Power.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.DeviceLinking.Systems;
using Content.Shared.DeviceLinking;
using Content.Shared.Containers.ItemSlots;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.UserInterface;
using Robust.Shared.Containers;

namespace Content.Server._Forge.ShipComputers;

public sealed class ShipComputerSystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly DeviceLinkSystem _deviceLink = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly ThrusterSystem _thrusters = default!;
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _activeModules = new();
    private readonly Dictionary<EntityUid, Dictionary<EntityUid, EntityUid>> _activeGridModules = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShipComputerComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ShipComputerComponent, ComponentShutdown>(OnShutdown); // Forge-Change
        SubscribeLocalEvent<ShipComputerComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<ShipComputerComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<ShipComputerComponent, PowerChangedEvent>(OnPowerChanged); // Forge-Change
        SubscribeLocalEvent<ShipComputerComponent, EntParentChangedMessage>(OnParentChanged); // Forge-Change
        SubscribeLocalEvent<ShuttleConsoleComponent, ActivatableUIOpenAttemptEvent>(OnShuttleConsoleOpenAttempt);
    }

    private void OnStartup(EntityUid uid, ShipComputerComponent component, ComponentStartup args)
    {
        _deviceLink.EnsureSinkPorts(uid, component.ConsoleSinkPort);
        _itemSlots.AddItemSlot(uid, ShipComputerComponent.ProcessorSlotId, component.ProcessorSlot);
        _itemSlots.AddItemSlot(uid, ShipComputerComponent.PowerSupplySlotId, component.PowerSupplySlot);
        component.Modules = _containers.EnsureContainer<Container>(uid, ShipComputerComponent.ModulesContainerId);
        foreach (var moduleProto in component.DefaultModules)
            SpawnInContainerOrDrop(moduleProto, uid, ShipComputerComponent.ModulesContainerId);
        Recalculate(uid, component);
        ApplyModuleComponents(uid, component); // Forge-Change
    }

    private void OnShutdown(EntityUid uid, ShipComputerComponent component, ComponentShutdown args) // Forge-Change
    {
        RemoveModuleComponents(uid);
    }

    private void OnContainerChanged(EntityUid uid, ShipComputerComponent component, ContainerModifiedMessage args)
    {
        Recalculate(uid, component);
        ApplyModuleComponents(uid, component); // Forge-Change
    }

    private void OnPowerChanged(EntityUid uid, ShipComputerComponent component, PowerChangedEvent args) // Forge-Change
    {
        if (args.Powered)
            ApplyModuleComponents(uid, component);
        else
        {
            RemoveModuleComponents(uid);
            if (TryComp<FireControlServerComponent>(uid, out var server))
                server.ProcessingPower = 0;
            UpdateThrusterAvailability(_transform.GetGrid(uid), 0, component);
        }
    }

    private void OnParentChanged(EntityUid uid, ShipComputerComponent component, ref EntParentChangedMessage args) // Forge-Change
    {
        ApplyModuleComponents(uid, component);
    }

    private void OnShuttleConsoleOpenAttempt(EntityUid uid, ShuttleConsoleComponent component,
        ref ActivatableUIOpenAttemptEvent args)
    {
        var grid = _transform.GetGrid(uid);
        if (grid == null)
            return;

        var hasFireControl = false;
        var computers = EntityQueryEnumerator<ShipComputerComponent, TransformComponent>();
        while (computers.MoveNext(out var computerUid, out _, out var transform))
        {
            if (transform.GridUid != grid)
                continue;

            hasFireControl |= HasComp<FireControlServerComponent>(computerUid);
        }

        if (!hasFireControl)
            args.Cancel();
    }

    private void ApplyModuleComponents(EntityUid uid, ShipComputerComponent component) // Forge-Change
    {
        RemoveModuleComponents(uid);
        if (TryComp<ApcPowerReceiverComponent>(uid, out var receiver) &&
            !_power.IsPowered(uid, receiver))
            return;

        var modules = new HashSet<EntityUid>();
        var gridModules = new Dictionary<EntityUid, EntityUid>();
        var grid = _transform.GetGrid(uid);
        foreach (var module in component.Modules.ContainedEntities)
        {
            if (!TryComp<ShipComputerModuleComponent>(module, out var moduleComp) || !moduleComp.Enabled)
                continue;

            EntityManager.AddComponents(uid, moduleComp.AddToServer);
            modules.Add(module);

            if (grid is { } gridUid)
            {
                EntityManager.AddComponents(gridUid, moduleComp.AddToGrid);
                gridModules[module] = gridUid;
            }
        }

        if (modules.Count > 0)
            _activeModules[uid] = modules;
        if (gridModules.Count > 0)
            _activeGridModules[uid] = gridModules;

        Recalculate(uid, component);
    }

    private void RemoveModuleComponents(EntityUid uid) // Forge-Change
    {
        if (_activeModules.Remove(uid, out var modules))
        {
            foreach (var module in modules)
            {
                if (TryComp<ShipComputerModuleComponent>(module, out var moduleComp) && Exists(uid))
                    EntityManager.RemoveComponents(uid, moduleComp.AddToServer);
            }
        }

        if (_activeGridModules.Remove(uid, out var gridModules))
        {
            foreach (var (module, grid) in gridModules)
            {
                if (TryComp<ShipComputerModuleComponent>(module, out var moduleComp) && Exists(grid))
                    EntityManager.RemoveComponents(grid, moduleComp.AddToGrid);
            }
        }
    }

    private void Recalculate(EntityUid uid, ShipComputerComponent component)
    {
        var processing = 0f;
        var capacity = 0f;
        var load = component.BasePowerLoad;

        if (component.ProcessorSlot.Item is { } processor &&
            TryComp<ShipComputerPartComponent>(processor, out var processorPart))
        {
            processing += processorPart.ProcessingPower;
            load += processorPart.PowerLoad;
        }

        if (component.PowerSupplySlot.Item is { } powerSupply &&
            TryComp<ShipComputerPartComponent>(powerSupply, out var powerPart))
        {
            capacity += powerPart.PowerSupplyCapacity;
            load += powerPart.PowerLoad;
        }

        var used = 0f;
        foreach (var module in component.Modules.ContainedEntities)
        {
            if (!TryComp<ShipComputerModuleComponent>(module, out var moduleComp))
                continue;

            load += moduleComp.PowerLoad;
            if (moduleComp.Enabled)
                used += moduleComp.ProcessingPowerCost;
        }

        var grid = _transform.GetGrid(uid);
        var thrusterPowerUse = 0f;
        if (grid is { } gridUid)
        {
            var thrusterQuery = AllEntityQuery<ThrusterComponent, TransformComponent>();
            while (thrusterQuery.MoveNext(out var thrusterUid, out var thruster, out var transform))
            {
                if (transform.GridUid != gridUid)
                    continue;

                thrusterPowerUse += thruster.Type == ThrusterType.Angular
                    ? component.GyroscopeProcessingPowerCost
                    : component.ThrusterProcessingPowerCost;
            }
        }

        used += thrusterPowerUse;

        component.ProcessingPower = processing;
        component.PowerSupplyCapacity = capacity;
        component.UsedProcessingPower = used;
        if (TryComp<ApcPowerReceiverComponent>(uid, out var receiver))
            receiver.Load = load;

        var withinSupplyLimit = capacity > 0 && load <= capacity;

        // Forge-Change-start: expose the installed gunnery module's compute budget to FireControl.
        if (TryComp<FireControlServerComponent>(uid, out var gunnery))
        {
            var gunneryInstalled = false;
            var gunneryModuleUse = 0f;
            var otherModuleUse = 0f;
            foreach (var module in component.Modules.ContainedEntities)
            {
                if (!TryComp<ShipComputerModuleComponent>(module, out var moduleComp) || !moduleComp.Enabled)
                    continue;

                if (moduleComp.ProvidesFireControl)
                {
                    gunneryInstalled = true;
                    gunneryModuleUse += moduleComp.ProcessingPowerCost;
                }
                else
                    otherModuleUse += moduleComp.ProcessingPowerCost;
            }

            gunnery.ProcessingPower = gunneryInstalled && withinSupplyLimit
                ? Math.Max(0, (int) MathF.Floor(processing - otherModuleUse - gunneryModuleUse - thrusterPowerUse))
                : 0;
        }
        // Forge-Change-end

        UpdateThrusterAvailability(grid, withinSupplyLimit ? processing - used + thrusterPowerUse : 0, component);
    }

    private void UpdateThrusterAvailability(EntityUid? grid, float availablePower, ShipComputerComponent computer)
    {
        if (grid == null)
            return;

        var query = AllEntityQuery<ThrusterComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var thruster, out var transform))
        {
            if (transform.GridUid != grid)
                continue;

            var cost = thruster.Type == ThrusterType.Angular
                ? computer.GyroscopeProcessingPowerCost
                : computer.ThrusterProcessingPowerCost;
            if (availablePower >= cost)
            {
                availablePower -= cost;
                if (thruster.Enabled && !thruster.IsOn)
                    _thrusters.EnableThruster(uid, thruster, transform);
            }
            else if (thruster.IsOn)
            {
                _thrusters.DisableThruster(uid, thruster, grid, transform);
            }
        }
    }
}
