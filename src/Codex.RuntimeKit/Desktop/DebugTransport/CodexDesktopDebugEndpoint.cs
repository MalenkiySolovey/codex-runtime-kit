using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Codex.RuntimeKit.Desktop.Lifecycle;

namespace Codex.RuntimeKit.Desktop.DebugTransport;

public sealed record CodexDesktopDebugEndpoint
{
    private CodexDesktopDebugEndpoint(int processId, int port, Uri baseAddress, Uri browserWebSocketAddress)
    {
        ProcessId = processId;
        Port = port;
        BaseAddress = baseAddress;
        BrowserWebSocketAddress = browserWebSocketAddress;
    }

    public int ProcessId { get; }
    public int Port { get; }
    public Uri BaseAddress { get; }
    public Uri BrowserWebSocketAddress { get; }

    /// <summary>Verifies the listener and the visible window of the exact managed Desktop process.</summary>
    public static bool TryReadManagedProfile(
        string profileDirectory,
        string expectedExecutablePath,
        int processId,
        DateTimeOffset startedAt,
        ICodexDesktopListenerOwnerResolver ownerResolver,
        out CodexDesktopDebugEndpoint? endpoint)
    {
        endpoint = null;
        if (!TryReadManagedProfile(profileDirectory, expectedExecutablePath, ownerResolver, out var candidate, out _) ||
            candidate?.ProcessId != processId)
        {
            return false;
        }

        using var process = Process.GetProcessById(processId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != startedAt.UtcDateTime ||
            process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        endpoint = candidate;
        return true;
    }

    public static bool TryReadManagedProfile(
        string profileDirectory,
        CodexDesktopManagedSession session,
        ICodexDesktopListenerOwnerResolver ownerResolver,
        out CodexDesktopDebugEndpoint? endpoint,
        out string diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ownerResolver);
        if (session.ProcessId <= 0)
        {
            endpoint = null;
            diagnosticCode = "desktop.cdp.process-id-invalid";
            return false;
        }

        if (!session.IsRunning)
        {
            endpoint = null;
            diagnosticCode = "desktop.cdp.process-not-running";
            return false;
        }

        if (!string.Equals(
                Path.GetFullPath(profileDirectory),
                Path.GetFullPath(session.ProfileDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            endpoint = null;
            diagnosticCode = "desktop.cdp.profile-mismatch";
            return false;
        }

        if (!TryReadManagedProfile(
                profileDirectory,
                session.ExecutablePath,
                ownerResolver,
                out endpoint,
                out diagnosticCode))
        {
            return false;
        }

        if (endpoint!.ProcessId != session.ProcessId)
        {
            endpoint = null;
            diagnosticCode = "desktop.cdp.owner-mismatch";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies the loopback DevTools endpoint recorded in a specific renderer profile.
    /// The listener owner must be the expected official executable image.
    /// </summary>
    public static bool TryReadManagedProfile(
        string profileDirectory,
        string expectedExecutablePath,
        ICodexDesktopListenerOwnerResolver ownerResolver,
        out CodexDesktopDebugEndpoint? endpoint,
        out string diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(ownerResolver);
        endpoint = null;
        if (string.IsNullOrWhiteSpace(expectedExecutablePath) || !Path.IsPathFullyQualified(expectedExecutablePath))
        {
            diagnosticCode = "desktop.cdp.executable-invalid";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profileDirectory) || !Path.IsPathFullyQualified(profileDirectory))
        {
            diagnosticCode = "desktop.cdp.profile-invalid";
            return false;
        }

        try
        {
            var fullProfilePath = Path.GetFullPath(profileDirectory);
            var activePortPath = Path.Combine(fullProfilePath, "DevToolsActivePort");
            var file = new FileInfo(activePortPath);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > 256)
            {
                diagnosticCode = "desktop.cdp.active-port-unavailable";
                return false;
            }

            var lines = File.ReadAllLines(activePortPath);
            if (lines.Length < 2 ||
                !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 ||
                !Regex.IsMatch(lines[1], "^/devtools/browser/[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant))
            {
                diagnosticCode = "desktop.cdp.active-port-invalid";
                return false;
            }

            var ownerProcessId = ownerResolver.GetLoopbackListenerOwnerProcessId(port);
            if (ownerProcessId is null or <= 0)
            {
                diagnosticCode = "desktop.cdp.owner-mismatch";
                return false;
            }

            if (!ownerResolver.ProcessExecutableMatches(ownerProcessId.Value, expectedExecutablePath))
            {
                diagnosticCode = "desktop.cdp.executable-mismatch";
                return false;
            }

            var baseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
            var browserAddress = new Uri($"ws://127.0.0.1:{port}{lines[1]}", UriKind.Absolute);
            endpoint = new CodexDesktopDebugEndpoint(ownerProcessId.Value, port, baseAddress, browserAddress);
            diagnosticCode = "desktop.cdp.endpoint-verified";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            diagnosticCode = "desktop.cdp.profile-access-denied";
            return false;
        }
        catch (IOException)
        {
            diagnosticCode = "desktop.cdp.active-port-unavailable";
            return false;
        }
        catch (UriFormatException)
        {
            diagnosticCode = "desktop.cdp.active-port-invalid";
            return false;
        }
    }

    private static bool AreSameExecutablePath(string? actualPath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath) || !Path.IsPathFullyQualified(actualPath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

public interface ICodexDesktopListenerOwnerResolver
{
    int? GetLoopbackListenerOwnerProcessId(int port);

    bool ProcessExecutableMatches(int processId, string expectedExecutablePath);
}

/// <summary>Resolves the local PID that owns an IPv4 TCP listener on Windows.</summary>
public sealed class WindowsCodexDesktopListenerOwnerResolver : ICodexDesktopListenerOwnerResolver
{
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidListener = 3;
    private const uint TcpStateListen = 2;

    public int? GetLoopbackListenerOwnerProcessId(int port)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535)
        {
            return null;
        }

        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, TcpTableOwnerPidListener, 0);
        if (status != ErrorInsufficientBuffer || size <= 0 || size > 16 * 1024 * 1024)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, true, 2, TcpTableOwnerPidListener, 0);
            if (status != 0)
            {
                return null;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var targetPort = unchecked((uint)(ushort)System.Net.IPAddress.HostToNetworkOrder((short)port));
            var loopbackAddress = BitConverter.ToUInt32(System.Net.IPAddress.Loopback.GetAddressBytes(), 0);
            for (var index = 0; index < count; index++)
            {
                var rowPointer = IntPtr.Add(buffer, sizeof(int) + index * rowSize);
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                if (row.State == TcpStateListen && row.LocalAddress == loopbackAddress && row.LocalPort == targetPort)
                {
                    return unchecked((int)row.OwningProcessId);
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool ProcessExecutableMatches(int processId, string expectedExecutablePath)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 ||
            string.IsNullOrWhiteSpace(expectedExecutablePath) || !Path.IsPathFullyQualified(expectedExecutablePath))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var actualExecutablePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actualExecutablePath) &&
                   string.Equals(
                       Path.GetFullPath(actualExecutablePath),
                       Path.GetFullPath(expectedExecutablePath),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException or Win32Exception or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
