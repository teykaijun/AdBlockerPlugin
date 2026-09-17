using System.Runtime.InteropServices;

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
}
