using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LfgBot.Services;

/// <summary>
/// Запись data/*.json: хранилища отдают готовый текст, на диск он попадает в фоне (раз в <see cref="Delay"/>),
/// несколько изменений подряд — одна запись. Пишется атомарно: временный файл → замена (файл не побьётся при сбое).
/// При остановке бота всё несохранённое пишется сразу (<see cref="FlushAll"/>).
/// </summary>
public static class DataFiles
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, string> Pending = new();
    private static readonly object WriteLock = new();
    private static readonly Timer Timer = new(_ => FlushAll(), null, Delay, Delay);
    private static volatile bool _frozen;

    static DataFiles()
    {
        // Последний шанс: процесс завершается — дописать всё, что накопилось
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll();
    }

    /// <summary>Сохранить текст файла (последний вызов для пути побеждает).</summary>
    public static void Save(string path, string content)
    {
        if (_frozen) return;
        var full = Path.GetFullPath(path);
        if (Protected.ContainsKey(full)) return;   // damaged on start and not restorable: never overwrite it
        GC.KeepAlive(Timer);
        Pending[full] = content;
    }

    // ---------- Safe loading ----------

    /// <summary>What went wrong with the data files on this start (written to every server's Log once the bot is ready).</summary>
    public static readonly ConcurrentQueue<string> Warnings = new();

    /// <summary>Files that were damaged and couldn't be restored: the bot runs without them but never writes over them.</summary>
    private static readonly ConcurrentDictionary<string, byte> Protected = new();

    /// <summary>
    /// Read a data file. A damaged file (bad JSON, "null", unreadable) is never silently replaced by empty data:
    /// it is kept as *.damaged-… and the newest automatic backup that has a good copy is restored. A file that is
    /// missing although backups have it is restored too. Nothing to restore from: empty data, and the damaged file is
    /// protected from being overwritten (fix it or restore a backup in Settings → Backup). A fresh install just gets null.
    /// </summary>
    public static T? Load<T>(string path) where T : class
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (File.Exists(full))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(full)) ?? throw new JsonException("the file contains null");
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                var damaged = $"{full}.damaged-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                try { File.Copy(full, damaged, overwrite: true); } catch { /* best effort */ }
                if (FromBackup<T>(full, out var text, out var backup) is { } restored)
                {
                    TryWrite(full, text!);
                    Warn($"Data file {name} was damaged ({ex.Message}) — restored from the automatic backup {backup}. The damaged file was kept as {Path.GetFileName(damaged)}.");
                    return restored;
                }
                Protected[full] = 0;
                Warn($"Data file {name} is damaged ({ex.Message}) and no backup has a good copy — the bot runs without it and will NOT overwrite it. Restore it in Settings → Backup or fix the file.");
                return null;
            }
        }
        if (FromBackup<T>(full, out var lostText, out var from) is { } lost)
        {
            TryWrite(full, lostText!);
            Warn($"Data file {name} was missing — restored from the automatic backup {from}.");
            return lost;
        }
        return null;
    }

    private static void Warn(string message)
    {
        Warnings.Enqueue(message);
        Console.Error.WriteLine($"[DATA SAFETY] {message}");
    }

    private static void TryWrite(string path, string text)
    {
        try { lock (WriteLock) WriteAtomic(path, text); } catch { /* it will be written on the next change */ }
    }

    /// <summary>The newest automatic backup (data/backups/auto-*.zip) with a readable copy of the file.</summary>
    private static T? FromBackup<T>(string full, out string? text, out string? backup) where T : class
    {
        text = null; backup = null;
        // the data folder is the nearest folder (this one or a parent) that has a backups folder
        var dir = Path.GetDirectoryName(full);
        string? dataDir = null;
        for (var d = dir; d is not null && dataDir is null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, "backups"))) dataDir = d;
        if (dataDir is null) return null;
        var entryName = Path.GetRelativePath(dataDir, full).Replace('\\', '/');
        try
        {
            foreach (var zipFile in new DirectoryInfo(Path.Combine(dataDir, "backups")).GetFiles("auto-*.zip").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(zipFile.FullName);
                    if (zip.GetEntry(entryName) is not { } entry) continue;
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    var candidate = reader.ReadToEnd();
                    if (JsonSerializer.Deserialize<T>(candidate) is { } value) { text = candidate; backup = zipFile.Name; return value; }
                }
                catch (Exception) { /* a damaged backup — try an older one */ }
            }
        }
        catch (Exception) { /* no backups readable */ }
        return null;
    }

    /// <summary>Записать всё накопленное сейчас (перед бэкапом, при остановке).</summary>
    public static void FlushAll()
    {
        lock (WriteLock)
        {
            foreach (var path in Pending.Keys.ToList())
            {
                if (!Pending.TryRemove(path, out var content)) continue;
                try
                {
                    WriteAtomic(path, content);
                }
                catch
                {
                    // Диск недоступен — попробуем в следующий раз, если за это время не пришла новая версия
                    Pending.TryAdd(path, content);
                }
            }
        }
    }

    /// <summary>Восстановление из бэкапа: забыть несохранённое и больше ничего не писать до перезапуска.</summary>
    public static void Freeze()
    {
        _frozen = true;
        lock (WriteLock) Pending.Clear();
    }

    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
