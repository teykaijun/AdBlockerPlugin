using System.Runtime.InteropServices;

// Windows DLLs come from System32 only, never from the folder adblocker.exe was started from
// (Downloads, say), where another program could have put a DLL of the same name.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AdBlocker.Platform;

internal static class NativeMethods
{
    public const uint DnsInterfaceSettingsVersion1 = 1;
    public const ulong DnsSettingIPv6 = 0x0001;
    public const ulong DnsSettingNameServer = 0x0002;

    /// <summary>DNS_INTERFACE_SETTINGS (netioapi.h), Windows 10 version 2004 and later.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain;
        public IntPtr NameServer;
        public IntPtr SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        public IntPtr ProfileNameServer;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern uint SetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern uint GetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern void FreeInterfaceDnsSettings(ref DnsInterfaceSettings settings);

    /// <summary>Same as <c>ipconfig /flushdns</c>.</summary>
    [DllImport("dnsapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DnsFlushResolverCache();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    public const uint MovefileDelayUntilReboot = 0x4;

    /// <summary>With a null destination and <see cref="MovefileDelayUntilReboot"/>, deletes the file at the next restart.</summary>
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
}
