using System.Reflection;

namespace AbsPlayer.Core;

public static class AppConfig
{
    public const string ClientName = "Homeshelf Windows";
    public const string DeviceModel = "Windows PC";

    public static readonly string Version =
        (Assembly.GetEntryAssembly() ?? typeof(AppConfig).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0";

    /// <summary>%LOCALAPPDATA%\ABSPlayer — settings, local progress and downloads. Named from before the rename to Homeshelf; kept so nothing is lost.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ABSPlayer");
}
