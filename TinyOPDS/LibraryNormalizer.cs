using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using TinyOPDS;
using TinyOPDS.Data;


namespace TinyOPDS
{
    /// <summary>
    /// Приводит библиотеку к инварианту "1 zip = 1 книга".
    /// Архивы с несколькими книгами (.fb2/.epub) раскладываются на отдельные zip
    /// в подпапке с именем исходного архива без расширения.
    /// Исходный мультиархив удаляется после записи всех книг; результат пишется в лог
    /// до перехода к следующему архиву.
    /// </summary>
    public static class LibraryNormalizer
    {
        public static volatile bool IsRunning = false;
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
            public int ArchivesSkippedIndexed; // пропущено по индексу уже нормализованных
            public int BooksSkippedExisting;   // уже записаны прерванным запуском, копия не создана
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
        /// keepOriginals — сохранять резервные копии при содержательной починке FB2.
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

            IsRunning = true;
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
                    NormalizeSingleArchive(zipPath, result);
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
                var index = NormalizerIndex.Load(libraryPath);
                var todo = new List<string>();

                foreach (string zipPath in Directory
                    .EnumerateFiles(libraryPath, "*.zip", SearchOption.AllDirectories).ToList())
                {
                    try
                    {
                        // Уже нормализован и не менялся - не читаем архив вовсе
                        if (index.IsUpToDate(zipPath)) result.ArchivesSkippedIndexed++;
                        else todo.Add(zipPath);
                    }
                    catch (Exception e)
                    {
                        result.Errors++;
                        Log.WriteLine(LogLevel.Error,
                            "LibraryNormalizer: repair failed \"{0}\": {1}", zipPath, e.Message);
                    }
                }

                RepairInParallel(todo, libraryPath, keepOriginals, index, result);
                index.Save();
            }
            Log.WriteLine(LogLevel.Info,
    "LibraryNormalizer: done. scanned={0}, split={1}, extracted={2}, loose={3}, packed={4}, repaired={5}, fixes={6}, skipped(indexed)={7}, errors={8}",
    result.ArchivesScanned, result.ArchivesSplit, result.BooksExtracted,
    result.LooseBooksScanned, result.BooksPacked,
    result.BooksRepaired, result.RepairFixes, result.ArchivesSkippedIndexed, result.Errors);

            return result;
        }

        private static void NormalizeSingleArchive(string zipPath, NormalizeResult result)
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

            // Не собираем книги всех мультиархивов в одном огромном каталоге.
            string targetDir = Path.Combine(Path.GetDirectoryName(zipPath),
                Path.GetFileNameWithoutExtension(zipPath));
            Directory.CreateDirectory(targetDir);
            int extractedHere = 0;
            int skippedHere = 0;
            var outputs = new List<string>(bookEntries.Count);

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
                    string nameOnly = Path.GetFileNameWithoutExtension(baseName);
                    string newZipPath = null;

                    // После прерванного запуска книга может уже лежать в папке: такую не пишем
                    // повторно и не создаём " (2)". Другая книга с тем же именем получает суффикс,
                    // недописанный (нечитаемый) ZIP перезаписывается.
                    for (int n = 1; newZipPath == null; n++)
                    {
                        string candidate = Path.Combine(targetDir, n == 1 ? baseName
                            : string.Format("{0} ({1}).zip", nameOnly, n));
                        if (!File.Exists(candidate)) { newZipPath = candidate; break; }

                        bool? same = SameBookZip(candidate, safeName, entry);
                        if (same == true) { outputs.Add(candidate); break; }
                        if (same == null)
                        {
                            Log.WriteLine(LogLevel.Warning,
                                "LibraryNormalizer: replacing unreadable partial ZIP \"{0}\"", candidate);
                            newZipPath = candidate;
                        }
                    }

                    if (newZipPath == null)
                    {
                        extractedHere++;
                        skippedHere++;
                        result.BooksSkippedExisting++;
                        continue;
                    }

                    using (var outStream = new FileStream(newZipPath, FileMode.Create, FileAccess.Write))
                    {
                        using (var newArchive = new ZipArchive(outStream, ZipArchiveMode.Create, true))
                        {
                            var newEntry = newArchive.CreateEntry(safeName, CompressionLevel.Optimal);
                            using (var src = entry.Open())
                            using (var dst = newEntry.Open())
                            {
                                src.CopyTo(dst);
                            }
                        }
                    }

                    outputs.Add(newZipPath);
                    extractedHere++;
                    result.BooksExtracted++;
                }
            }   // Исходный архив и все выходные ZIP закрыты.

            if (extractedHere != bookEntries.Count)
                throw new InvalidDataException("Not all books were written; source archive retained.");

            // Один проход сброса на диск вместо Flush(true) после каждой книги: до удаления
            // сборника все его ZIP (и записанные сейчас, и пропущенные) должны быть на диске.
            foreach (string output in outputs)
                using (var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.Read))
                    fs.Flush(true);

            result.ArchivesSplit++;
            // Удаляем здесь, до возврата в цикл; KeepOriginals относится только к починке FB2.
            try { File.Delete(zipPath); }
            catch (Exception e)
            {
                result.Errors++;
                Log.WriteLine(LogLevel.Error, "LibraryNormalizer: delete original failed \"{0}\": {1}",
                    zipPath, e.Message);
                return;
            }

            Log.WriteLine(LogLevel.Info,
                "LibraryNormalizer: source archive deleted \"{0}\"; output=\"{1}\"; books={2}; skippedExisting={3}",
                zipPath, targetDir, extractedHere, skippedHere);
            Log.Flush(); // Завершение текущего сборника видно в файле до обработки следующего.
        }

        // В .NET Framework 4.8 у ZipArchiveEntry нет публичного Crc32; поле заполняется
        // из центрального каталога при открытии архива, распаковка не нужна.
        private static readonly System.Reflection.FieldInfo Crc32Field = typeof(ZipArchiveEntry)
            .GetField("_crc32", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static uint Crc32(ZipArchiveEntry entry)
        {
            if (Crc32Field == null)
                throw new NotSupportedException("ZipArchiveEntry CRC32 is unavailable in this .NET runtime");
            return (uint)Crc32Field.GetValue(entry);
        }

        /// <summary>
        /// true — в ZIP ровно эта книга (имя, размер и CRC32 совпадают); false — другой файл;
        /// null — ZIP не читается, то есть недописан при прерывании.
        /// Содержимое не распаковывается.
        /// </summary>
        private static bool? SameBookZip(string zipPath, string entryName, ZipArchiveEntry source)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    if (zip.Entries.Count != 1) return false;
                    var e = zip.Entries[0];
                    if (!string.Equals(e.FullName, entryName, StringComparison.Ordinal)
                        || e.Length != source.Length)
                        return false;
                    return Crc32(e) == Crc32(source);
                }
            }
            catch (InvalidDataException) { return null; }
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
            {
                using (var newArchive = new ZipArchive(outStream, ZipArchiveMode.Create, true))
                {
                    var newEntry = newArchive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var src = File.OpenRead(bookPath))
                    using (var dst = newEntry.Open())
                    {
                        src.CopyTo(dst);
                    }
                }
                outStream.Flush(true);   // данные на диск до удаления оригинала
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

        private const long RepairMemoryBudget = 2L << 30;   // архивы в очередях и в работе, байт

        private sealed class RepairJob
        {
            public string ZipPath;
            public long Reserved;
            public byte[] Source, NewArchive;   // исходный и починенный zip целиком в памяти
            public Exception Error;
            public int Fixes;
            public bool Heavy;
            public NormalizerIndex.Status Status;
        }

        /// <summary>
        /// Сколько потоков разбора: физические ядра минус одно (его оставляем системе и потокам
        /// чтения/записи). Считаем, что физических ядер минимум 2, значит потоков минимум 1.
        /// </summary>
        internal static int RepairWorkers()
        {
            return Math.Max(1, PhysicalCores() - 1);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

        /// <summary>
        /// Число физических ядер по GetLogicalProcessorInformationEx(RelationProcessorCore).
        /// Если Windows не ответила, берём логические ядра пополам (типичный HyperThreading).
        /// </summary>
        internal static int PhysicalCores()
        {
            const int RelationProcessorCore = 0;
            int length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (length > 0)
            {
                IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(length);
                try
                {
                    if (GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                    {
                        // Каждая запись: Relationship (int), Size (int), ...; одна запись = одно ядро
                        int cores = 0;
                        for (int offset = 0; offset < length; cores++)
                            offset += System.Runtime.InteropServices.Marshal.ReadInt32(buffer, offset + 4);
                        if (cores > 0) return cores;
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
            }
            return Math.Max(2, Environment.ProcessorCount / 2);
        }

        /// <summary>
        /// Проход C: конвейер "чтение - разбор - запись". Один поток читает архивы с диска подряд,
        /// несколько потоков чинят FB2 и собирают новый zip в памяти, вызывающий поток пишет
        /// результаты на диск по одному и подменяет оригиналы пачками после сброса на диск.
        /// Индекс, счётчики и резервные копии трогает только вызывающий поток.
        /// </summary>
        private static void RepairInParallel(List<string> todo, string libraryPath, bool keepOriginals,
            NormalizerIndex index, NormalizeResult result)
        {
            int workers = RepairWorkers();
            Log.WriteLine(LogLevel.Info,
                "LibraryNormalizer: repair pass for {0} archives, {1} workers ({2} physical cores, {3} logical)",
                todo.Count, workers, PhysicalCores(), Environment.ProcessorCount);

            var budget = new MemoryBudget(RepairMemoryBudget);
            var total = System.Diagnostics.Stopwatch.StartNew();
            long readTicks = 0, prepareTicks = 0, stageTicks = 0, waitTicks = 0;
            commitFlushTicks = commitReplaceTicks = 0;
            using (var input = new BlockingCollection<RepairJob>(workers * 4))
            using (var output = new BlockingCollection<RepairJob>(workers * 4))
            {
                Task reader = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        foreach (string zipPath in todo)
                        {
                            var job = new RepairJob { ZipPath = zipPath };
                            try
                            {
                                // исходник и новый архив примерно одного размера
                                job.Reserved = budget.Acquire(2 * new FileInfo(zipPath).Length);
                                job.Source = File.ReadAllBytes(zipPath);
                            }
                            catch (Exception e) { job.Error = e; }
                            input.Add(job);
                        }
                    }
                    finally { readTicks = sw.ElapsedTicks; input.CompleteAdding(); }
                });

                var cpu = new Task[workers];
                for (int i = 0; i < workers; i++)
                    cpu[i] = Task.Run(() =>
                    {
                        foreach (RepairJob job in input.GetConsumingEnumerable())
                        {
                            if (job.Error == null)
                            {
                                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                                try { PrepareRepair(job); }
                                catch (Exception e) { job.Error = e; }
                                System.Threading.Interlocked.Add(ref prepareTicks,
                                    System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                            }
                            job.Source = null;
                            output.Add(job);
                        }
                    });
                Task.WhenAll(cpu).ContinueWith(t => output.CompleteAdding());

                var pending = new List<PendingRepair>();
                int sinceSave = 0;
                long waitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                while (true)
                {
                    RepairJob job;
                    if (!output.TryTake(out job))
                    {
                        // Разбор отстаёт: сбрасываем накопленное, заодно возвращая память читателю
                        if (pending.Count > 0) sinceSave += CommitRepairs(pending, index, result, budget);
                        if (!output.TryTake(out job, System.Threading.Timeout.Infinite)) break;
                    }
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    waitTicks += t0 - waitFrom;
                    int staged = pending.Count;
                    try { sinceSave += StageRepair(job, libraryPath, keepOriginals, index, result, pending); }
                    catch (Exception e)
                    {
                        result.Errors++;
                        Log.WriteLine(LogLevel.Error,
                            "LibraryNormalizer: repair failed \"{0}\": {1}", job.ZipPath, e.Message);
                    }
                    finally
                    {
                        // Архив, ушедший в пачку, держит память до записи в CommitRepairs
                        if (pending.Count == staged) budget.Release(job.Reserved);
                        else pending[staged].Reserved = job.Reserved;
                        job.NewArchive = null;
                        stageTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    }

                    if (pending.Count >= RepairFlushBatch)
                        sinceSave += CommitRepairs(pending, index, result, budget);
                    if (sinceSave >= NormalizerIndex.SaveEvery)
                    {
                        index.Save();
                        sinceSave = 0;
                    }
                    waitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                }

                CommitRepairs(pending, index, result, budget);
                reader.Wait();
                Task.WaitAll(cpu);
            }

            // Где уходит время: read - поток чтения целиком, prepare - сумма по потокам разбора,
            // wait - запись ждала разбор, stage - резервные копии, flush - запись пачки, replace - подмена
            Func<long, string> sec = t => (t / (double)System.Diagnostics.Stopwatch.Frequency).ToString("0.0");
            Log.WriteLine(LogLevel.Info,
                "LibraryNormalizer: repair timing total={0}s read={1}s prepare={2}s ({3} workers) wait={4}s stage={5}s flush={6}s replace={7}s",
                sec(total.ElapsedTicks), sec(readTicks), sec(prepareTicks), workers, sec(waitTicks),
                sec(stageTicks), sec(commitFlushTicks), sec(commitReplaceTicks));
        }

        /// <summary>
        /// Ограничивает объём архивов в памяти: чтение ждёт, пока запись не освободит место.
        /// Архив больше всего бюджета берёт бюджет целиком и идёт один.
        /// </summary>
        private sealed class MemoryBudget
        {
            private readonly long limit;
            private long free;

            public MemoryBudget(long limit) { this.limit = free = limit; }

            public long Acquire(long bytes)
            {
                bytes = Math.Max(1, Math.Min(bytes, limit));
                lock (this)
                {
                    while (free < bytes) System.Threading.Monitor.Wait(this);
                    free -= bytes;
                }
                return bytes;
            }

            public void Release(long bytes)
            {
                if (bytes == 0) return;
                lock (this)
                {
                    free += bytes;
                    System.Threading.Monitor.PulseAll(this);
                }
            }
        }

        /// <summary>
        /// Чинит FB2 внутри архива целиком в памяти, без диска; безопасно для нескольких потоков.
        /// Новый zip собирается, только если реально были правки.
        /// </summary>
        private static void PrepareRepair(RepairJob job)
        {
            var payload = new List<Tuple<string, byte[], DateTimeOffset>>();
            bool changed = false, anyUnrepairable = false;

            using (var archive = new ZipArchive(new MemoryStream(job.Source, false), ZipArchiveMode.Read, false, Latin1))
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
                        bool unrepairable;
                        bool lightOnly;
                        byte[] repaired = RepairFb2Bytes(data, job.ZipPath,
                            job.ZipPath + "@" + FixEntryName(entry.FullName),
                            entry.LastWriteTime.DateTime, out fixes, out lightOnly, out unrepairable);
                        if (unrepairable) anyUnrepairable = true;
                        if (repaired != null && fixes > 0)
                        {
                            data = repaired;
                            job.Fixes += fixes;
                            changed = true;
                            if (!lightOnly) job.Heavy = true;   // правки содержимого, а не только id/дат
                        }
                    }

                    // имя восстанавливаем так же, как в проходах A/B
                    payload.Add(Tuple.Create(
                        FixEntryName(entry.FullName), data, entry.LastWriteTime));
                }
            }

            if (!changed)
            {
                job.Status = anyUnrepairable ? NormalizerIndex.Status.Unrepairable : NormalizerIndex.Status.Clean;
                return;
            }
            job.Status = anyUnrepairable ? NormalizerIndex.Status.Unrepairable : NormalizerIndex.Status.Repaired;

            using (var outStream = new MemoryStream())
            {
                using (var newArchive = new ZipArchive(outStream, ZipArchiveMode.Create,
                    true, new UTF8Encoding(false)))
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

                // Проверяем, что новый архив читается и содержит все записи
                outStream.Position = 0;
                using (var check = new ZipArchive(outStream, ZipArchiveMode.Read, true))
                {
                    if (check.Entries.Count != payload.Count)
                        throw new InvalidDataException("repacked archive has " + check.Entries.Count
                            + " entries, expected " + payload.Count);
                }
                job.NewArchive = outStream.ToArray();
            }
        }

        /// <summary>
        /// Пишет готовый архив во временный файл рядом с оригиналом и ставит его в очередь на подмену.
        /// Возвращает число записей, добавленных в индекс.
        /// </summary>
        private static int StageRepair(RepairJob job, string libraryPath, bool keepOriginals,
            NormalizerIndex index, NormalizeResult result, List<PendingRepair> pending)
        {
            if (job.Error != null)
            {
                result.Errors++;
                Log.WriteLine(LogLevel.Error,
                    "LibraryNormalizer: repair failed \"{0}\": {1}", job.ZipPath, job.Error.Message);
                return 0;
            }

            if (job.NewArchive == null)
            {
                index.Record(job.ZipPath, job.Status);
                return 1;
            }

            // резервная копия до правки: только для содержательных правок.
            // Дописанные id и даты (и перестановка элементов описания) текст книги не меняют
            if (keepOriginals && job.Heavy)
            {
                try
                {
                    string origDir = LibraryMeta.GetOrigDir(libraryPath);
                    LibraryMeta.MoveFileSafely(job.ZipPath, origDir, copyInsteadOfMove: true);
                }
                catch (Exception e)
                {
                    Log.WriteLine(LogLevel.Warning,
                        "LibraryNormalizer: backup before repair failed \"{0}\": {1}", job.ZipPath, e.Message);
                }
            }

            // Запись и подмена оригинала - в CommitRepairs, всей пачкой
            pending.Add(new PendingRepair
            {
                ZipPath = job.ZipPath,
                TempPath = job.ZipPath + ".repair.tmp",
                Data = job.NewArchive,
                Fixes = job.Fixes,
                Heavy = job.Heavy,
                Status = job.Status
            });
            return 0;
        }

        private const int RepairFlushBatch = 100;
        private static long commitFlushTicks, commitReplaceTicks;   // только для строки repair timing

        private const int MOVEFILE_REPLACE_EXISTING = 1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        // Префикс \\?\ снимает предел MAX_PATH у прямых вызовов WinAPI
        private static string LongPath(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;
            path = Path.GetFullPath(path);
            return path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        private sealed class PendingRepair
        {
            public string ZipPath, TempPath;
            public byte[] Data;
            public long Reserved;
            public int Fixes;
            public bool Heavy;
            public NormalizerIndex.Status Status;
        }

        /// <summary>
        /// Сбрасывает на диск пачку починенных временных архивов одним проходом и только потом
        /// подменяет оригиналы: при сбое питания не остаётся книги нужного размера, заполненной нулями.
        /// Возвращает число записей, добавленных в индекс.
        /// </summary>
        private static int CommitRepairs(List<PendingRepair> pending, NormalizerIndex index, NormalizeResult result,
            MemoryBudget budget)
        {
            // Запись и сброс ждут устройство, а не процессор: файлы пачки пишутся параллельно
            var flushError = new Exception[pending.Count];
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            System.Threading.Tasks.Parallel.For(0, pending.Count,
                new ParallelOptions { MaxDegreeOfParallelism = RepairWorkers() }, i =>
                {
                    try
                    {
                        using (var fs = new FileStream(pending[i].TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            fs.Write(pending[i].Data, 0, pending[i].Data.Length);
                            fs.Flush(true);
                        }
                    }
                    catch (Exception e) { flushError[i] = e; }
                    finally
                    {
                        pending[i].Data = null;
                        budget.Release(pending[i].Reserved);
                    }
                });
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            commitFlushTicks += t1 - t0;

            // Вся пачка уже на диске - теперь подмена оригиналов атомарным переименованием.
            // File.Replace делает то же, но в разы медленнее
            System.Threading.Tasks.Parallel.For(0, pending.Count,
                new ParallelOptions { MaxDegreeOfParallelism = RepairWorkers() }, i =>
                {
                    if (flushError[i] != null) return;
                    if (!MoveFileEx(LongPath(pending[i].TempPath), LongPath(pending[i].ZipPath), MOVEFILE_REPLACE_EXISTING))
                        flushError[i] = new System.ComponentModel.Win32Exception();
                });

            int recorded = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                if (flushError[i] != null)
                {
                    result.Errors++;
                    Log.WriteLine(LogLevel.Error,
                        "LibraryNormalizer: repack after repair failed \"{0}\": {1}", p.ZipPath, flushError[i].Message);
                    try { if (File.Exists(p.TempPath)) File.Delete(p.TempPath); } catch { }
                    continue;
                }

                result.BooksRepaired++;
                result.RepairFixes += p.Fixes;
                Log.WriteLine(LogLevel.Info,
                    "LibraryNormalizer: fb2 repaired ({0} fixes{2}) \"{1}\"", p.Fixes, p.ZipPath,
                    p.Heavy ? "" : ", metadata only, no backup");
                index.Record(p.ZipPath, p.Status);
                recorded++;
            }
            pending.Clear();
            commitReplaceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            return recorded;
        }


        /// <summary>
        /// Чинит один fb2 в памяти. Возвращает null, если правок не было
        /// или файл не удалось разобрать. Кодировка исходника сохраняется.
        /// </summary>
        private static byte[] RepairFb2Bytes(byte[] data, string sourceForLog, string idSeed,
            DateTime fileDate, out int fixes, out bool lightOnly, out bool unrepairable)
        {
            fixes = 0;
            lightOnly = false;
            unrepairable = false;
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
                    unrepairable = true;
                    return null;
                }

                if (sanitizeFixes == 0)
                {
                    Log.WriteLine(LogLevel.Warning,
                        "LibraryNormalizer: fb2 parse failed, nothing to sanitize \"{0}\": {1}",
                        sourceForLog, firstError.Message);
                    unrepairable = true;
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
                    unrepairable = true;
                    return null;
                }

                Log.WriteLine(LogLevel.Info,
                    "LibraryNormalizer: fb2 sanitized ({0} syntax fixes) \"{1}\"",
                    sanitizeFixes, sourceForLog);
            }

            int repairFixes = 0;
            int lightFixes = 0;
            try { repairFixes = Fb2Repair.Repair(doc, idSeed, fileDate, out lightFixes); }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Warning,
                    "LibraryNormalizer: repair threw \"{0}\": {1}", sourceForLog, e.Message);
                unrepairable = true;
                return null;
            }

            fixes = sanitizeFixes + repairFixes;
            if (fixes == 0) return null;
            lightOnly = sanitizeFixes == 0 && repairFixes == lightFixes;

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
    /// <summary>
    /// Индекс уже нормализованных архивов. Хранится рядом с исполняемым файлом
    /// (normalized.index), саму библиотеку не изменяет. Архив пропускается, если
    /// размер и время изменения не изменились с момента записи. Исход Unrepairable
    /// перепроверяется только после увеличения Version (улучшения санитайзера).
    /// Файл можно удалить - тогда будут проверены все архивы.
    /// </summary>
    internal sealed class NormalizerIndex
    {
        /// <summary>Версия логики починки. Увеличивать при улучшении Fb2Sanitizer/Fb2Repair.</summary>
        public const int Version = 4;

        // Записи Clean/Repaired с версией ниже этой перепроверяются: версия 2 начала
        // записывать в книги id документа и даты (document-info и дата книги по году издания),
        // версия 3 - убирать HTML-элементы (br, b, i, font...) из описания
        public const int MinCleanVersion = 3;
        public const int SaveEvery = 500;
        private const string Header = "# TinyOPDS normalizer index v1";

        public enum Status { Clean = 'O', Repaired = 'R', Unrepairable = 'U', Failed = 'F' }

        private struct Entry
        {
            public Status Status;
            public int Version;
            public long Length;
            public long Ticks;
        }

        private readonly string root;
        private readonly string path;
        private readonly Dictionary<string, Entry> items =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private bool dirty;

        public static string IndexPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "normalized.index"); }
        }

        private NormalizerIndex(string libraryPath)
        {
            root = libraryPath.TrimEnd('\\', '/');
            path = IndexPath;
        }

        private string Key(string zipPath)
        {
            if (zipPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && zipPath.Length > root.Length + 1)
                return zipPath.Substring(root.Length + 1);
            return zipPath;
        }

        public static NormalizerIndex Load(string libraryPath)
        {
            var idx = new NormalizerIndex(libraryPath);
            try
            {
                if (!File.Exists(idx.path)) return idx;

                string[] lines = File.ReadAllLines(idx.path, Encoding.UTF8);
                // строка 1 - заголовок, строка 2 - корень библиотеки
                if (lines.Length < 2 || lines[0] != Header || !lines[1].StartsWith("library="))
                    return idx;
                if (!string.Equals(lines[1].Substring("library=".Length), idx.root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Log.WriteLine(LogLevel.Info,
                        "LibraryNormalizer: index belongs to another library, starting a new one");
                    return idx;
                }

                for (int i = 2; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split('\t');
                    if (f.Length != 5 || f[0].Length != 1) continue;
                    var e = new Entry();
                    e.Status = (Status)f[0][0];
                    if (!int.TryParse(f[1], out e.Version)) continue;
                    if (!long.TryParse(f[2], out e.Length)) continue;
                    if (!long.TryParse(f[3], out e.Ticks)) continue;
                    idx.items[f[4]] = e;
                }
                Log.WriteLine(LogLevel.Info, "LibraryNormalizer: index loaded, {0} archives", idx.items.Count);
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Warning, "LibraryNormalizer: index load failed: {0}", e.Message);
                idx.items.Clear();
            }
            return idx;
        }

        public bool IsUpToDate(string zipPath)
        {
            Entry e;
            if (!items.TryGetValue(Key(zipPath), out e)) return false;

            // отказ перепроверяем после улучшения логики починки
            if (e.Status == Status.Unrepairable && e.Version < Version) return false;
            if (e.Status != Status.Unrepairable && e.Version < MinCleanVersion) return false;

            var fi = new FileInfo(zipPath);
            return fi.Exists && fi.Length == e.Length && fi.LastWriteTimeUtc.Ticks == e.Ticks;
        }

        public void Record(string zipPath, Status status)
        {
            var fi = new FileInfo(zipPath);
            if (!fi.Exists) return;      // архив ушёл (например, разложен и перемещён)
            items[Key(zipPath)] = new Entry
            {
                Status = status,
                Version = Version,
                Length = fi.Length,
                Ticks = fi.LastWriteTimeUtc.Ticks
            };
            dirty = true;
        }

        public void Save()
        {
            if (!dirty) return;
            string tmp = path + ".tmp";
            try
            {
                using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                {
                    w.WriteLine(Header);
                    w.WriteLine("library=" + root);
                    foreach (var kv in items)
                        w.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}",
                            (char)kv.Value.Status, kv.Value.Version, kv.Value.Length, kv.Value.Ticks, kv.Key);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                dirty = false;
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Warning, "LibraryNormalizer: index save failed: {0}", e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
