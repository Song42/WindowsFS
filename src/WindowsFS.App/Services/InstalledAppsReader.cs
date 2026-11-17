using System.IO;
using Microsoft.Win32;
using WindowsFS.Core.Cleanup;

namespace WindowsFS.App.Services;

public interface ICleanupContextReader
{
    Task<CleanupContext> ReadAsync(CancellationToken token);
}

/// <summary>Reads registration metadata only. Never queries Win32_Product or reads/executes uninstall commands.</summary>
public sealed class InstalledAppsReader : ICleanupContextReader
{
    public Task<CleanupContext> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        var apps = new List<InstalledApp>();
        var incomplete = false;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: false);
                if (uninstall is null) continue;
                var keyNames = uninstall.GetSubKeyNames();
                if (keyNames.Length > 20000) incomplete = true;
                foreach (var keyName in keyNames.Take(20000))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName, writable: false);
                        if (key is null) continue;
                        var name = key.GetValue("DisplayName") as string;
                        var location = key.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location) || name.Length > 512 || location.Length > 32767) continue;
                        location = location.Trim().Trim('"');
                        if (!Path.IsPathFullyQualified(location) || location.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                        var publisher = key.GetValue("Publisher") as string ?? "";
                        apps.Add(new(name.Trim(), Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), publisher[..Math.Min(publisher.Length, 512)]));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                    { incomplete = true; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { incomplete = true; }
        }
        return new CleanupContext(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct().ToArray(),
            apps, incomplete ? "설치 등록 정보 일부 접근 불가" : "설치 등록 정보는 현재 시점 기준");
    }, token);
}
