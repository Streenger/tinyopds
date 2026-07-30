using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TinyOPDS;
using TinyOPDS.Data;


namespace TinyOPDS
{
    /// <summary>
    /// Приводит библиотеку к инварианту "1 zip = 1 книга".
    /// Архивы с несколькими книгами (.fb2/.epub) раскладываются на отдельные zip.
    /// Оригинал мультикниги при KeepOriginals=true уходит в libmeta/orig, иначе удаляется.
    /// </summary>
    public static class LibraryNormalizer
    {
        private static readonly string[] BookExtensions = { ".fb2", ".epub" };

        public class NormalizeResult
        {
            public int ArchivesScanned;
            public int ArchivesSplit;      // сколько мультиархивов разложено
            public int BooksExtracted;     // сколько отдельных zip создано
            public int LooseBooksScanned;  // сколько голых книг (.fb2/.epub) найдено
            public int BooksPacked;        // сколько голых книг упаковано в zip
            public int Errors;
            public int BooksRepaired;      // ← ДОБАВИТЬ
            public int RepairFixes;        // ← ДОБАВИТЬ
            public List<string> Messages = new List<string>();
        }

        // Кодировки для восстановления имён записей в старых архивах.
        // Latin-1 (28591) отображает байты 0x00-0xFF в символы U+0000-U+00FF без потерь,
        // что позволяет получить сырые байты имени и определить кодировку самостоятельно.
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        private static readonly Encoding Cp866 = Encoding.GetEncoding(866);
        private static readonly Encoding Cp1251 = Encoding.GetEncoding(1251);
        

        /// <summary>
        /// Оценка "правдоподобности" декодированного имени.
        /// Кириллица и ASCII - плюс, псевдографика и редкие кириллические буквы - минус.
        /// </summary>
        private static int ScoreName(string s)
        {
            int score = 0;
            foreach (char c in s)
            {
                if (c < 0x80) { score += 1; continue; }                       // ASCII
                if (c >= 0x0410 && c <= 0x044F) { score += 3; continue; }     // А-я
                if (c == 0x0401 || c == 0x0451) { score += 3; continue; }     // Ёё
                if (c >= 0x2500 && c <= 0x259F) { score -= 8; continue; }     // псевдографика DOS
                if (c >= 0x0400 && c <= 0x045F) { score -= 6; continue; }     // ў Є Ј Ї і - редкие
                if ("«»№—–…''\"\"".IndexOf(c) >= 0) { score += 1; continue; } // типографика
                score -= 4;                                                   // ¦ ° ± ¬ § - мусор
            }
            return score;
        }

        /// <summary>
        /// Восстанавливает имя записи архива, прочитанное через Latin-1.
        /// </summary>
        public static string FixEntryName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;

            bool allBytes = true, hasHigh = false;
            foreach (char c in rawName)
            {
                if (c > 0xFF) { allBytes = false; break; }
                if (c > 0x7F) hasHigh = true;
            }

            // Символы > 0xFF - имя уже корректно декодировано как UTF-8 (был флаг).
            // Нет символов > 0x7F - чистый ASCII, портить нечего.
            if (!allBytes || !hasHigh) return rawName;

            byte[] bytes = new byte[rawName.Length];
            for (int i = 0; i < rawName.Length; i++) bytes[i] = (byte)rawName[i];

            string best = null;
            int bestScore = int.MinValue;

            // UTF-8 без флага (строгая проверка - невалидный UTF-8 отсеется исключением)
            try
            {
                string utf8 = new UTF8Encoding(false, true).GetString(bytes);
                best = utf8; bestScore = ScoreName(utf8) + 2; // небольшой бонус за валидный UTF-8
            }
            catch (DecoderFallbackException) { }

            foreach (var enc in new[] { Cp866, Cp1251 })
            {
                string candidate = enc.GetString(bytes);
                int score = ScoreName(candidate);
                if (score > bestScore) { bestScore = score; best = candidate; }
            }

            return best ?? rawName;
        }

        /// <summary>
        /// Убирает недопустимые для файловой системы символы.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "book" : s;
        }
        private static bool IsBookEntry(string entryName)
        {
            string ext = Path.GetExtension(entryName);
            return BookExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Основной проход. libraryPath — корень библиотеки.
        /// keepOriginals — сохранять исходные мультиархивы в libmeta/orig.
        /// </summary>
        public static NormalizeResult Normalize(string libraryPath, bool keepOriginals,
    bool repairFb2 = true)
        {
            var result = new NormalizeResult();

            if (string.IsNullOrWhiteSpace(libraryPath) || !Directory.Exists(libraryPath))
            {
                Log.WriteLine(LogLevel.Warning, "LibraryNormalizer: library path not found \"{0}\"", libraryPath);
                return result;
            }

            Log.WriteLine(LogLevel.Info, "LibraryNormalizer: started for \"{0}\" (keepOriginals={1})",
                libraryPath, keepOriginals);

            IEnumerable<string> zipFiles;
            try
            {
                zipFiles = Directory.EnumerateFiles(libraryPath, "*.zip", SearchOption.AllDirectories);
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "LibraryNormalizer: enumerate failed: {0}", e.Message);
                result.Errors++;
                return result;
            }

            foreach (string zipPath in zipFiles.ToList())
            {
                result.ArchivesScanned++;
                try
                {
                    NormalizeSingleArchive(zipPath, libraryPath, keepOriginals, result);
                }
                catch (Exception e)
                {
                    result.Errors++;
                    Log.WriteLine(LogLevel.Error, "LibraryNormalizer: failed on \"{0}\": {1}", zipPath, e.Message);
                }
            }
            // --- Проход B: упаковка голых книг (.fb2/.epub) в отдельные zip ---
            List<string> looseBooks;
            try
            {
                looseBooks = BookExtensions
                    .SelectMany(ext => Directory.EnumerateFiles(libraryPath, "*" + ext, SearchOption.AllDirectories))
                    .ToList();
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "LibraryNormalizer: enumerate loose books failed: {0}", e.Message);
                result.Errors++;
                looseBooks = new List<string>();
            }

            foreach (string bookPath in looseBooks)
            {
                result.LooseBooksScanned++;
                try
                {
                    PackLooseBook(bookPath, result);
                }
                catch (Exception e)
                {
                    result.Errors++;
                    Log.WriteLine(LogLevel.Error, "LibraryNormalizer: pack failed \"{0}\": {1}", bookPath, e.Message);
                }
            }
            // --- Проход C: починка нарушений схемы FB2 ---
            if (repairFb2)
            {
                foreach (string zipPath in Directory
                    .EnumerateFiles(libraryPath, "*.zip", SearchOption.AllDirectories).ToList())
                {
                    try
                    {
                        RepairSingleArchive(zipPath, libraryPath, keepOriginals, result);
                    }
                    catch (Exception e)
                    {
                        result.Errors++;
                        Log.WriteLine(LogLevel.Error,
                            "LibraryNormalizer: repair failed \"{0}\": {1}", zipPath, e.Message);
                    }
                }
            }
            Log.WriteLine(LogLevel.Info,
    "LibraryNormalizer: done. scanned={0}, split={1}, extracted={2}, loose={3}, packed={4}, repaired={5}, fixes={6}, errors={7}",
    result.ArchivesScanned, result.ArchivesSplit, result.BooksExtracted,
    result.LooseBooksScanned, result.BooksPacked,
    result.BooksRepaired, result.RepairFixes, result.Errors);

            return result;
        }

        private static void NormalizeSingleArchive(string zipPath, string libraryPath,
           bool keepOriginals, NormalizeResult result)
        {
            List<string> bookEntries;

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, Latin1))
            {
                bookEntries = archive.Entries
                    .Where(en => !string.IsNullOrEmpty(en.Name) && IsBookEntry(en.Name))
                    .Select(en => en.FullName)
                    .ToList();
            }

            // Инвариант уже соблюдён (0 или 1 книга) — ничего не делаем.
            if (bookEntries.Count <= 1)
                return;

            Log.WriteLine(LogLevel.Info, "LibraryNormalizer: splitting \"{0}\" ({1} books)",
                zipPath, bookEntries.Count);

            string targetDir = Path.GetDirectoryName(zipPath);
            int extractedHere = 0;

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, Latin1))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || !IsBookEntry(entry.Name))
                        continue;

                    string fixedName = FixEntryName(entry.Name);
                    string safeName = SanitizeFileName(fixedName);

                    if (!string.Equals(fixedName, entry.Name, StringComparison.Ordinal))
                        Log.WriteLine(LogLevel.Info, "LibraryNormalizer: name repaired \"{0}\" -> \"{1}\"",
                            entry.Name, safeName);

                    string baseName = Path.GetFileNameWithoutExtension(safeName) + ".zip";
                    string newZipPath = LibraryMeta.GetUniquePath(targetDir, baseName);

                    using (var outStream = new FileStream(newZipPath, FileMode.CreateNew, FileAccess.Write))
                    using (var newArchive = new ZipArchive(outStream, ZipArchiveMode.Create))
                    {
                        var newEntry = newArchive.CreateEntry(safeName, CompressionLevel.Optimal);
                        using (var src = entry.Open())
                        using (var dst = newEntry.Open())
                        {
                            src.CopyTo(dst);
                        }
                    }

                    extractedHere++;
                    result.BooksExtracted++;
                }
            }   // <-- архив закрыт, только теперь можно двигать/удалять файл

            // Разложили — теперь убираем исходный мультиархив
            if (extractedHere > 0)
            {
                result.ArchivesSplit++;

                if (keepOriginals)
                {
                    string origDir = LibraryMeta.GetOrigDir(libraryPath);
                    LibraryMeta.MoveFileSafely(zipPath, origDir, copyInsteadOfMove: false);
                }
                else
                {
                    try { File.Delete(zipPath); }
                    catch (Exception e)
                    {
                        result.Errors++;
                        Log.WriteLine(LogLevel.Error, "LibraryNormalizer: delete original failed \"{0}\": {1}",
                            zipPath, e.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Тип B: упаковка «голой» книги (.fb2/.epub) в одноимённый zip рядом.
        /// Оригинал удаляется БЕЗУСЛОВНО, но только после успешной и проверенной упаковки.
        /// </summary>
        private static void PackLooseBook(string bookPath, NormalizeResult result)
        {
            string targetDir = Path.GetDirectoryName(bookPath);
            string entryName = Path.GetFileName(bookPath);
            string baseName = Path.GetFileNameWithoutExtension(bookPath) + ".zip";
            string newZipPath = LibraryMeta.GetUniquePath(targetDir, baseName);

            // 1. Упаковка
            using (var outStream = new FileStream(newZipPath, FileMode.CreateNew, FileAccess.Write))
            using (var newArchive = new ZipArchive(outStream, ZipArchiveMode.Create))
            {
                var newEntry = newArchive.CreateEntry(entryName, CompressionLevel.Optimal);
                using (var src = File.OpenRead(bookPath))
                using (var dst = newEntry.Open())
                {
                    src.CopyTo(dst);
                }
            }

            // 2. Проверка, что zip создан и книга внутри читается
            bool ok = false;
            try
            {
                using (var check = ZipFile.OpenRead(newZipPath))
                {
                    ok = check.Entries.Any(en =>
                        string.Equals(en.Name, entryName, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { ok = false; }

            if (!ok)
            {
                result.Errors++;
                Log.WriteLine(LogLevel.Error, "LibraryNormalizer: pack verify failed \"{0}\"", bookPath);
                try { if (File.Exists(newZipPath)) File.Delete(newZipPath); } catch { }
                return; // оригинал НЕ трогаем — данные целы
            }

            // 3. Только теперь удаляем оригинал (keepOriginals игнорируется — по требованию)
            try
            {
                File.Delete(bookPath);
            }
            catch (Exception e)
            {
                result.Errors++;
                Log.WriteLine(LogLevel.Error, "LibraryNormalizer: delete loose book failed \"{0}\": {1}",
                    bookPath, e.Message);
            }

            result.BooksPacked++;
            Log.WriteLine(LogLevel.Info, "LibraryNormalizer: packed loose \"{0}\" -> \"{1}\"",
                bookPath, newZipPath);
        }

        /// <summary>
        /// Проход C: починка нарушений схемы FB2 внутри архивов.
        /// Архив перезаписывается только если реально были правки.
        /// </summary>
        private static void RepairSingleArchive(string zipPath, string libraryPath,
            bool keepOriginals, NormalizeResult result)
        {
            var payload = new List<Tuple<string, byte[], DateTimeOffset>>();
            int fixesHere = 0;
            bool changed = false;

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, Latin1))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // папка

                    byte[] data;
                    using (var src = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        src.CopyTo(ms);
                        data = ms.ToArray();
                    }

                    if (string.Equals(Path.GetExtension(entry.Name), ".fb2",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        int fixes;
                        byte[] repaired = RepairFb2Bytes(data, zipPath, out fixes);
                        if (repaired != null && fixes > 0)
                        {
                            data = repaired;
                            fixesHere += fixes;
                            changed = true;
                        }
                    }

                    // имя восстанавливаем так же, как в проходах A/B
                    payload.Add(Tuple.Create(
                        FixEntryName(entry.FullName), data, entry.LastWriteTime));
                }
            }   // архив закрыт

            if (!changed) return;

            // резервная копия до правки
            if (keepOriginals)
            {
                try
                {
                    string origDir = LibraryMeta.GetOrigDir(libraryPath);
                    LibraryMeta.MoveFileSafely(zipPath, origDir, copyInsteadOfMove: true);
                }
                catch (Exception e)
                {
                    Log.WriteLine(LogLevel.Warning,
                        "LibraryNormalizer: backup before repair failed \"{0}\": {1}", zipPath, e.Message);
                }
            }

            // пишем во временный файл, затем подменяем
            string tempPath = zipPath + ".repair.tmp";
            try
            {
                using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var newArchive = new ZipArchive(outStream, ZipArchiveMode.Create,
                false, new UTF8Encoding(false)))
                {
                    foreach (var item in payload)
                    {
                        var newEntry = newArchive.CreateEntry(item.Item1, CompressionLevel.Optimal);

                        // zip хранит даты только с 1980 по 2107 — вне диапазона setter бросает
                        try
                        {
                            if (item.Item3.Year >= 1980 && item.Item3.Year <= 2107)
                                newEntry.LastWriteTime = item.Item3;
                        }
                        catch (ArgumentOutOfRangeException) { }

                        using (var dst = newEntry.Open())
                            dst.Write(item.Item2, 0, item.Item2.Length);
                    }
                }

                File.Delete(zipPath);
                File.Move(tempPath, zipPath);
            }
            catch (Exception e)
            {
                result.Errors++;
                Log.WriteLine(LogLevel.Error,
                    "LibraryNormalizer: repack after repair failed \"{0}\": {1}", zipPath, e.Message);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return;
            }

            result.BooksRepaired++;
            result.RepairFixes += fixesHere;
            Log.WriteLine(LogLevel.Info,
                "LibraryNormalizer: fb2 repaired ({0} fixes) \"{1}\"", fixesHere, zipPath);
        }

        /// <summary>
        /// Чинит один fb2 в памяти. Возвращает null, если правок не было
        /// или файл не удалось разобрать. Кодировка исходника сохраняется.
        /// </summary>
        private static byte[] RepairFb2Bytes(byte[] data, string sourceForLog, out int fixes)
        {
            fixes = 0;
            XDocument doc = null;
            int sanitizeFixes = 0;

            try
            {
                using (var ms = new MemoryStream(data, false))
                    doc = XDocument.Load(ms, LoadOptions.PreserveWhitespace);
            }
            catch (Exception firstError)
            {
                // Документ не читается вовсе. Пробуем починить синтаксис текстом.
                string clean;
                try
                {
                    string raw = Fb2Sanitizer.BytesToText(data);
                    clean = Fb2Sanitizer.Sanitize(raw, out sanitizeFixes);
                }
                catch (Exception e)
                {
                    Log.WriteLine(LogLevel.Warning,
                        "LibraryNormalizer: sanitize threw \"{0}\": {1}", sourceForLog, e.Message);
                    return null;
                }

                if (sanitizeFixes == 0)
                {
                    Log.WriteLine(LogLevel.Warning,
                        "LibraryNormalizer: fb2 parse failed, nothing to sanitize \"{0}\": {1}",
                        sourceForLog, firstError.Message);
                    return null;
                }

                try
                {
                    doc = XDocument.Parse(clean, LoadOptions.PreserveWhitespace);
                }
                catch (Exception secondError)
                {
                    Log.WriteLine(LogLevel.Warning,
                        "LibraryNormalizer: sanitize insufficient ({0} fixed), repair skipped \"{1}\": {2}",
                        sanitizeFixes, sourceForLog, secondError.Message);
                    return null;
                }

                Log.WriteLine(LogLevel.Info,
                    "LibraryNormalizer: fb2 sanitized ({0} syntax fixes) \"{1}\"",
                    sanitizeFixes, sourceForLog);
            }

            int repairFixes = 0;
            try { repairFixes = Fb2Repair.Repair(doc); }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Warning,
                    "LibraryNormalizer: repair threw \"{0}\": {1}", sourceForLog, e.Message);
                return null;
            }

            fixes = sanitizeFixes + repairFixes;
            if (fixes == 0) return null;

            // КРИТИЧНО: писать в той же кодировке, что была в декларации,
            // иначе windows-1251 превратится в мусор
            Encoding enc = new UTF8Encoding(false);
            if (doc.Declaration != null && !string.IsNullOrEmpty(doc.Declaration.Encoding))
            {
                try { enc = Encoding.GetEncoding(doc.Declaration.Encoding); }
                catch { }
            }

            var ws = new XmlWriterSettings();
            ws.Encoding = enc;
            ws.Indent = false;

            using (var outMs = new MemoryStream())
            {
                using (var w = XmlWriter.Create(outMs, ws))
                    doc.Save(w);
                return outMs.ToArray();
            }
        }
    }
}