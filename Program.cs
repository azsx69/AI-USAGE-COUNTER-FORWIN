using Velopack;

namespace AiUsageCounter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Velopack hooks: ต้องรันก่อนสุด เพื่อจัดการ install/update/uninstall แล้ว exit เอง
        VelopackApp.Build().Run();

        // Single instance guard.
        using var mutex = new Mutex(true, "AiUsageCounter_SingleInstance", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());

        GC.KeepAlive(mutex);
    }
}
