using System;
using System.Collections.Generic;
using System.IO;
using TinyOPDS.Properties;

namespace TinyOPDS
{
    /// <summary>
    /// Потокобезопасный сборщик "битых" и "дублирующихся" книг, обнаруженных во время скана.
    /// Заполняется из обработчиков сканера (bad) и из BatchResult.DuplicateFiles (dup),
    /// а по завершении скана переносит файлы в libmeta/bad и libmeta/dup.
    /// Общий для GUI (MainForm) и CLI (Program), чтобы логика переноса не дублировалась.
    /// </summary>
    public static class NormalizationCollector
    {
        private static readonly object sync = new object();
        private static readonly HashSet<string> badPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> dupPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Сбросить накопленное (вызывать перед началом скана).</summary>
        public static void Reset()
        {
            lock (sync)
            {
                badPaths.Clear();
                dupPaths.Clear();
            }
        }

        /// <summary>
        /// Добавить "битую" книгу. bookName приходит из события OnInvalidBook:
        /// либо физический путь файла, либо "&lt;полный_путь_к_zip&gt;@&lt;запись&gt;".
        /// </summary>
        public static void AddBad(string bookName)
        {
            string physical = ExtractPhysicalPath(bookName);
            if (physical == null) return;
            lock (sync) { badPaths.Add(physical); }
        }

        /// <summary>Добавить относительные/абсолютные пути книг-дублей (из BatchResult.DuplicateFiles).</summary>
        public static void AddDuplicates(IEnumerable<string> paths)
        {
            if (paths == null) return;
            lock (sync)
            {
                foreach (var p in paths)
                {
                    string phys = ExtractPhysicalPath(p);
                    if (phys != null) dupPaths.Add(phys);
                }
            }
        }

        /// <summary>
        /// Перенести все накопленные файлы в libmeta/bad и libmeta/dup.
        /// Относительные пути разрешаются относительно libraryPath.
        /// Для категорий с включённым MoveBadBooks / MoveDuplicates файлы переносятся,
        /// для остальных — удаляются (чтобы не копить место при слиянии библиотек).
        /// </summary>
        public static MoveResult MoveAll(string libraryPath)
        {
            var result = new MoveResult();

            List<string> bad, dup;
            lock (sync)
            {
                bad = new List<string>(badPaths);
                dup = new List<string>(dupPaths);
            }

            string badDir = LibraryMeta.GetBadDir(libraryPath);
            string dupDir = LibraryMeta.GetDupDir(libraryPath);

            foreach (string f in bad)
            {
                string phys = Resolve(libraryPath, f);
                if (phys == null) continue;
                if (Settings.Default.MoveBadBooks)
                {
                    if (LibraryMeta.MoveFileSafely(phys, badDir) != null) result.BadMoved++;
                }
                else if (DeleteSafely(phys)) result.BadDeleted++;
            }

            foreach (string f in dup)
            {
                string phys = Resolve(libraryPath, f);
                if (phys == null) continue;
                if (Settings.Default.MoveDuplicates)
                {
                    if (LibraryMeta.MoveFileSafely(phys, dupDir) != null) result.DupMoved++;
                }
                else if (DeleteSafely(phys)) result.DupDeleted++;
            }

            Log.WriteLine(LogLevel.Info,
                "NormalizationCollector: moved bad={0}, dup={1}; deleted bad={2}, dup={3}",
                result.BadMoved, result.DupMoved, result.BadDeleted, result.DupDeleted);
            return result;
        }

        /// <summary>
        /// Удалить файл книги. Архив, в котором больше одной книги, не трогаем:
        /// вместе с проблемной книгой пропали бы и остальные.
        /// </summary>
        private static bool DeleteSafely(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    int books = 0;
                    try
                    {
                        using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                            foreach (var entry in zip.Entries)
                            {
                                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                                if (ext == ".fb2" || ext == ".epub") books++;
                            }
                    }
                    catch (InvalidDataException) { } // unreadable archive: nothing else to lose
                    if (books > 1)
                    {
                        Log.WriteLine(LogLevel.Warning,
                            "NormalizationCollector: \"{0}\" holds {1} books, left in place for the normalizer (next scan)", path, books);
                        return false;
                    }
                }
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Error, "NormalizationCollector: cannot delete \"{0}\": {1}", path, ex.Message);
                return false;
            }
        }

        /// <summary>Выделить физический путь: "path.zip@entry" -> "path.zip"; иначе сам путь.</summary>
        private static string ExtractPhysicalPath(string bookName)
        {
            if (string.IsNullOrWhiteSpace(bookName)) return null;
            int at = bookName.IndexOf('@');
            string p = (at >= 0) ? bookName.Substring(0, at) : bookName;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }

        /// <summary>Абсолютный путь оставить как есть, относительный — склеить с libraryPath.</summary>
        private static string Resolve(string libraryPath, string path)
        {
            try
            {
                return Path.IsPathRooted(path) ? path : Path.Combine(libraryPath, path);
            }
            catch { return null; }
        }

        public class MoveResult
        {
            public int BadMoved;
            public int DupMoved;
            public int BadDeleted;
            public int DupDeleted;
        }
    }
}