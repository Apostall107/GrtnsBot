using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Automatic copies of the bot data (data/*.json → data/backups/auto-*.zip): one shortly after every start
/// (the last 5 are kept) and one a day (the last 14 are kept). If something gets lost, there is always a copy
/// from before — downloadable in Settings → Backup.
/// </summary>
public sealed class AutoBackupService(IConfiguration config, ILogger<AutoBackupService> logger) : BackgroundService
{
    /// <summary>Folders that never go into a backup (site login keys, the backups themselves).</summary>
    public static readonly string[] SkipDirs = ["web-keys", "backups"];

    public static string DataDir(IConfiguration config) =>
        Path.GetFullPath(Path.GetDirectoryName(config["Storage:ActivitiesPath"] ?? "data/activities.json") is { Length: > 0 } d ? d : "data");

    public static string BackupDir(IConfiguration config) => Path.Combine(DataDir(config), "backups");

    public static byte[] Zip(string dataDir)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (Directory.Exists(dataDir))
                foreach (var file in Directory.EnumerateFiles(dataDir, "*.json", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                    if (SkipDirs.Any(d => relative.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))) continue;
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
        }
        return ms.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            // Before the bot touches anything after a start — but after the data has been loaded
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
            Save("start", 5);
            while (!stop.IsCancellationRequested)
            {
                var dir = BackupDir(config);
                var today = $"auto-day-{DateTime.UtcNow:yyyyMMdd}";
                if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, today + "*.zip").Any()) Save("day", 14);
                await Task.Delay(TimeSpan.FromHours(1), stop);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private void Save(string kind, int keep)
    {
        try
        {
            DataFiles.FlushAll();
            var dir = BackupDir(config);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"auto-{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"), Zip(DataDir(config)));
            foreach (var old in new DirectoryInfo(dir).GetFiles($"auto-{kind}-*.zip").OrderByDescending(f => f.Name).Skip(keep))
                old.Delete();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic backup ({Kind}) failed", kind);
        }
    }
}
