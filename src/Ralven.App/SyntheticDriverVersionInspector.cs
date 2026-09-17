using Ralven.Windows.Infrastructure;

namespace Ralven.App;

/// <summary>Fixed, non-identifying driver snapshot for demo runs.</summary>
internal sealed class SyntheticDriverVersionInspector : IDriverVersionInspector
{
    public DriverVersionSnapshot GetSnapshot() => new(
        Video: [new("Demo Graphics Adapter", "1.0.0.0")],
        Network: [], Audio: [], Chipset: [], Storage: [], Usb: [], Bluetooth: []);
}
