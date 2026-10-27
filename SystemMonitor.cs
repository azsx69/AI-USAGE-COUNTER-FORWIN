using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace AiUsageCounter;

// อ่าน CPU% / RAM% ผ่าน Win32 API (ไม่ต้องใช้ admin) และอุณหภูมิ CPU ผ่าน LibreHardwareMonitor
// อุณหภูมิอ่านได้เฉพาะตอนรันแบบ Administrator และเครื่องติดตั้ง driver PawnIO แล้ว
public sealed class SystemMonitor : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private const double Gb = 1024.0 * 1024 * 1024;
    private const string PawnIoUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    private static readonly bool IsAdmin =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private Computer? _computer;
    private (long Idle, long Kernel, long User, DateTime At)? _lastCpu;

    public async Task<SystemUsage> ReadAsync()
    {
        // baseline เก่าเกิน (popup เพิ่งเปิด) → เก็บ sample ใหม่แล้วรอสั้น ๆ ให้ได้ CPU% ของตอนนี้
        if (_lastCpu is not { } prev || DateTime.UtcNow - prev.At > TimeSpan.FromSeconds(5))
        {
            _lastCpu = SampleCpu();
            await Task.Delay(250);
        }
        // LibreHardwareMonitor เปิดครั้งแรกช้า จึงอ่านนอก UI thread
        return await Task.Run(Read);
    }

    private SystemUsage Read()
    {
        var u = new SystemUsage();

        var cur = SampleCpu();
        if (_lastCpu is { } prev)
            u.CpuPct = CpuPercent(cur.Idle - prev.Idle, cur.Kernel - prev.Kernel, cur.User - prev.User);
        _lastCpu = cur;

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem) && mem.TotalPhys > 0)
        {
            u.RamTotalGb = mem.TotalPhys / Gb;
            u.RamUsedGb = (mem.TotalPhys - mem.AvailPhys) / Gb;
            u.RamPct = u.RamUsedGb / u.RamTotalGb * 100;
        }

        ReadTemp(u);
        return u;
    }

    private void ReadTemp(SystemUsage u)
    {
        // ไม่ใช่ admin → driver ไม่ยอมให้อ่าน ไม่ต้องโหลด LibreHardwareMonitor ให้เปลืองแรม
        if (!IsAdmin) { u.TempNote = "Run as administrator to read"; return; }

        try
        {
            if (_computer is null)
            {
                _computer = new Computer { IsCpuEnabled = true };
                _computer.Open();
            }
            var temps = new List<(string Name, float? Value)>();
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                temps.AddRange(hw.Sensors
                    .Where(s => s.SensorType == SensorType.Temperature)
                    .Select(s => (s.Name, s.Value)));
            }
            u.TempC = PickCpuTemp(temps);
        }
        catch { /* อ่านเซ็นเซอร์ไม่ได้ก็ปล่อยเป็นค่าว่าง */ }

        if (u.TempC is null)
        {
            using var pawnIo = Registry.LocalMachine.OpenSubKey(PawnIoUninstallKey);
            u.TempNote = pawnIo is null ? "PawnIO driver not installed" : "No CPU temperature sensor";
        }
    }

    private static (long Idle, long Kernel, long User, DateTime At) SampleCpu()
    {
        GetSystemTimes(out long idle, out long kernel, out long user);
        return (idle, kernel, user, DateTime.UtcNow);
    }

    // kernel time ของ Windows รวม idle ไว้แล้ว → เวลาที่ CPU ทำงาน = kernel + user - idle
    internal static double? CpuPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        long total = kernelDelta + userDelta;
        if (total <= 0) return null;
        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    // เลือก sensor "Package" (Intel) / "Tctl" (AMD) ก่อน ถ้าไม่มีใช้ค่าที่ร้อนที่สุด
    internal static double? PickCpuTemp(IEnumerable<(string Name, float? Value)> temps)
    {
        var valid = temps.Where(t => t.Value is > 0).ToList();
        if (valid.Count == 0) return null;
        var main = valid.FirstOrDefault(t => t.Name.Contains("Package") || t.Name.Contains("Tctl"));
        return main.Name is not null ? main.Value : valid.Max(t => t.Value);
    }

    public void Dispose() => _computer?.Close();
}
