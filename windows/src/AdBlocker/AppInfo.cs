namespace AdBlocker;

internal static class AppInfo
{
    public const string SourceUrl = "https://github.com/teykaijun/AdBlockerPlugin";
    public const string SupportUrl = "https://buymeacoffee.com/casunoxd";

    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
