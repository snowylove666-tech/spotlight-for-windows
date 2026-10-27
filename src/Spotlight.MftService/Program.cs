using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Spotlight.Core.Indexing;

namespace Spotlight.MftService;

/// <summary>
/// SpotlightWin MFT service: reads an NTFS volume's master file table for the app, which can't (it
/// needs administrator rights). It does one thing: given a drive letter, stream that drive's file
/// names, sizes, times and attributes over a local pipe. Started on demand by the app, it stops by
/// itself after two idle minutes.
///
///   SpotlightWin.MftService.exe install     (as administrator) copy to Program Files and register
///   SpotlightWin.MftService.exe uninstall   (as administrator) stop and remove
///   SpotlightWin.MftService.exe scan C:\    (as administrator) read a drive here, print the time it took
///   (no arguments)                          run as the service, started by Windows
/// </summary>
internal static class Program
{
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(2);

    private static int Main(string[] args)
    {
        try
        {
            switch (args.FirstOrDefault()?.ToLowerInvariant())
            {
                case "install": return Installer.Install();
                case "uninstall": return Installer.Uninstall();
                case "scan": return ScanHere(args.Length > 1 ? args[1] : "C:\\");
                default: return ServiceHost.Run(Serve);
            }
        }
        catch (Exception e)
        {
            Log(e.ToString());
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int ScanHere(string volume)
    {
        var sw = Stopwatch.StartNew();
        long n = 0, dirs = 0;
        MftReader.Read(volume, e => { n++; if ((e.Attributes & 0x10) != 0) dirs++; });
        Console.WriteLine($"{volume}: {n:N0} names ({dirs:N0} folders) in {sw.Elapsed.TotalSeconds:F2} s");
        return 0;
    }

    /// <summary>Answers requests one at a time until <paramref name="stop"/> is set or nobody asked for a while.</summary>
    private static void Serve(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(MftProtocol.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20, PipeAccess());
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
            idle.CancelAfter(IdleExit);
            try { pipe.WaitForConnectionAsync(idle.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; } // stopped, or idle: exit until the app starts it again
            try { Answer(pipe); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // the app went away
            catch (Exception e) { Log(e.ToString()); }
        }
    }

    private static void Answer(NamedPipeServerStream pipe)
    {
        string request = ReadLine(pipe, 64);
        var w = new BinaryWriter(new BufferedStream(pipe, 1 << 20), Encoding.UTF8, leaveOpen: true);
        var parts = request.Split(' ');
        if (parts.Length != 4 || parts[0] != "SWMFT" || parts[1] != MftProtocol.Version.ToString() || parts[2] != "SCAN")
        {
            Fail(w, 87, "unsupported request: protocol " + MftProtocol.Version);
            return;
        }
        // Only a whole local NTFS drive, named by its letter: nothing else is accepted.
        string volume = parts[3];
        if (volume.Length != 3 || !char.IsAsciiLetter(volume[0]) || volume[1] != ':' || volume[2] != '\\')
        {
            Fail(w, 87, "not a drive: " + volume);
            return;
        }
        var drive = new DriveInfo(volume);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
        {
            Fail(w, 50, "not a local NTFS drive: " + volume);
            return;
        }
        bool started = false;
        try
        {
            MftReader.Read(volume, e =>
            {
                if (!started) { w.Write(0); started = true; }
                MftProtocol.WriteEntry(w, e);
            });
        }
        catch (Exception e) when (!started)
        {
            Fail(w, e is System.ComponentModel.Win32Exception we ? we.NativeErrorCode : 1, e.Message);
            return;
        }
        if (!started) w.Write(0);
        MftProtocol.WriteEnd(w);
        w.Flush();
        pipe.WaitForPipeDrain();
    }

    private static void Fail(BinaryWriter w, int code, string message)
    {
        w.Write(code);
        w.Write(message);
        w.Flush();
    }

    private static string ReadLine(Stream s, int max)
    {
        var sb = new StringBuilder();
        while (sb.Length < max)
        {
            int b = s.ReadByte();
            if (b is -1 or '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>The signed-in user's programs may connect; nothing from the network.</summary>
    private static PipeSecurity PipeAccess()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return ps;
    }

    internal static void Log(string text)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpotlightWin");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "mft-service.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}

/// <summary>Registers with the service control manager and reports state (plain Win32: no packages).</summary>
internal static class ServiceHost
{
    private const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const int SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    private const int SERVICE_ACCEPT_STOP = 1, SERVICE_ACCEPT_SHUTDOWN = 4;
    private const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_SHUTDOWN = 5, SERVICE_CONTROL_INTERROGATE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public ServiceMainProc? lpServiceProc;
    }

    private delegate void ServiceMainProc(int argc, IntPtr argv);
    private delegate int HandlerProc(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerProc handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    // Kept in fields: the service control manager calls them after Run's locals would be collected.
    private static ServiceMainProc? _main;
    private static HandlerProc? _handler;
    private static IntPtr _status;
    private static readonly CancellationTokenSource Stop = new();

    public static int Run(Action<CancellationToken> work)
    {
        _main = (_, _) =>
        {
            _handler = (control, _, _, _) =>
            {
                if (control is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
                {
                    Report(SERVICE_STOP_PENDING);
                    Stop.Cancel();
                }
                return 0;
            };
            _status = RegisterServiceCtrlHandlerEx(MftProtocol.ServiceName, _handler, IntPtr.Zero);
            Report(SERVICE_RUNNING);
            int exit = 0;
            try { work(Stop.Token); }
            catch (Exception e) { Program.Log(e.ToString()); exit = 1; }
            Report(SERVICE_STOPPED, exit);
        };
        var table = new[] { new SERVICE_TABLE_ENTRY { lpServiceName = MftProtocol.ServiceName, lpServiceProc = _main }, new SERVICE_TABLE_ENTRY() };
        if (!StartServiceCtrlDispatcher(table))
        {
            Console.Error.WriteLine("This is the SpotlightWin MFT service; run it with \"install\" as administrator to register it.");
            return Marshal.GetLastWin32Error();
        }
        return 0;
    }

    private static void Report(int state, int exit = 0)
    {
        var s = new SERVICE_STATUS
        {
            dwServiceType = SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = state,
            dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0,
            dwWin32ExitCode = exit,
            dwWaitHint = state is SERVICE_START_PENDING or SERVICE_STOP_PENDING ? 5000 : 0,
        };
        SetServiceStatus(_status, ref s);
    }
}

/// <summary>"install" / "uninstall", run elevated by the app's settings window.</summary>
internal static class Installer
{
    private const string ExeName = "SpotlightWin.MftService.exe";

    // Program Files: only administrators can replace the exe a SYSTEM service runs.
    private static string TargetDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SpotlightWin MFT Service");

    // Default service rights, plus: signed-in users may start it (RP), so the app can start it on demand.
    private const string Sddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private static bool Elevated() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static int Install()
    {
        if (!Elevated()) { Console.Error.WriteLine("需要管理员权限"); return 5; }
        string target = Path.Combine(TargetDir, ExeName);
        bool exists = Sc("query", MftProtocol.ServiceName) == 0;
        if (exists) StopService();
        Directory.CreateDirectory(TargetDir);
        string self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(self, target);
        string bin = $"\"{target}\"";
        int rc = exists
            ? Sc("config", MftProtocol.ServiceName, "binPath=", bin, "start=", "demand")
            : Sc("create", MftProtocol.ServiceName, "binPath=", bin, "start=", "demand", "DisplayName=", "SpotlightWin MFT 读取服务");
        if (rc != 0) return rc;
        Sc("description", MftProtocol.ServiceName, "为聚焦搜索（SpotlightWin）读取 NTFS 主文件表，让建索引和重新扫描在几秒内完成。只在需要时运行，空闲两分钟后自动停止。");
        rc = Sc("sdset", MftProtocol.ServiceName, Sddl);
        Program.Log($"installed {target} (rc {rc})");
        return rc;
    }

    public static int Uninstall()
    {
        if (!Elevated()) { Console.Error.WriteLine("需要管理员权限"); return 5; }
        StopService();
        int rc = Sc("delete", MftProtocol.ServiceName);
        try { Directory.Delete(TargetDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Program.Log($"uninstalled (rc {rc})");
        return rc is 0 or 1060 /* not installed */ ? 0 : rc;
    }

    private static void StopService()
    {
        Sc("stop", MftProtocol.ServiceName);
        for (int i = 0; i < 40; i++)
        {
            var p = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName))
                .Where(x => x.Id != Environment.ProcessId).ToList();
            if (p.Count == 0 || p.All(x => { try { return !x.MainModule!.FileName.StartsWith(TargetDir, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; } }))
                return;
            Thread.Sleep(250);
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, overwrite: true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(250); }
        }
    }

    private static int Sc(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args[0] != "query" && args[0] != "stop") Program.Log($"sc {string.Join(' ', args)}: {p.ExitCode} {output.Trim()}");
        return p.ExitCode;
    }
}
