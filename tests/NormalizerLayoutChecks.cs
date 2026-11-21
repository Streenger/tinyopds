using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

// Runs production code against a new synthetic library, never an existing one.
internal static class NormalizerLayoutChecks
{
    private static Assembly app;
    private static string root;
    private static int checks, failures;

    private static void Equal(object expected, object actual)
    {
        if (!object.Equals(expected, actual))
            throw new Exception("Expected [" + expected + "], got [" + actual + "]");
    }

    private static string Payload(string name)
    {
        return "Synthetic book: " + name;
    }

    private static void Archive(string path, params string[] names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (string name in names)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
                    writer.Write(Payload(name));
    }

    private static void Book(string zipPath, string name, string originalName)
    {
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Equal(1, zip.Entries.Count);
            Equal(name, zip.Entries[0].FullName);
            using (var reader = new StreamReader(zip.Entries[0].Open()))
                Equal(Payload(originalName), reader.ReadToEnd());
        }
    }

    private static object Normalize(string library, bool keepOriginals)
    {
        return app.GetType("TinyOPDS.LibraryNormalizer", true).GetMethod("Normalize")
            .Invoke(null, new object[] { library, keepOriginals, false });
    }

    private static int Count(object result, string field)
    {
        return (int)result.GetType().GetField(field).GetValue(result);
    }

    // FB2 without document-info: the repair pass must add it (metadata-only fix).
    private static void RepairableBook(string zipPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("book.fb2").Open(), new UTF8Encoding(false)))
            writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\">"
                + "<description><title-info><genre>prose</genre><author><first-name>A</first-name><last-name>B</last-name></author>"
                + "<book-title>T</book-title><lang>ru</lang></title-info></description><body><section><p>x</p></section></body></FictionBook>");
    }

    private static bool HasDocumentInfo(string zipPath)
    {
        using (var zip = ZipFile.OpenRead(zipPath))
        using (var reader = new StreamReader(zip.Entries[0].Open()))
            return reader.ReadToEnd().Contains("document-info");
    }

    // The normalizer index lives next to the running exe; keep runs independent.
    private static object Repair(string library)
    {
        string index = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "normalized.index");
        if (File.Exists(index)) File.Delete(index);
        try
        {
            return app.GetType("TinyOPDS.LibraryNormalizer", true).GetMethod("Normalize")
                .Invoke(null, new object[] { library, false, true });
        }
        finally { if (File.Exists(index)) File.Delete(index); }
    }

    private static string EnableFileLog(string library)
    {
        string folder = Path.Combine(Directory.GetParent(library).FullName, "logs");
        app.GetType("TinyOPDS.Utils", true)
            .GetField("serviceFilesLocation", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, folder);
        var log = app.GetType("TinyOPDS.Log");
        log.GetField("FlushIntervalSeconds").SetValue(null, 3600);
        log.GetProperty("SaveToFile").SetValue(null, true, null);
        return (string)log.GetProperty("LogFileName").GetValue(null, null);
    }

    private static void Check(string name, Action<string> test)
    {
        string scenario = Path.Combine(root, (++checks).ToString("00"));
        string library = Path.Combine(scenario, "library");
        Directory.CreateDirectory(library);
        var settingsType = app.GetType("TinyOPDS.Properties.Settings", true);
        object settings = settingsType.GetProperty("Default").GetValue(null, null);
        settingsType.GetProperty("MetaPath").SetValue(settings, Path.Combine(scenario, "libmeta"), null);
        try { test(library); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
        finally
        {
            app.GetType("TinyOPDS.Log").GetProperty("SaveToFile").SetValue(null, false, null);
            app.GetType("TinyOPDS.LibraryNormalizer").GetField("IsRunning").SetValue(null, false);
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Usage: NormalizerLayoutChecks.exe <app.exe> <NEW-scratch-directory>"); return 2; }
        root = Path.GetFullPath(args[1]);
        if (Directory.Exists(root) || File.Exists(root)) { Console.Error.WriteLine("Refusing to use an existing scratch path."); return 2; }
        Directory.CreateDirectory(root);
        app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        app.GetType("TinyOPDS.Log", true).GetProperty("SaveToFile").SetValue(null, false, null);

        Check("multi-book ZIP gets its own named directory", library => {
            string source = Path.Combine(library, "fb2-000024-030559.zip");
            Archive(source, "a.fb2", "inside/b.fb2", "c.epub", "cover.jpg");
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(1, Count(result, "ArchivesSplit"));
            Equal(3, Count(result, "BooksExtracted"));
            string folder = Path.Combine(library, "fb2-000024-030559");
            Book(Path.Combine(folder, "a.zip"), "a.fb2", "a.fb2");
            Book(Path.Combine(folder, "b.zip"), "b.fb2", "inside/b.fb2");
            Book(Path.Combine(folder, "c.zip"), "c.epub", "c.epub");
            Equal(false, File.Exists(source));
            Equal(0, Directory.GetFiles(library, "*.zip").Length);
            Equal(3, Directory.GetFiles(folder, "*.zip").Length);
        });

        Check("nested archive keeps its parent path and _lost suffix", library => {
            string parent = Path.Combine(library, "lib.rus.ec");
            Archive(Path.Combine(parent, "fb2-000007-788888_lost.zip"), "7.fb2", "8.fb2");
            Equal(0, Count(Normalize(library, false), "Errors"));
            string folder = Path.Combine(parent, "fb2-000007-788888_lost");
            Book(Path.Combine(folder, "7.zip"), "7.fb2", "7.fb2");
            Book(Path.Combine(folder, "8.zip"), "8.fb2", "8.fb2");
            Equal(0, Directory.GetFiles(parent, "*.zip").Length);
        });

        Check("different archives do not mix identically named books", library => {
            Archive(Path.Combine(library, "first.zip"), "first/shared.fb2", "first.epub");
            Archive(Path.Combine(library, "second.zip"), "second/shared.fb2", "second.epub");
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(2, Count(result, "ArchivesSplit"));
            Book(Path.Combine(library, "first", "shared.zip"), "shared.fb2", "first/shared.fb2");
            Book(Path.Combine(library, "second", "shared.zip"), "shared.fb2", "second/shared.fb2");
            Equal(2, Directory.GetFiles(Path.Combine(library, "first")).Length);
            Equal(2, Directory.GetFiles(Path.Combine(library, "second")).Length);
        });

        Check("existing destination folder and files are not overwritten", library => {
            string folder = Path.Combine(library, "existing");
            string retained = Path.Combine(folder, "a.zip");
            Archive(retained, "preserved.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(retained));
            Archive(Path.Combine(library, "existing.zip"), "a.fb2", "b.fb2");
            Equal(0, Count(Normalize(library, false), "Errors"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(retained)));
            Book(Path.Combine(folder, "a (2).zip"), "a.fb2", "a.fb2");
            Book(Path.Combine(folder, "b.zip"), "b.fb2", "b.fb2");
        });

        Check("duplicate entry basenames keep distinct payloads", library => {
            Archive(Path.Combine(library, "duplicates.zip"), "one/a.fb2", "two/a.fb2");
            Equal(0, Count(Normalize(library, false), "Errors"));
            Book(Path.Combine(library, "duplicates", "a.zip"), "a.fb2", "one/a.fb2");
            Book(Path.Combine(library, "duplicates", "a (2).zip"), "a.fb2", "two/a.fb2");
        });

        Check("successful repeated run creates no extra ZIPs or directories", library => {
            Archive(Path.Combine(library, "repeat.zip"), "a.fb2", "b.fb2");
            Equal(0, Count(Normalize(library, false), "Errors"));
            object second = Normalize(library, false);
            Equal(0, Count(second, "Errors"));
            Equal(0, Count(second, "BooksExtracted"));
            Equal(0, Count(second, "ArchivesSplit"));
            string folder = Path.Combine(library, "repeat");
            Equal(2, Directory.GetFiles(folder).Length);
            Equal(0, Directory.GetDirectories(folder).Length);
            Book(Path.Combine(folder, "a.zip"), "a.fb2", "a.fb2");
            Book(Path.Combine(folder, "b.zip"), "b.fb2", "b.fb2");
        });

        Check("resume skips an already written identical book without (2)", library => {
            string source = Path.Combine(library, "resume.zip");
            string folder = Path.Combine(library, "resume");
            string written = Path.Combine(folder, "a.zip");
            Archive(written, "a.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(written));
            Archive(source, "a.fb2", "b.fb2");
            string log = EnableFileLog(library);
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(1, Count(result, "BooksExtracted"));
            Equal(1, Count(result, "BooksSkippedExisting"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(written)));
            Book(Path.Combine(folder, "b.zip"), "b.fb2", "b.fb2");
            Equal(2, Directory.GetFiles(folder).Length);
            Equal(false, File.Exists(source));
            Equal(true, File.ReadAllText(log).Contains("skippedExisting=1"));
        });

        Check("same name and size but different CRC gets (2)", library => {
            string folder = Path.Combine(library, "crc");
            string other = Path.Combine(folder, "a.zip");
            Directory.CreateDirectory(folder);
            using (var zip = ZipFile.Open(other, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("a.fb2").Open(), new UTF8Encoding(false)))
                writer.Write(Payload("z.fb2")); // та же длина, другое содержимое
            string before = Convert.ToBase64String(File.ReadAllBytes(other));
            Archive(Path.Combine(library, "crc.zip"), "a.fb2", "b.fb2");
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(0, Count(result, "BooksSkippedExisting"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(other)));
            Book(Path.Combine(folder, "a (2).zip"), "a.fb2", "a.fb2");
        });

        Check("truncated partial ZIP is rewritten in place", library => {
            string folder = Path.Combine(library, "partial");
            string partial = Path.Combine(folder, "a.zip");
            Archive(partial, "a.fb2");
            byte[] bytes = File.ReadAllBytes(partial);
            Array.Resize(ref bytes, bytes.Length / 2);
            File.WriteAllBytes(partial, bytes);
            Archive(Path.Combine(library, "partial.zip"), "a.fb2", "b.fb2");
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(2, Count(result, "BooksExtracted"));
            Book(partial, "a.fb2", "a.fb2");
            Equal(2, Directory.GetFiles(folder).Length);
        });

        Check("interrupted duplicate basenames resume without (3)", library => {
            string source = Path.Combine(library, "duplicates.zip");
            Archive(source, "one/a.fb2", "two/a.fb2");
            Equal(0, Count(Normalize(library, false), "Errors"));
            Archive(source, "one/a.fb2", "two/a.fb2"); // как будто удаление не успело
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(2, Count(result, "BooksSkippedExisting"));
            string folder = Path.Combine(library, "duplicates");
            Equal(2, Directory.GetFiles(folder).Length);
            Book(Path.Combine(folder, "a (2).zip"), "a.fb2", "two/a.fb2");
            Equal(false, File.Exists(source));
        });

        Check("source is kept when an output ZIP cannot be flushed", library => {
            string source = Path.Combine(library, "noflush.zip");
            string written = Path.Combine(library, "noflush", "a.zip");
            Archive(written, "a.fb2");
            Archive(source, "a.fb2", "b.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(source));
            using (new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                object result = Normalize(library, false);
                Equal(1, Count(result, "Errors"));
                Equal(0, Count(result, "ArchivesSplit"));
            }
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(source)));
        });

        Check("single-book ZIP and loose-book packing retain their layout", library => {
            string single = Path.Combine(library, "single.zip");
            Archive(single, "inside/single.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(single));
            File.WriteAllText(Path.Combine(library, "loose.fb2"), Payload("loose.fb2"), new UTF8Encoding(false));
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(0, Count(result, "ArchivesSplit"));
            Equal(1, Count(result, "BooksPacked"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(single)));
            Book(Path.Combine(library, "loose.zip"), "loose.fb2", "loose.fb2");
            Equal(false, File.Exists(Path.Combine(library, "loose.fb2")));
            Equal(0, Directory.GetDirectories(library).Length);
        });

        Check("multi-book source is deleted even with KeepOriginals enabled", library => {
            string source = Path.Combine(library, "keep.zip");
            Archive(source, "a.fb2", "b.fb2", "cover.jpg");
            Equal(0, Count(Normalize(library, true), "Errors"));
            Equal(false, File.Exists(source));
            string meta = Path.Combine(Directory.GetParent(library).FullName, "libmeta");
            Equal(false, Directory.Exists(meta));
            Equal(2, Directory.GetFiles(Path.Combine(library, "keep")).Length);
            Book(Path.Combine(library, "keep", "a.zip"), "a.fb2", "a.fb2");
            Book(Path.Combine(library, "keep", "b.zip"), "b.fb2", "b.fb2");
        });

        Check("destination blocked by a file leaves the original intact", library => {
            string source = Path.Combine(library, "blocked.zip");
            Archive(source, "a.fb2", "b.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(source));
            File.WriteAllText(Path.Combine(library, "blocked"), "do not overwrite");
            object result = Normalize(library, false);
            Equal(1, Count(result, "Errors"));
            Equal(0, Count(result, "BooksExtracted"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(source)));
            Equal("do not overwrite", File.ReadAllText(Path.Combine(library, "blocked")));
        });

        Check("failure after one output ZIP preserves the complete source", library => {
            string source = Path.Combine(library, "partial.zip");
            Archive(source, "a.fb2", "b.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(source));
            Directory.CreateDirectory(Path.Combine(library, "partial", "b.zip"));
            string log = EnableFileLog(library);
            object result = Normalize(library, true);
            Equal(1, Count(result, "Errors"));
            Equal(0, Count(result, "ArchivesSplit"));
            Equal(1, Count(result, "BooksExtracted"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(source)));
            Book(Path.Combine(library, "partial", "a.zip"), "a.fb2", "a.fb2");
            string text = File.ReadAllText(log);
            Equal(true, text.Contains("LibraryNormalizer: failed on"));
            Equal(false, text.Contains("source archive deleted"));
        });

        Check("source is already deleted when single-archive worker returns", library => {
            string first = Path.Combine(library, "first.zip");
            string second = Path.Combine(library, "second.zip");
            Archive(first, "a.fb2", "b.fb2");
            Archive(second, "c.fb2", "d.fb2");
            string secondBefore = Convert.ToBase64String(File.ReadAllBytes(second));
            var type = app.GetType("TinyOPDS.LibraryNormalizer");
            var worker = type.GetMethod("NormalizeSingleArchive", BindingFlags.Static | BindingFlags.NonPublic);
            object result = Activator.CreateInstance(type.GetNestedType("NormalizeResult"));
            string log = EnableFileLog(library);
            worker.Invoke(null, new object[] { first, result });
            Equal(false, File.Exists(first));
            Equal(secondBefore, Convert.ToBase64String(File.ReadAllBytes(second)));
            Equal(false, Directory.Exists(Path.Combine(library, "second")));
            Book(Path.Combine(library, "first", "a.zip"), "a.fb2", "a.fb2");
            Book(Path.Combine(library, "first", "b.zip"), "b.fb2", "b.fb2");
            string firstLog = File.ReadAllText(log);
            Equal(true, firstLog.Contains("source archive deleted \"" + first + "\""));
            Equal(false, firstLog.Contains("source archive deleted \"" + second + "\""));
            worker.Invoke(null, new object[] { second, result });
            Equal(false, File.Exists(second));
            Book(Path.Combine(library, "second", "c.zip"), "c.fb2", "c.fb2");
            Book(Path.Combine(library, "second", "d.zip"), "d.fb2", "d.fb2");
            Equal(2, Count(result, "ArchivesSplit"));
            Equal(0, Count(result, "Errors"));
            string bothLogs = File.ReadAllText(log);
            Equal(true, bothLogs.IndexOf(first, StringComparison.Ordinal) <
                bothLogs.IndexOf(second, StringComparison.Ordinal));
        });

        Check("completion log is flushed without waiting for the timer", library => {
            string source = Path.Combine(library, "logged.zip");
            Archive(source, "a.fb2", "b.fb2");
            string log = EnableFileLog(library);
            Equal(0, Count(Normalize(library, false), "Errors"));
            Equal(false, File.Exists(source));
            string text = File.ReadAllText(log);
            Equal(true, text.Contains("source archive deleted \"" + source + "\""));
            Equal(true, text.Contains("output=\"" + Path.Combine(library, "logged") + "\""));
            Equal(true, text.Contains("books=2"));
        });

        Check("source deletion failure is logged without a success event", library => {
            string source = Path.Combine(library, "locked.zip");
            Archive(source, "a.fb2", "b.fb2");
            string before = Convert.ToBase64String(File.ReadAllBytes(source));
            string log = EnableFileLog(library);
            using (var handle = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                object result = Normalize(library, false);
                Equal(1, Count(result, "Errors"));
                Equal(before, Convert.ToBase64String(File.ReadAllBytes(source)));
                string text = File.ReadAllText(log);
                Equal(true, text.Contains("delete original failed"));
                Equal(false, text.Contains("source archive deleted"));
            }
        });

        Check("empty ZIP creates no destination directory", library => {
            string source = Path.Combine(library, "empty.zip");
            Archive(source);
            string before = Convert.ToBase64String(File.ReadAllBytes(source));
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(0, Count(result, "ArchivesSplit"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(source)));
            Equal(0, Directory.GetDirectories(library).Length);
        });

        Check("1024-book archive stays grouped and every payload is readable", library => {
            string[] entries = new string[1024];
            for (int i = 0; i < entries.Length; i++) entries[i] = "b" + i.ToString("0000") + ".fb2";
            Archive(Path.Combine(library, "large.zip"), entries);
            object result = Normalize(library, false);
            Equal(0, Count(result, "Errors"));
            Equal(1024, Count(result, "BooksExtracted"));
            Equal(0, Directory.GetFiles(library, "*.zip").Length);
            string folder = Path.Combine(library, "large");
            Equal(1024, Directory.GetFiles(folder, "*.zip").Length);
            foreach (string name in entries)
                Book(Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + ".zip"), name, name);
        });

        Check("repair pass replaces 250 books across flush batches without temp files", library => {
            for (int i = 0; i < 250; i++) RepairableBook(Path.Combine(library, "r" + i.ToString("000") + ".zip"));
            object result = Repair(library);
            Equal(0, Count(result, "Errors"));
            Equal(250, Count(result, "BooksRepaired"));
            Equal(0, Directory.GetFiles(library, "*.tmp", SearchOption.AllDirectories).Length);
            foreach (string zip in Directory.GetFiles(library, "*.zip")) Equal(true, HasDocumentInfo(zip));
        });

        Check("locked original stays intact while the rest of the batch is repaired", library => {
            for (int i = 0; i < 3; i++) RepairableBook(Path.Combine(library, "l" + i + ".zip"));
            string locked = Path.Combine(library, "l1.zip");
            string before = Convert.ToBase64String(File.ReadAllBytes(locked));
            object result;
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                result = Repair(library);
            Equal(1, Count(result, "Errors"));
            Equal(2, Count(result, "BooksRepaired"));
            Equal(before, Convert.ToBase64String(File.ReadAllBytes(locked)));
            Equal(true, HasDocumentInfo(Path.Combine(library, "l0.zip")));
            Equal(0, Directory.GetFiles(library, "*.tmp", SearchOption.AllDirectories).Length);
        });

        Console.WriteLine("Checks: " + checks + "; Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
