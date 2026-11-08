using LibreHardwareMonitor.Hardware;

async Task Probe(bool onPool)
{
    await Task.Yield();
    var c = new Computer
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = false,
        IsMotherboardEnabled = false,
        IsStorageEnabled = false,
        IsNetworkEnabled = false
    };
    try { c.Open(); }
    catch (Exception ex) { Console.WriteLine($"[{onPool}] Open FAILED {ex.Message}"); return; }

    Console.WriteLine($"[{onPool}] hw count = {c.Hardware.Count}, thread={Environment.CurrentManagedThreadId}");
    foreach (var hw in c.Hardware) Console.WriteLine($"   {hw.HardwareType} {hw.Name}");
    c.Close();
}

await Probe(false);
await Task.Run(() => Probe(true));
