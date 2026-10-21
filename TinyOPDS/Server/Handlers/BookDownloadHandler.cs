/*
 * This file is part of TinyOPDS server project
 * https://github.com/sensboston/tinyopds
 *
 * Copyright (c) 2013-2025 SeNSSoFT
 * SPDX-License-Identifier: MIT
 *
 * This module handles books download requests (FB2 and EPUB)
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TinyOPDS.Data;
using System.Text.RegularExpressions;

namespace TinyOPDS.Server
{
    /// <summary>
    /// Handles book download requests for FB2 and EPUB formats
    /// </summary>
    public class BookDownloadHandler
    {
        private static readonly Encoding Cp866 = Encoding.GetEncoding(866);
        private static readonly Encoding Cp1251 = Encoding.GetEncoding(1251);

        /// <summary>
        /// Возможные прочтения имени записи, прочитанного через Latin-1.
        /// Нужно для совместимости: в БД могли попасть как корректные имена (новый скан),
        /// так и искажённые - байты CP866, прочитанные системной ANSI (старые записи).
        /// </summary>
        private static List<string> EntryNameVariants(string rawName)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(rawName)) return list;

            list.Add(rawName);

            string fixedName = LibraryNormalizer.FixEntryName(rawName);
            if (!list.Contains(fixedName)) list.Add(fixedName);

            bool allBytes = true;
            foreach (char c in rawName) if (c > 0xFF) { allBytes = false; break; }

            if (allBytes)
            {
                byte[] bytes = new byte[rawName.Length];
                for (int i = 0; i < rawName.Length; i++) bytes[i] = (byte)rawName[i];

                foreach (var enc in new[] { Cp1251, Cp866 })
                {
                    string s = enc.GetString(bytes);
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            return list;
        }

        /// <summary>
        /// Main entry point for handling book download requests
        /// </summary>
        public void HandleBookDownloadRequest(HttpProcessor processor, string request, string ext, bool acceptFB2)
        {
            try
            {
                string bookID = ExtractBookIdFromRequest(request);
                if (string.IsNullOrEmpty(bookID))
                {
                    Log.WriteLine(LogLevel.Warning, "Invalid book ID in download request: {0}", request);
                    processor.WriteBadRequest();
                    return;
                }

                Book book = Library.GetBook(bookID);
                if (book == null)
                {
                    Log.WriteLine(LogLevel.Warning, "Book {0} not found in library", bookID);
                    processor.WriteFailure();
                    return;
                }

                string downloadFormat = null;
                string downloadType = "download";

                if (request.Contains("/fb2") || request.Contains(".fb2.zip"))
                {
                    downloadFormat = "fb2";
                    HandleFB2Download(processor, book);
                }
                else if (request.Contains("/epub") || ext.Equals(".epub"))
                {
                    downloadFormat = "epub";
                    HandleEpubDownload(processor, book, acceptFB2);
                }
                else if (request.Contains("/mobi") || ext.Equals(".mobi"))
                {
                    downloadFormat = "mobi";
                    HandleMobiDownload(processor, book);
                }

                RecordDownload(bookID, downloadType, downloadFormat, processor);

                HttpServer.ServerStatistics.IncrementBooksSent();
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "Book download error: {0}", e.Message);
                processor.WriteFailure();
            }
        }

        /// <summary>
        /// Records download to database through Library
        /// </summary>
        private void RecordDownload(string bookId, string downloadType, string format, HttpProcessor processor)
        {
            try
            {
                string clientInfo = null;
                if (processor != null)
                {
                    string userAgent = processor.HttpHeaders.ContainsKey("User-Agent") ?
                        processor.HttpHeaders["User-Agent"] : null;
                    string clientIp = processor.RealClientIP;

                    if (!string.IsNullOrEmpty(userAgent) || !string.IsNullOrEmpty(clientIp))
                    {
                        clientInfo = string.Format("{0}|{1}",
                            userAgent ?? "Unknown",
                            clientIp ?? "Unknown");

                        if (clientInfo.Length > 255)
                        {
                            clientInfo = clientInfo.Substring(0, 255);
                        }
                    }
                }

                Library.RecordDownload(bookId, downloadType, format, clientInfo);

                Log.WriteLine(LogLevel.Info, "Recorded {0} for book {1}, format: {2}",
                    downloadType, bookId, format ?? "n/a");
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Warning, "Failed to record download for book {0}: {1}",
                    bookId, ex.Message);
            }
        }

        /// <summary>
        /// Extracts book ID from download request URL
        /// </summary>
        private string ExtractBookIdFromRequest(string request)
        {
            try
            {
                Log.WriteLine(LogLevel.Info, "Extracting book ID from request: {0}", request);

                if (request.StartsWith("/"))
                    request = request.Substring(1);

                string[] parts = request.Split('/');
                Log.WriteLine(LogLevel.Info, "Request parts: [{0}]", string.Join(", ", parts));

                if (parts.Length >= 3 && parts[0] == "download")
                {
                    string guid = parts[1];
                    guid = guid.Replace("%7B", "{").Replace("%7D", "}");

                    Log.WriteLine(LogLevel.Info, "Extracted book ID: {0}", guid);
                    return guid;
                }

                Log.WriteLine(LogLevel.Warning, "Could not extract book ID from request: {0}", request);
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Warning, "Error extracting book ID from {0}: {1}", request, ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Creates an ASCII-only file name for HTTP downloads and ZIP entries.
        /// </summary>
        private static string CreateSafeDownloadFileName(Book book, string extension)
        {
            string author = book.Authors?.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(author))
                author = "Unknown";

            string title = book.Title;

            if (string.IsNullOrWhiteSpace(title))
                title = "Book";

            string baseName = string.Format("{0}_{1}", author, title);
            baseName = Transliteration.Front(baseName);

            baseName = baseName
                .Replace('–', '-')
                .Replace('—', '-')
                .Replace('−', '-')
                .Replace('‐', '-')
                .Replace('‑', '-');

            baseName = Regex.Replace(baseName, @"[^A-Za-z0-9._-]+", "_");
            baseName = Regex.Replace(baseName, @"_+", "_");
            baseName = baseName.Trim('_', '.', '-');

            if (string.IsNullOrEmpty(baseName))
                baseName = "book";

            const int maximumBaseNameLength = 120;
            if (baseName.Length > maximumBaseNameLength)
            {
                baseName = baseName.Substring(0, maximumBaseNameLength);
                baseName = baseName.Trim('_', '.', '-');
            }

            return baseName + extension;
        }

        /// <summary>
        /// Handles FB2 book download (creates ZIP with FB2 inside)
        /// </summary>
        private void HandleFB2Download(HttpProcessor processor, Book book)
        {
            using (var memStream = new MemoryStream())
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    if (!ExtractBookContentWithCancellation(book, memStream, cts.Token))
                    {
                        processor.WriteFailure();
                        return;
                    }
                }

                using (var outputStream = new MemoryStream())
                {
                    using (var zipArchive = new System.IO.Compression.ZipArchive(outputStream, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        string fileName = CreateSafeDownloadFileName(book, ".fb2");

                        var zipEntry = zipArchive.CreateEntry(fileName);
                        using (var entryStream = zipEntry.Open())
                        {
                            memStream.Position = 0;
                            memStream.CopyTo(entryStream);
                        }
                    }

                    outputStream.Position = 0;

                    string downloadFileName = CreateSafeDownloadFileName(book, ".fb2.zip");

                    Log.WriteLine(
                        LogLevel.Info,
                        "Sending FB2 archive: HTTP filename={0}, size={1}",
                        downloadFileName,
                        outputStream.Length);

                    processor.OutputStream.WriteLine("HTTP/1.0 200 OK");
                    processor.OutputStream.WriteLine("Content-Type: application/fb2+zip");
                    processor.OutputStream.WriteLine("Content-Disposition: attachment; filename=\"{0}\"", downloadFileName);
                    processor.OutputStream.WriteLine("Content-Length: {0}", outputStream.Length);
                    processor.OutputStream.WriteLine("Connection: close");
                    processor.OutputStream.WriteLine();

                    outputStream.CopyTo(processor.OutputStream.BaseStream);
                    processor.OutputStream.BaseStream.Flush();
                }
            }
        }

        /// <summary>
        /// Handles EPUB book download (with FB2 to EPUB conversion when needed)
        /// </summary>
        private void HandleEpubDownload(HttpProcessor processor, Book book, bool acceptFB2)
        {
            using (var memStream = new MemoryStream())
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    if (!ExtractBookContentWithCancellation(book, memStream, cts.Token))
                    {
                        processor.WriteFailure();
                        return;
                    }
                }

                if (book.BookType == BookType.FB2)
                {
                    Log.WriteLine(LogLevel.Info, "Converting FB2 to EPUB for book: {0}", book.FileName);

                    if (!ConvertFB2ToEpub(book, memStream))
                    {
                        Log.WriteLine(LogLevel.Error, "Failed to convert FB2 to EPUB for book: {0}", book.FileName);
                        processor.WriteFailure();
                        return;
                    }
                }
                else if (book.BookType == BookType.EPUB)
                {
                    Log.WriteLine(LogLevel.Info, "Book is already in EPUB format: {0}", book.FileName);
                }

                string downloadFileName = CreateSafeDownloadFileName(book, ".epub");

                processor.OutputStream.WriteLine("HTTP/1.0 200 OK");
                processor.OutputStream.WriteLine("Content-Type: application/epub+zip");
                processor.OutputStream.WriteLine("Content-Disposition: attachment; filename=\"{0}\"", downloadFileName);
                processor.OutputStream.WriteLine("Content-Length: {0}", memStream.Length);
                processor.OutputStream.WriteLine("Connection: close");
                processor.OutputStream.WriteLine();

                memStream.Position = 0;
                memStream.CopyTo(processor.OutputStream.BaseStream);
                processor.OutputStream.BaseStream.Flush();

                Log.WriteLine(LogLevel.Info, "Successfully sent EPUB file: {0}", downloadFileName);
            }
        }

        /// <summary>
        /// Extracts book content from file or ZIP archive
        /// </summary>
        public bool ExtractBookContent(Book book, MemoryStream memStream)
        {
            return ExtractBookContentWithCancellation(book, memStream, CancellationToken.None);
        }

        /// <summary>
        /// Extracts book content with cancellation support
        /// </summary>
        public bool ExtractBookContentWithCancellation(Book book, MemoryStream memStream, CancellationToken cancellationToken)
        {
            try
            {
                string bookPath = book.FilePath;

                if (!Path.IsPathRooted(bookPath))
                {
                    string libraryPath = Properties.Settings.Default.LibraryPath;
                    if (!string.IsNullOrEmpty(libraryPath))
                    {
                        bookPath = Path.Combine(libraryPath, bookPath);
                    }
                }

                // Path traversal protection
                if (bookPath.Contains("..") || bookPath.Contains("~"))
                {
                    Log.WriteLine(LogLevel.Warning, "Suspicious path detected: {0}", bookPath);
                    return false;
                }

                Log.WriteLine(LogLevel.Info, "Attempting to extract book content from: {0}", bookPath);

                if (bookPath.ToLower().Contains(".zip@"))
                {
                    int sepIndex = bookPath.IndexOf(".zip@", StringComparison.OrdinalIgnoreCase);
                    string zipPath = bookPath.Substring(0, sepIndex + 4);
                    string targetEntry = bookPath.Substring(sepIndex + 5);

                    if (zipPath.Contains("..") || zipPath.Contains("~"))
                    {
                        Log.WriteLine(LogLevel.Warning, "Suspicious ZIP path detected: {0}", zipPath);
                        return false;
                    }

                    if (!File.Exists(zipPath))
                    {
                        Log.WriteLine(LogLevel.Warning, "ZIP archive not found: {0}", zipPath);
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    using (var zipArchive = System.IO.Compression.ZipFile.Open(zipPath,
                        System.IO.Compression.ZipArchiveMode.Read, LibraryNormalizer.Latin1))
                    using (var entryReader = new ZipEntryReader(zipPath))
                    {
                        var entry = zipArchive.Entries.FirstOrDefault(e =>
                            EntryNameVariants(e.FullName).Any(v =>
                                v.IndexOf(targetEntry, StringComparison.OrdinalIgnoreCase) >= 0));

                        if (entry != null)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            using (var entryStream = entryReader.Open(entry))
                            {
                                const int bufferSize = 81920;
                                byte[] buffer = new byte[bufferSize];
                                int bytesRead;

                                while ((bytesRead = entryStream.Read(buffer, 0, bufferSize)) > 0)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    memStream.Write(buffer, 0, bytesRead);
                                }

                                memStream.Position = 0;
                                Log.WriteLine(LogLevel.Info, "Successfully extracted book from ZIP: {0}",
                                    LibraryNormalizer.FixEntryName(entry.FullName));
                                return true;
                            }
                        }
                        else
                        {
                            Log.WriteLine(LogLevel.Warning, "Entry not found in ZIP: {0}", targetEntry);
                            return false;
                        }
                    }
                }
                else
                {
                    if (!File.Exists(bookPath))
                    {
                        Log.WriteLine(LogLevel.Warning, "Book file not found: {0}", bookPath);
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    using (var stream = new FileStream(bookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        const int bufferSize = 81920;
                        byte[] buffer = new byte[bufferSize];
                        int bytesRead;

                        while ((bytesRead = stream.Read(buffer, 0, bufferSize)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            memStream.Write(buffer, 0, bytesRead);
                        }

                        memStream.Position = 0;
                        Log.WriteLine(LogLevel.Info, "Successfully extracted book from file: {0}", bookPath);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.WriteLine(LogLevel.Info, "Book extraction cancelled for: {0}", book.FilePath);
                throw;
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Error, "Error extracting book content from {0}: {1}", book.FilePath, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Converts FB2 book to EPUB format
        /// </summary>
        private bool ConvertFB2ToEpub(Book book, MemoryStream memStream)
        {
            try
            {
                var converter = new FB2EpubConverter();

                using (var epubStream = new MemoryStream())
                {
                    memStream.Position = 0;
                    bool converted = converter.ConvertToEpubStream(book, memStream, epubStream);

                    if (converted && epubStream.Length > 0)
                    {
                        memStream.SetLength(0);
                        memStream.Position = 0;
                        epubStream.Position = 0;
                        epubStream.CopyTo(memStream);
                        memStream.Position = 0;

                        Log.WriteLine(LogLevel.Info, "Successfully converted {0} using FB2EpubConverter", book.FileName);
                        return true;
                    }
                    else
                    {
                        Log.WriteLine(LogLevel.Warning, "FB2EpubConverter failed for {0}", book.FileName);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Warning, "FB2EpubConverter error for {0}: {1}", book.FileName, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Handles MOBI download requests - converts FB2 to MOBI on the fly
        /// </summary>
        private void HandleMobiDownload(HttpProcessor processor, Book book)
        {
            try
            {
                if (book.BookType != BookType.FB2)
                {
                    Log.WriteLine(LogLevel.Warning, "MOBI conversion only supported for FB2 books. Book {0} is {1}",
                        book.FileName, book.BookType);
                    processor.WriteFailure();
                    return;
                }

                using (var memStream = new MemoryStream())
                {
                    if (!ExtractBookContent(book, memStream))
                    {
                        Log.WriteLine(LogLevel.Error, "Failed to extract book content for: {0}", book.FileName);
                        processor.WriteFailure();
                        return;
                    }

                    memStream.Position = 0;

                    using (var mobiStream = new MemoryStream())
                    {
                        var converter = new FB2MobiConverter();
                        bool success = converter.ConvertToMobiStream(book, memStream, mobiStream);

                        if (!success)
                        {
                            Log.WriteLine(LogLevel.Error, "Failed to convert {0} to MOBI", book.FileName);
                            processor.WriteFailure();
                            return;
                        }

                        mobiStream.Position = 0;

                        string fileName = CreateSafeDownloadFileName(book, ".mobi");

                        processor.OutputStream.WriteLine("HTTP/1.0 200 OK");
                        processor.OutputStream.WriteLine("Content-Type: application/x-mobipocket-ebook");
                        processor.OutputStream.WriteLine("Content-Disposition: attachment; filename=\"{0}\"", fileName);
                        processor.OutputStream.WriteLine("Content-Length: {0}", mobiStream.Length);
                        processor.OutputStream.WriteLine("Connection: close");
                        processor.OutputStream.WriteLine();

                        mobiStream.CopyTo(processor.OutputStream.BaseStream);
                        processor.OutputStream.BaseStream.Flush();

                        Log.WriteLine(LogLevel.Info, "Successfully served MOBI conversion for: {0}", book.Title);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log.WriteLine(LogLevel.Info, "MOBI conversion cancelled for: {0}", book.Title);
                throw;
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Error, "MOBI conversion error for {0}: {1}", book.FileName, ex.Message);
                processor.WriteFailure();
            }
        }
    }
}