using System.IO.Ports;
using System.Runtime.InteropServices;

namespace AaoDcsBiosRuntimeBridge;

public static class PortLister
{
    public static void PrintPorts()
    {
        Console.WriteLine("Available serial ports:");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var p in SerialPort.GetPortNames().OrderBy(s => s))
                Console.WriteLine($"  {p}");
            return;
        }

        // Linux/macOS: SerialPort.GetPortNames can be unreliable depending on distro.
        // We list typical device nodes as well.
        var candidates = new List<string>();

        try
        {
            candidates.AddRange(SerialPort.GetPortNames());
        }
        catch { }

        void AddGlob(string pattern)
        {
            try
            {
                var dir = Path.GetDirectoryName(pattern);
                if (string.IsNullOrEmpty(dir)) return;
                var filepat = Path.GetFileName(pattern);
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, filepat))
                    candidates.Add(f);
            }
            catch { }
        }

        AddGlob("/dev/ttyACM*");
        AddGlob("/dev/ttyUSB*");
        AddGlob("/dev/tty.*");     // macOS
        AddGlob("/dev/cu.*");      // macOS

        // by-id is the nicest stable naming on Linux
        try
        {
            if (Directory.Exists("/dev/serial/by-id"))
            {
                foreach (var f in Directory.GetFiles("/dev/serial/by-id"))
                    candidates.Add(f);
            }
        }
        catch { }

        foreach (var p in candidates.Distinct().OrderBy(s => s))
            Console.WriteLine($"  {p}");

        Console.WriteLine();
        Console.WriteLine("Linux tip: user needs access to serial devices (usually group 'dialout').");
        Console.WriteLine("  sudo usermod -aG dialout $USER");
    }
}