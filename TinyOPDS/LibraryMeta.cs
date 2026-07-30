using System;
using System.IO;

namespace TinyOPDS
{
    /// <summary>
    /// Служебный каталог "libmeta" рядом с корнем библиотеки (или по MetaPath).
    /// Хранит подкаталоги для дубликатов (dup), битых книг (bad) и оригиналов (orig).
    /// Находится ВНЕ сканируемого корня, поэтому не попадает в сканирование.
    /// </summary>
    public static class LibraryMeta
    {
        public const string MetaDirName = "libmeta";
        public const string DupDirName = "dup";
        public const string BadDirName = "bad";
        public const string OrigDirName = "orig";

        /// <summary>
        /// Корень служебного каталога libmeta.
        /// Если задан Settings.MetaPath — используется он, иначе каталог рядом с libraryPath.
        /// </summary>
        public static string GetMetaRoot(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                return null;

            string configured = Properties.Settings.Default.MetaPath;
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            string full = Path.GetFullPath(libraryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            DirectoryInfo parent = Directory.GetParent(full);

            // libraryPath = корень диска -> кладём libmeta прямо внутрь корня диска
            string baseDir = (parent != null) ? parent.FullName : full;
            return Path.Combine(baseDir, MetaDirName);
        }

        public static string GetDupDir(string libraryPath) { return SubDir(libraryPath, DupDirName); }
        public static string GetBadDir(string libraryPath) { return SubDir(libraryPath, BadDirName); }
        public static string GetOrigDir(string libraryPath) { return SubDir(libraryPath, OrigDirName); }

        private static string SubDir(string libraryPath, string name)
        {
            string root = GetMetaRoot(libraryPath);
            return (root == null) ? null : Path.Combine(root, name);
        }

        /// <summary>Создаёт каталог, если его нет. Возвращает false при ошибке.</summary>
        public static bool EnsureDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return true;
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "LibraryMeta.EnsureDir failed for \"{0}\": {1}", dir, e.Message);
                return false;
            }
        }

        /// <summary>
        /// Безопасно перемещает файл в targetDir. При конфликте имён добавляет " (2)", " (3)"...
        /// Если copyInsteadOfMove = true — копирует (оригинал остаётся на месте).
        /// Возвращает итоговый путь назначения или null при ошибке.
        /// </summary>
        public static string MoveFileSafely(string sourceFile, string targetDir, bool copyInsteadOfMove = false)
        {
            if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
            {
                Log.WriteLine(LogLevel.Warning, "LibraryMeta.MoveFileSafely: source not found \"{0}\"", sourceFile);
                return null;
            }
            if (!EnsureDir(targetDir)) return null;

            try
            {
                string dest = GetUniquePath(targetDir, Path.GetFileName(sourceFile));

                if (copyInsteadOfMove)
                    File.Copy(sourceFile, dest, false);
                else
                    File.Move(sourceFile, dest);

                Log.WriteLine(LogLevel.Info, "LibraryMeta: {0} \"{1}\" -> \"{2}\"",
                    copyInsteadOfMove ? "copied" : "moved", sourceFile, dest);
                return dest;
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "LibraryMeta.MoveFileSafely failed \"{0}\" -> \"{1}\": {2}",
                    sourceFile, targetDir, e.Message);
                return null;
            }
        }

        /// <summary>Возвращает несуществующий путь в targetDir на базе fileName (с суффиксом при конфликте).</summary>
        public static string GetUniquePath(string targetDir, string fileName)
        {
            string dest = Path.Combine(targetDir, fileName);
            if (!File.Exists(dest)) return dest;

            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int i = 2;
            do
            {
                dest = Path.Combine(targetDir, string.Format("{0} ({1}){2}", nameOnly, i, ext));
                i++;
            }
            while (File.Exists(dest));
            return dest;
        }
    }
}