using Content.Shared._Forge.ShipComputers;
using JetBrains.Annotations;

namespace Content.Client._Forge.ShipComputers;

[UsedImplicitly]
public sealed class ShipComputerConsoleBoundUserInterface : BoundUserInterface
{
    private ShipComputerConsoleWindow? _window;

    public ShipComputerConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = new ShipComputerConsoleWindow();
        _window.OnClose += Close;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is ShipComputerConsoleState consoleState)
            _window?.UpdateState(consoleState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
