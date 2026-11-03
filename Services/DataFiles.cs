using System.Collections.Concurrent;
using System.Text;

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
        GC.KeepAlive(Timer);
        Pending[Path.GetFullPath(path)] = content;
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
