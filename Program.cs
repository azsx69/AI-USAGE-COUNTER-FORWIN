namespace AiUsageCounter;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance guard.
        using var mutex = new Mutex(true, "AiUsageCounter_SingleInstance", out bool created);
        if (!created) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());

        GC.KeepAlive(mutex);
    }
}
