using Content.Shared._Forge.ShipComputers;
using Content.Shared.DeviceLinking.Events;
using Content.Server.DeviceLinking.Systems;
using Content.Shared.DeviceLinking;
using Robust.Server.GameObjects;

namespace Content.Server._Forge.ShipComputers;

public sealed class ShipComputerConsoleSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly DeviceLinkSystem _deviceLink = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShipComputerConsoleComponent, LinkAttemptEvent>(OnLinkAttempt);
        SubscribeLocalEvent<ShipComputerConsoleComponent, NewLinkEvent>(OnNewLink);
        SubscribeLocalEvent<ShipComputerConsoleComponent, PortDisconnectedEvent>(OnPortDisconnected);
        SubscribeLocalEvent<ShipComputerConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ShipComputerConsoleComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ShipComputerConsoleComponent, ComponentInit>(OnInit);
    }

    private void OnInit(Entity<ShipComputerConsoleComponent> ent, ref ComponentInit args)
    {
        _deviceLink.EnsureSourcePorts(ent, ent.Comp.ServerSourcePort);
    }

    private void OnLinkAttempt(Entity<ShipComputerConsoleComponent> ent, ref LinkAttemptEvent args)
    {
        if (args.Source != ent.Owner)
            return;

        if (!HasComp<ShipComputerComponent>(args.Sink))
            args.Cancelled = true;
    }

    private void OnNewLink(Entity<ShipComputerConsoleComponent> ent, ref NewLinkEvent args)
    {
        if (args.Source != ent.Owner || !HasComp<ShipComputerComponent>(args.Sink))
            return;

        ent.Comp.Server = args.Sink;
        UpdateUi(ent);
    }

    private void OnPortDisconnected(Entity<ShipComputerConsoleComponent> ent, ref PortDisconnectedEvent args)
    {
        ent.Comp.Server = null;
        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<ShipComputerConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnShutdown(Entity<ShipComputerConsoleComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Server = null;
    }

    public void UpdateUi(Entity<ShipComputerConsoleComponent> ent)
    {
        var state = new ShipComputerConsoleState();
        if (ent.Comp.Server is { } server && TryComp<ShipComputerComponent>(server, out var computer))
        {
            state.Connected = true;
            state.ProcessingPower = computer.ProcessingPower;
            state.UsedProcessingPower = computer.UsedProcessingPower;
            state.PowerSupplyCapacity = computer.PowerSupplyCapacity;
            if (TryComp<Content.Server.Power.Components.ApcPowerReceiverComponent>(server, out var receiver))
                state.PowerLoad = receiver.Load;

            foreach (var module in computer.Modules.ContainedEntities)
            {
                if (!TryComp<ShipComputerModuleComponent>(module, out var moduleComp))
                    continue;

                state.Modules.Add(new ShipComputerModuleEntry
                {
                    Name = Name(module),
                });
            }
        }

        _ui.SetUiState(ent.Owner, ShipComputerConsoleUiKey.Key, state);
    }
}
