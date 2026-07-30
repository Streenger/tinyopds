using System;
using System.Collections.Generic;
using System.IO;

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
                if (phys != null && LibraryMeta.MoveFileSafely(phys, badDir) != null)
                    result.BadMoved++;
            }

            foreach (string f in dup)
            {
                string phys = Resolve(libraryPath, f);
                if (phys != null && LibraryMeta.MoveFileSafely(phys, dupDir) != null)
                    result.DupMoved++;
            }

            Log.WriteLine(LogLevel.Info,
                "NormalizationCollector: moved bad={0}, dup={1}", result.BadMoved, result.DupMoved);
            return result;
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
        }
    }
}