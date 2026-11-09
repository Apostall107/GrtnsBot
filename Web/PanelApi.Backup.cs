using System.IO.Compression;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Web;

// Резервная копия данных бота (только владелец): скачать zip папки data/ и восстановить из zip.
public static partial class PanelApi
{
    /// <summary>Ключи входа на сайт (web-keys) и старые копии в архив не попадают и при восстановлении не трогаются.</summary>
    private static readonly string[] BackupSkipDirs = AutoBackupService.SkipDirs;

    private static string DataDir(IConfiguration config) => AutoBackupService.DataDir(config);

    private static void MapBackup(RouteGroupBuilder g)
    {
        g.MapGet("/", (IConfiguration config) =>
        {
            DataFiles.FlushAll(); // в архив — самые свежие данные
            var bytes = ZipData(DataDir(config));
            return Results.File(bytes, "application/zip", $"greatness-bot-data-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
        });

        // Automatic copies (Settings → Backup): list and download
        g.MapGet("/auto", (IConfiguration config) =>
        {
            var dir = AutoBackupService.BackupDir(config);
            var list = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new { name = f.Name, size = f.Length, at = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() }).ToList()
                : [];
            return Results.Ok(list);
        });
        g.MapGet("/auto/{name}", (string name, IConfiguration config) =>
        {
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..") || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Error("Bad file name.");
            var path = Path.Combine(AutoBackupService.BackupDir(config), name);
            return File.Exists(path) ? Results.File(path, "application/zip", name) : Error("This backup no longer exists.");
        });

        // Тело запроса — zip-файл. Перед восстановлением текущие данные сохраняются в data/backups/,
        // затем бот перезапускается (systemd поднимет его через 10 секунд), чтобы прочитать новые файлы.
        g.MapPost("/restore", async (HttpRequest request, IConfiguration config, IHostApplicationLifetime lifetime,
            AuditLog audit, ILoggerFactory logs) =>
        {
            if (request.ContentLength is null or 0) return Error("Choose a backup .zip file.");
            if (request.ContentLength > 200 * 1024 * 1024) return Error("The file is too large (max 200 MB).");

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            buffer.Position = 0;

            var dataDir = DataDir(config);
            List<(string Path, byte[] Content)> files = [];
            try
            {
                using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')))
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // папка
                    var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                    if (BackupSkipDirs.Any(d => relative.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Error($"Unexpected file in the backup: {relative}");

                    // Защита от «../» в именах: файл должен остаться внутри data/
                    var target = Path.GetFullPath(Path.Combine(dataDir, relative));
                    if (!target.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        return Error($"Bad path in the backup: {relative}");

                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    files.Add((target, ms.ToArray()));
                }
            }
            catch (InvalidDataException)
            {
                return Error("This is not a valid .zip file.");
            }
            if (files.Count == 0) return Error("The backup has no data files.");

            // Страховка: текущие данные — в data/backups/pre-restore-*.zip
            // Несохранённые изменения не должны перезаписать восстановленные файлы
            DataFiles.FlushAll();
            var backups = Path.Combine(dataDir, "backups");
            Directory.CreateDirectory(backups);
            await File.WriteAllBytesAsync(Path.Combine(backups, $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"), ZipData(dataDir));

            DataFiles.Freeze();
            foreach (var (path, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".restore";
                await File.WriteAllBytesAsync(temp, content);
                File.Move(temp, path, overwrite: true);
            }

            audit.Add(0, request.HttpContext.User.UserId() ?? 0, request.HttpContext.User.Identity?.Name ?? "owner", "web", "settings",
                "Restored data from backup", $"{files.Count} file(s); bot restarts");
            logs.CreateLogger("Backup").LogWarning("Data restored from backup ({Count} files), restarting", files.Count);

            // Ответ уходит, затем остановка — systemd перезапустит бота с новыми данными
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                lifetime.StopApplication();
            });
            return Results.Ok(new { files = files.Count });
        });
    }

    private static byte[] ZipData(string dataDir) => AutoBackupService.Zip(dataDir);
}
