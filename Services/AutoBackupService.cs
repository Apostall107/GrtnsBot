using System.IO.Compression;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Automatic copies of the bot data (data/*.json → data/backups/auto-*.zip): one shortly after every start
/// (the last 5 are kept) and one a day (the last 14 are kept). If something gets lost, there is always a copy
/// from before — downloadable in Settings → Backup.
/// </summary>
public sealed class AutoBackupService(IConfiguration config, DiscordSocketClient client, AuditLog audit, ILogger<AutoBackupService> logger) : BackgroundService
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
            CheckShrink(dir);
            File.WriteAllBytes(Path.Combine(dir, $"auto-{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"), Zip(DataDir(config)));
            foreach (var old in new DirectoryInfo(dir).GetFiles($"auto-{kind}-*.zip").OrderByDescending(f => f.Name).Skip(keep))
                old.Delete();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic backup ({Kind}) failed", kind);
        }
    }

    /// <summary>
    /// Before a new backup: is a data file much smaller than in the newest backup (or gone)? Then something may have
    /// wiped it — the Log of every server says so, and that backup is copied to keep-*.zip so it never rotates away.
    /// Compares file sizes only (milliseconds, once at start and once a day).
    /// </summary>
    private void CheckShrink(string dir)
    {
        var newest = new DirectoryInfo(dir).GetFiles("auto-*.zip").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return;
        var dataDir = DataDir(config);
        var problems = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(newest.FullName);
            foreach (var entry in zip.Entries)
            {
                if (entry.Length < 4096 || entry.FullName.Contains("audit", StringComparison.OrdinalIgnoreCase)) continue;
                var file = new FileInfo(Path.Combine(dataDir, entry.FullName));
                if (!file.Exists) problems.Add($"{entry.FullName} is gone (was {entry.Length / 1024} KB)");
                else if (file.Length < entry.Length * .4) problems.Add($"{entry.FullName} shrank from {entry.Length / 1024} KB to {file.Length / 1024} KB");
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not compare data with backup {Backup}", newest.Name); return; }
        if (problems.Count == 0) return;
        var keep = Path.Combine(dir, $"keep-{newest.Name[5..]}");
        try { if (!File.Exists(keep)) File.Copy(newest.FullName, keep); } catch { /* best effort */ }
        var text = $"{string.Join("; ", problems)}. If that wasn't on purpose, restore from {Path.GetFileName(keep)} (Settings → Backup).";
        logger.LogCritical("Data shrank since the last backup: {Text}", text);
        _ = Task.Run(async () =>
        {
            // servers may not be loaded yet right after a start
            for (var i = 0; i < 60 && client.Guilds.Count == 0; i++) await Task.Delay(2000);
            foreach (var guild in client.Guilds) audit.FromSystem(guild.Id, "safety", "Data much smaller than in the last backup", text);
        });
    }
}
