using Robust.Shared.Serialization;
using Robust.Shared.Prototypes;
using Content.Shared.DeviceLinking;

namespace Content.Shared._Forge.ShipComputers;

[RegisterComponent]
public sealed partial class ShipComputerConsoleComponent : Component
{
    [DataField]
    public ProtoId<SourcePortPrototype> ServerSourcePort;

    [ViewVariables]
    public EntityUid? Server;
}

[Serializable, NetSerializable]
public enum ShipComputerConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class ShipComputerConsoleState : BoundUserInterfaceState
{
    public bool Connected;
    public float ProcessingPower;
    public float UsedProcessingPower;
    public float PowerSupplyCapacity;
    public float PowerLoad;
    public List<ShipComputerModuleEntry> Modules = new();
}

[Serializable, NetSerializable]
public sealed class ShipComputerModuleEntry
{
    public string Name = string.Empty;
}
