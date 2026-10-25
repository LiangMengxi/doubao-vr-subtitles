using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DoubaoVR.Desktop;

internal static class SteamVrAccess
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, TokenIntegrityBuffer buffer, uint length, out uint required);

    internal static uint? ReadIntegrity(int processId)
    {
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) return null;
        if (!OpenProcessToken(process, 8, out var token)) { token?.Dispose(); return null; }
        using (token)
        using (var buffer = new TokenIntegrityBuffer())
            return GetTokenInformation(token, 25, buffer, TokenIntegrityBuffer.Capacity, out uint required)
                && required >= (uint)IntPtr.Size + 4 && required <= TokenIntegrityBuffer.Capacity ? buffer.Integrity() : null;
    }

    internal static void EnsureCompatible(uint? callerIntegrity, uint? serverIntegrity)
    {
        if (callerIntegrity < 12288 && serverIntegrity >= 12288)
            throw new InvalidOperationException("SteamVR 以较高权限运行，当前程序权限较低，无法连接。请退出本程序，再以管理员身份启动本程序后重试。");
    }

    internal static void EnsureCompatible()
    {
        uint? integrity = ReadIntegrity(Environment.ProcessId);
        if (!integrity.HasValue || integrity >= 12288) return;
        using var current = Process.GetCurrentProcess();
        var servers = Process.GetProcessesByName("vrserver");
        try
        {
            foreach (var server in servers)
            {
                try
                {
                    if (server.SessionId == current.SessionId) EnsureCompatible(integrity, ReadIntegrity(server.Id));
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) when (server.HasExited) { }
            }
        }
        finally { foreach (var server in servers) server.Dispose(); }
    }
}
