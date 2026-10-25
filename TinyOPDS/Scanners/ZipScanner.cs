/*
 * This file is part of TinyOPDS server project
 * https://github.com/sensboston/tinyopds
 *
 * Copyright (c) 2013-2025 SeNSSoFT
 * SPDX-License-Identifier: MIT
 *
 * This module defines the ZipScanner class (FileScanner for 
 * files in zip archives)
 * 
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;

using TinyOPDS.Data;
using TinyOPDS.Parsers;
using System.IO.Compression;

namespace TinyOPDS.Scanner
{
    /// <summary>
    /// Накопительные замеры времени сканирования (в тиках Stopwatch), итог пишется в лог
    /// </summary>
    public static class ScanProfile
    {
        public static long OpenTicks, NameTicks, KnownTicks, ParseTicks, EventTicks, UiTicks;
        public static long Archives, Entries, Skipped, Parsed, UiCalls;
        private static long startTimestamp;

        // Распаковка записи архива в память (отдельно от разбора)
        public static long ReadTicks;

        // Внутри FB2Parser.Parse
        public static long Fb2CopyTicks, Fb2FirstReadTicks, Fb2SanitizeTicks, Fb2SecondReadTicks,
            Fb2DescTicks, Fb2BodyAuthorTicks, Fb2DatesTicks;
        public static long Fb2SanitizeCount, Fb2BodyAuthorCount;

        public static long Now()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void Since(ref long field, long start)
        {
            Interlocked.Add(ref field, Stopwatch.GetTimestamp() - start);
        }

        public static void Reset()
        {
            OpenTicks = NameTicks = KnownTicks = ParseTicks = EventTicks = UiTicks = ReadTicks = 0;
            Archives = Entries = Skipped = Parsed = UiCalls = 0;
            Fb2CopyTicks = Fb2FirstReadTicks = Fb2SanitizeTicks = Fb2SecondReadTicks = 0;
            Fb2DescTicks = Fb2BodyAuthorTicks = Fb2DatesTicks = 0;
            Fb2SanitizeCount = Fb2BodyAuthorCount = 0;
            startTimestamp = Stopwatch.GetTimestamp();
        }

        public static string SummaryFb2()
        {
            return string.Format(
                "Scan profile (read/parse of {0} books): unpack entry {1:F0} ms, Parse() call {2:F0} ms; inside Parse: " +
                "copy to buffer {3:F0} ms, first description read {4:F0} ms, sanitize {5:F0} ms ({6} files), " +
                "second read {7:F0} ms, description parse {8:F0} ms, body author {9:F0} ms ({10} files), dates/log {11:F0} ms",
                Parsed, Ms(ReadTicks), Ms(ParseTicks),
                Ms(Fb2CopyTicks), Ms(Fb2FirstReadTicks), Ms(Fb2SanitizeTicks), Fb2SanitizeCount,
                Ms(Fb2SecondReadTicks), Ms(Fb2DescTicks), Ms(Fb2BodyAuthorTicks), Fb2BodyAuthorCount, Ms(Fb2DatesTicks));
        }

        private static double Ms(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        public static string Summary()
        {
            double total = Ms(Stopwatch.GetTimestamp() - startTimestamp);
            return string.Format(
                "Scan profile: total {0:F0} ms; archives={1}, entries={2}, skipped={3}, parsed={4}; " +
                "open zip {5:F0} ms, entry name {6:F0} ms, known-file check {7:F0} ms, read+parse {8:F0} ms, " +
                "events {9:F0} ms; UI updates {10} calls, {11:F0} ms (UI thread)",
                total, Archives, Entries, Skipped, Parsed,
                Ms(OpenTicks), Ms(NameTicks), Ms(KnownTicks), Ms(ParseTicks), Ms(EventTicks), UiCalls, Ms(UiTicks));
        }
    }

    public class ZipScanner
    {
        public string ZipFileName { get; set; }
        public FileScannerStatus Status { get; set; }
        public int SkippedFiles { get; set; }

        public event BookFoundEventHandler OnBookFound;
        private IEnumerable<BookFoundEventHandler> BookFoundEventHandlers() { return from d in OnBookFound.GetInvocationList() select (BookFoundEventHandler)d; }

        public event InvalidBookEventHandler OnInvalidBook;

        public event FileSkippedEventHandler OnFileSkipped;
        private IEnumerable<FileSkippedEventHandler> FileSkippedEventHandlers() { return from d in OnFileSkipped.GetInvocationList() select (FileSkippedEventHandler)d; }

        public ZipScanner(string zipFileName)
        {
            ZipFileName = zipFileName;
            Status = FileScannerStatus.STOPPED;
            SkippedFiles = 0;
        }

        public void Stop()
        {
            Status = FileScannerStatus.STOPPED;
            if (OnBookFound != null) OnBookFound -= BookFoundEventHandlers().Last();
            if (OnFileSkipped != null) OnFileSkipped -= FileSkippedEventHandlers().Last();
        }

        /// <summary>
        /// Scan zip file using System.IO.Compression for proper memory management
        /// </summary>
        public void Scan()
        {
            Status = FileScannerStatus.SCANNING;
            long tOpen = Stopwatch.GetTimestamp();
            try
            {
                using (var zipArchive = ZipFile.Open(ZipFileName, ZipArchiveMode.Read, LibraryNormalizer.Latin1))
                using (var entryReader = new ZipEntryReader(ZipFileName))
                {
                    Interlocked.Add(ref ScanProfile.OpenTicks, Stopwatch.GetTimestamp() - tOpen);
                    Interlocked.Increment(ref ScanProfile.Archives);

                    for (int entryIndex = 0; entryIndex < zipArchive.Entries.Count; entryIndex++)
                    {
                        var entry = zipArchive.Entries[entryIndex];
                        if (Status != FileScannerStatus.SCANNING) break;

                        if (!string.IsNullOrEmpty(entry.FullName))
                        {
                            // Имя записи восстанавливается из сырых байтов (CP866/CP1251/UTF-8).
                            // Latin1 при чтении гарантирует, что байты дойдут без искажений.
                            long tName = Stopwatch.GetTimestamp();
                            string entryFileName = LibraryNormalizer.FixEntryName(entry.FullName);
                            Interlocked.Add(ref ScanProfile.NameTicks, Stopwatch.GetTimestamp() - tName);
                            Interlocked.Increment(ref ScanProfile.Entries);

                            // Process accepted files
                            try
                            {
                                Book book = null;
                                string ext = Path.GetExtension(entryFileName).ToLower();

                                long tKnown = Stopwatch.GetTimestamp();
                                bool alreadyKnown = Library.ContainsOrReplaced(ZipFileName.Substring(Library.LibraryPath.Length + 1) + "@" + entryFileName);
                                Interlocked.Add(ref ScanProfile.KnownTicks, Stopwatch.GetTimestamp() - tKnown);

                                if (alreadyKnown)
                                {
                                    SkippedFiles++;
                                    Interlocked.Increment(ref ScanProfile.Skipped);
                                    long tSkipEvent = Stopwatch.GetTimestamp();
                                    OnFileSkipped?.Invoke(this, new FileSkippedEventArgs(SkippedFiles));
                                    Interlocked.Add(ref ScanProfile.EventTicks, Stopwatch.GetTimestamp() - tSkipEvent);
                                }
                                else if (ext.Contains(".epub"))
                                {
                                    long tParse = Stopwatch.GetTimestamp();
                                    Interlocked.Increment(ref ScanProfile.Parsed);
                                    using (var entryStream = entryReader.Open(entry, entryIndex))
                                    using (var memStream = new MemoryStream())
                                    {
                                        entryStream.CopyTo(memStream);
                                        memStream.Position = 0;
                                        book = new ePubParser().Parse(memStream, ZipFileName + "@" + entryFileName);

                                        // Ensure correct DocumentSize for archive entries
                                        if (book != null)
                                        {
                                            book.DocumentSize = (uint)entry.Length;
                                        }
                                    }
                                    Interlocked.Add(ref ScanProfile.ParseTicks, Stopwatch.GetTimestamp() - tParse);
                                }
                                else if (ext.Contains(".fb2"))
                                {
                                    long tParse = Stopwatch.GetTimestamp();
                                    Interlocked.Increment(ref ScanProfile.Parsed);
                                    using (var entryStream = entryReader.Open(entry, entryIndex))
                                    using (var memStream = new MemoryStream())
                                    {
                                        entryStream.CopyTo(memStream);
                                        memStream.Position = 0;
                                        long tCall = Stopwatch.GetTimestamp();
                                        Interlocked.Add(ref ScanProfile.ReadTicks, tCall - tParse);
                                        book = new FB2Parser().Parse(memStream, ZipFileName + "@" + entryFileName);
                                        Interlocked.Add(ref ScanProfile.ParseTicks, Stopwatch.GetTimestamp() - tCall);

                                        // Ensure correct DocumentSize for archive entries
                                        if (book != null)
                                        {
                                            book.DocumentSize = (uint)entry.Length;
                                        }
                                    }
                                }

                                if (book != null)
                                {
                                    if (book.IsValid && OnBookFound != null)
                                    {
                                        long tEvent = Stopwatch.GetTimestamp();
                                        OnBookFound(this, new BookFoundEventArgs(book));
                                        Interlocked.Add(ref ScanProfile.EventTicks, Stopwatch.GetTimestamp() - tEvent);
                                    }
                                    else if (!book.IsValid && OnInvalidBook != null)
                                    {
                                        OnInvalidBook(this, new InvalidBookEventArgs(ZipFileName + "@" + entryFileName));
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Log.WriteLine(LogLevel.Error, ".ScanDirectory: exception {0} on file: {1}", e.Message, ZipFileName + "@" + entryFileName);
                                OnInvalidBook?.Invoke(this, new InvalidBookEventArgs(ZipFileName + "@" + entryFileName));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Error, "Error scanning ZIP file {0}: {1}", ZipFileName, ex.Message);
                Status = FileScannerStatus.STOPPED;
            }
            finally
            {
                if (Status == FileScannerStatus.SCANNING)
                {
                    Status = FileScannerStatus.STOPPED;
                }
            }
        }
    }
}