using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DeviceLinking;

namespace Content.Shared._Forge.ShipComputers;

[RegisterComponent]
public sealed partial class ShipComputerComponent : Component
{
    [DataField]
    public ProtoId<SinkPortPrototype> ConsoleSinkPort;

    [DataField]
    public List<EntProtoId> DefaultModules = new();
    public const string ModulesContainerId = "ship_computer_modules";

    public const string ProcessorSlotId = "ship_computer_processor";
    public const string PowerSupplySlotId = "ship_computer_power_supply";

    [DataField]
    public ItemSlot ProcessorSlot = new();

    [DataField]
    public ItemSlot PowerSupplySlot = new();

    [DataField]
    public float BasePowerLoad;

    [DataField]
    public float ProcessingPower;

    [ViewVariables]
    public float UsedProcessingPower;

    [DataField]
    public float PowerSupplyCapacity;

    [DataField]
    public float ThrusterProcessingPowerCost;

    [DataField]
    public float GyroscopeProcessingPowerCost;

    [DataField]
    public float WeaponProcessingPowerCost;

    [ViewVariables]
    public Container Modules = default!;

}

[RegisterComponent]
public sealed partial class ShipComputerPartComponent : Component
{
    [DataField]
    public float ProcessingPower;

    [DataField]
    public float PowerSupplyCapacity;

    [DataField]
    public float PowerLoad;
}

[RegisterComponent]
public sealed partial class ShipComputerProcessorComponent : Component;

[RegisterComponent]
public sealed partial class ShipComputerPowerSupplyComponent : Component;

[RegisterComponent]
public sealed partial class ShipComputerModuleComponent : Component
{
    [DataField]
    public float ProcessingPowerCost;

    [DataField]
    public float PowerLoad;

    [DataField]
    public bool Enabled = true;

    [DataField]
    public bool ProvidesFireControl;

    [DataField]
    public string? UserInterfaceKey;

    [DataField]
    public ComponentRegistry AddToServer = new();

    [DataField]
    public ComponentRegistry AddToGrid = new();
}

[RegisterComponent]
public sealed partial class ShipWeaponProcessingCostComponent : Component
{
    [DataField]
    public float Cost = 1f;
}
