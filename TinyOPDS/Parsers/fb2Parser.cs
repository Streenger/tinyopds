/*
 * This file is part of TinyOPDS server project
 * https://github.com/sensboston/tinyopds
 *
 * Copyright (c) 2013-2025 SeNSSoFT
 * SPDX-License-Identifier: MIT
 *
 * Native FB2 parser without external dependencies - optimized version
 *
 */

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

using TinyOPDS.Data;

namespace TinyOPDS.Parsers
{
    public class FB2Parser : BookParser
    {
        private readonly XNamespace fb2Ns = "http://www.gribuser.ru/xml/fictionbook/2.0";

        public override Book Parse(Stream stream, string fileName)
        {
            Book book = new Book(fileName);

            try
            {
                if (stream.CanSeek)
                {
                    book.DocumentSize = (UInt32)stream.Length;
                }
                else
                {
                    book.DocumentSize = 0;
                }
            }
            catch
            {
                book.DocumentSize = 0;
            }

            try
            {
                byte[] buffer;
                long tProf = TinyOPDS.Scanner.ScanProfile.Now();
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    buffer = ms.ToArray();
                }
                TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2CopyTicks, tProf);

                XElement description = null;

                // Первая попытка: читаем как есть
                tProf = TinyOPDS.Scanner.ScanProfile.Now();
                try
                {
                    using (var testStream = new MemoryStream(buffer))
                    {
                        description = ReadDescriptionBlock(testStream, fileName);
                    }
                    TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2FirstReadTicks, tProf);
                }
                catch
                {
                    TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2FirstReadTicks, tProf);
                    tProf = TinyOPDS.Scanner.ScanProfile.Now();
                    System.Threading.Interlocked.Increment(ref TinyOPDS.Scanner.ScanProfile.Fb2SanitizeCount);

                    // Если упало — сразу чистим буфер через санитайзер (убираем ns0:, чиним теги)
                    string raw = Fb2Sanitizer.BytesToText(buffer);
                    int fixes;
                    string clean = Fb2Sanitizer.Sanitize(raw, out fixes);

                    buffer = Encoding.UTF8.GetBytes(clean);
                    if (fixes > 0)
                    {
                        Log.WriteLine(LogLevel.Info, "FB2Parser: auto-sanitized {0} ({1} fixes)", fileName, fixes);
                    }
                    TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2SanitizeTicks, tProf);
                }

                // Вторая попытка: читаем из очищенного буфера (если первая не удалась)
                if (description == null)
                {
                    tProf = TinyOPDS.Scanner.ScanProfile.Now();
                    using (var cleanStream = new MemoryStream(buffer))
                    {
                        description = ReadDescriptionBlock(cleanStream, fileName);
                    }
                    TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2SecondReadTicks, tProf);
                }

                if (description != null)
                {
                    tProf = TinyOPDS.Scanner.ScanProfile.Now();
                    bool authorUnknown = ParseDescriptionContent(description, book, fileName);
                    TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2DescTicks, tProf);

                    // Автора нет ни в title-info, ни среди составителей: в заголовке тела книги
                    // он обычно стоит перед названием (<title><p>Автор</p><p>Название</p></title>)
                    if (authorUnknown)
                    {
                        tProf = TinyOPDS.Scanner.ScanProfile.Now();
                        System.Threading.Interlocked.Increment(ref TinyOPDS.Scanner.ScanProfile.Fb2BodyAuthorCount);
                        string bodyAuthor = ExtractAuthorFromBodyTitle(buffer, book.Title);
                        TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2BodyAuthorTicks, tProf);
                        if (!string.IsNullOrEmpty(bodyAuthor))
                        {
                            book.Authors = new List<string> { bodyAuthor };
                            Log.WriteLine(LogLevel.Info, "Author taken from body title: \"{0}\" for {1}", bodyAuthor, fileName);
                        }
                    }
                }
                else
                {
                    Log.WriteLine(LogLevel.Warning, "No description block found in {0}", fileName);
                }
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "Book.Parse() exception {0} on file: {1}", e.Message, fileName);
            }

            long tDates = TinyOPDS.Scanner.ScanProfile.Now();
            EnsureValidDates(book, fileName);
            TinyOPDS.Scanner.ScanProfile.Since(ref TinyOPDS.Scanner.ScanProfile.Fb2DatesTicks, tDates);

            return book;
        }

        /// <summary>
        /// This method MUST be called before returning any Book object
        /// </summary>
        private void EnsureValidDates(Book book, string fileName)
        {
            DateTime fileDate = DateParser.GetFileDate(fileName);

            // Validate BookDate
            if (book.BookDate == DateTime.MinValue ||
                book.BookDate == default ||
                book.BookDate.Year <= 1 ||
                book.BookDate.Year < 1800 ||
                book.BookDate.Year > DateTime.Now.Year + 10)
            {
                book.BookDate = fileDate;
            }

            // Validate DocumentDate
            if (book.DocumentDate == DateTime.MinValue ||
                book.DocumentDate == default ||
                book.DocumentDate.Year <= 1)
            {
                Log.WriteLine(LogLevel.Info,
                    "Invalid or missing DocumentDate for {0}, using file date", fileName);
                book.DocumentDate = fileDate;
            }

            // Generate ID if missing
            if (string.IsNullOrEmpty(book.ID))
            {
                book.ID = Utils.CreateGuid(Utils.IsoOidNamespace, fileName).ToString();
                book.DocumentIDTrusted = false;
                Log.WriteLine(LogLevel.Info, "No document ID found in FB2, generated: {0} for file: {1}",
                    book.ID, fileName);
            }

            // Final safety check - should never happen after above validations
            if (book.BookDate.Year <= 1 || book.DocumentDate.Year <= 1)
            {
                Log.WriteLine(LogLevel.Error,
                    "CRITICAL: Date validation failed for {0}, forcing current date", fileName);
                book.BookDate = DateTime.Now;
                book.DocumentDate = DateTime.Now;
            }
        }

        private XElement ReadDescriptionBlock(
            Stream stream,
            string fileName)
        {
            try
            {
                // Санитизация потока «на лету» (удаление ns0:, выравнивание тегов и прочего мусора)
                if (stream != null && stream.CanRead)
                {
                    try
                    {
                        if (stream.CanSeek) stream.Position = 0;
                        byte[] buffer;
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            buffer = ms.ToArray();
                        }

                        string raw = Fb2Sanitizer.BytesToText(buffer);
                        int fixes;
                        string clean = Fb2Sanitizer.Sanitize(raw, out fixes);

                        if (fixes > 0)
                        {
                            buffer = Encoding.UTF8.GetBytes(clean);
                            Log.WriteLine(LogLevel.Info, "FB2Parser: auto-sanitized stream for {0} ({1} fixes)", fileName, fixes);
                        }

                        stream = new MemoryStream(buffer);
                    }
                    catch
                    {
                        // Если что-то пошло не так, оставляем поток как есть
                    }
                }

                if (stream == null || !stream.CanRead)
                {
                    Log.WriteLine(
                        LogLevel.Warning,
                        "FB2 stream is null or unreadable: {0}",
                        fileName);

                    return null;
                }

                if (!stream.CanSeek)
                {
                    Log.WriteLine(
                        LogLevel.Warning,
                        "FB2 stream does not support seeking: {0}",
                        fileName);

                    return null;
                }

                stream.Position = 0;

                Encoding encoding = DetectStreamEncoding(stream);

                stream.Position = 0;

                const int charBufferSize = 8192;
                const int maxDescriptionLength = 16 * 1024 * 1024;
                const string closingTag = "</description>";

                var contentBuilder = new StringBuilder();
                var charBuffer = new char[charBufferSize];

                bool descriptionFound = false;

                using (var reader = new StreamReader(
                    stream,
                    encoding,
                    true,
                    charBufferSize,
                    true))
                {
                    while (true)
                    {
                        int charsRead = reader.Read(charBuffer, 0, charBufferSize);
                        if (charsRead <= 0)
                        {
                            break;
                        }

                        if (!descriptionFound)
                        {
                            string chunk = new string(charBuffer, 0, charsRead);
                            int descStartIndex = chunk.IndexOf("description", StringComparison.OrdinalIgnoreCase);

                            if (descStartIndex >= 0)
                            {
                                int ltIndex = chunk.LastIndexOf('<', descStartIndex);
                                if (ltIndex >= 0)
                                {
                                    descriptionFound = true;
                                    contentBuilder.Append(charBuffer, ltIndex, charsRead - ltIndex);
                                }
                            }
                        }
                        else
                        {
                            contentBuilder.Append(charBuffer, 0, charsRead);
                        }

                        if (contentBuilder.Length > maxDescriptionLength)
                        {
                            Log.WriteLine(
                                LogLevel.Warning,
                                "Description block exceeds maximum length ({0} bytes) in {1}",
                                maxDescriptionLength,
                                fileName);

                            return null;
                        }

                        if (descriptionFound && contentBuilder.ToString().IndexOf(closingTag, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            break;
                        }
                    }
                }

                if (!descriptionFound)
                {
                    return null;
                }

                string xmlContent = contentBuilder.ToString();

                // Финальная страховка закрывающего тега
                if (!xmlContent.Contains("</description>") && xmlContent.Contains("</ns0:description>"))
                {
                    xmlContent = xmlContent.Replace("</ns0:description>", "</description>");
                }

                int closeIndex = xmlContent.IndexOf("</description>", StringComparison.OrdinalIgnoreCase);
                if (closeIndex >= 0)
                {
                    xmlContent = xmlContent.Substring(0, closeIndex + 14);
                }

                try
                {
                    // Объявления ищем именно в открывающем теге <description>: объявление
                    // xmlns:l внутри вложенного элемента (например, <p xmlns:l="..."> в аннотации)
                    // на остальное описание (<coverpage><image l:href=...>) не распространяется.
                    var descOpen = Regex.Match(xmlContent, @"<description(\s[^>]*)?>", RegexOptions.IgnoreCase);
                    string descOpenTag = descOpen.Success ? descOpen.Value : string.Empty;

                    if (!descOpenTag.Contains("xmlns:xlink="))
                    {
                        xmlContent = Regex.Replace(xmlContent, @"<description(\s|>)", "<description xmlns:xlink=\"http://www.w3.org/1999/xlink\"$1", RegexOptions.IgnoreCase);
                    }
                    if (!descOpenTag.Contains("xmlns:l="))
                    {
                        xmlContent = Regex.Replace(xmlContent, @"<description(\s|>)", "<description xmlns:l=\"http://www.w3.org/1999/xlink\"$1", RegexOptions.IgnoreCase);
                    }
                    if (!descOpenTag.Contains("xmlns="))
                    {
                        xmlContent = Regex.Replace(xmlContent, @"<description(\s|>)", "<description xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"$1", RegexOptions.IgnoreCase);
                    }

                    return XElement.Parse(xmlContent, LoadOptions.PreserveWhitespace);
                }
                catch (Exception e)
                {
                    Log.WriteLine(
                        LogLevel.Error,
                        "Failed to read description block for {0}: {1}",
                        fileName,
                        e.Message);

                    return null;
                }
            }
            catch (Exception e)
            {
                Log.WriteLine(
                    LogLevel.Error,
                    "Exception while reading description block for {0}: {1}",
                    fileName,
                    e.Message);

                return null;
            }
        }
        private string FixXmlEncoding(string xmlText)
        {
            // Remove BOM if present
            if (xmlText.Length > 0 && xmlText[0] == '\ufeff')
            {
                xmlText = xmlText.Substring(1);
            }

            // First decode HTML entities (like &#1041; -> Б)
            if (xmlText.Contains("&#"))
            {
                xmlText = Regex.Replace(xmlText, @"&#(\d+);",
                    match => ((char)int.Parse(match.Groups[1].Value)).ToString());
            }

            // Fix broken paragraph tags in annotations
            xmlText = FixBrokenParagraphs(xmlText);

            // Then escape unescaped ampersands (but not already escaped ones)
            xmlText = Regex.Replace(xmlText, @"&(?!(amp|lt|gt|quot|apos);)", "&amp;");

            return xmlText;
        }

        private string FixBrokenParagraphs(string xmlText)
        {
            // Find annotations and fix them
            int annotationStart = xmlText.IndexOf("<annotation>");
            if (annotationStart >= 0)
            {
                int annotationEnd = xmlText.IndexOf("</annotation>");
                if (annotationEnd > annotationStart)
                {
                    string before = xmlText.Substring(0, annotationStart);
                    string annotation = xmlText.Substring(annotationStart, annotationEnd - annotationStart + 13);
                    string after = xmlText.Substring(annotationEnd + 13);

                    // Fix broken paragraphs in annotation
                    annotation = Regex.Replace(annotation, @"<p>([^<]*(?:<(?!/?p|empty-line)[^>]*>[^<]*)*)<(?=p>|empty-line)", "<p>$1</p><");

                    xmlText = before + annotation + after;
                }
            }

            return xmlText;
        }
        private Encoding DetectStreamEncoding(Stream stream)
        {
            if (stream == null || !stream.CanRead)
                return Encoding.UTF8;

            long originalPosition = 0;

            if (stream.CanSeek)
                originalPosition = stream.Position;

            try
            {
                var buffer = new byte[1024];

                int bytesRead = stream.Read(
                    buffer,
                    0,
                    buffer.Length);

                if (bytesRead >= 3 &&
                    buffer[0] == 0xEF &&
                    buffer[1] == 0xBB &&
                    buffer[2] == 0xBF)
                {
                    return new UTF8Encoding(true);
                }

                if (bytesRead >= 2 &&
                    buffer[0] == 0xFF &&
                    buffer[1] == 0xFE)
                {
                    return Encoding.Unicode;
                }

                if (bytesRead >= 2 &&
                    buffer[0] == 0xFE &&
                    buffer[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode;
                }

                // Название кодировки в XML-декларации состоит из ASCII-символов,
                // поэтому декларацию можно безопасно искать через ASCII.
                string declaration = Encoding.ASCII.GetString(
                    buffer,
                    0,
                    bytesRead);

                return DetectEncoding(declaration);
            }
            catch
            {
                return Encoding.UTF8;
            }
            finally
            {
                if (stream.CanSeek)
                    stream.Position = originalPosition;
            }
        }
        private Encoding DetectEncoding(string xmlStart)
        {
            if (string.IsNullOrEmpty(xmlStart))
                return new UTF8Encoding(false);

            try
            {
                Match encodingMatch = Regex.Match(
                    xmlStart,
                    @"<\?xml[^>]*\bencoding\s*=\s*[""']" +
                    @"(?<encoding>[^""']+)[""']",
                    RegexOptions.IgnoreCase);

                if (!encodingMatch.Success)
                    return new UTF8Encoding(false);

                string encodingName = encodingMatch
                    .Groups["encoding"]
                    .Value
                    .Trim();

                if (string.IsNullOrEmpty(encodingName))
                    return new UTF8Encoding(false);

                return Encoding.GetEncoding(encodingName);
            }
            catch (Exception exception)
            {
                Log.WriteLine(
                    LogLevel.Warning,
                    "Unsupported FB2 encoding, using UTF-8: {0}",
                    exception.Message);

                return new UTF8Encoding(false);
            }
        }

        private bool ParseDescriptionContent(XElement description, Book book, string fileName)
        {
            bool authorUnknown = false;
            // Parse title-info FIRST (contains book date)
            var titleInfo = description.Element(fb2Ns + "title-info");
            if (titleInfo != null)
            {
                ParseTitleInfo(titleInfo, book, fileName);
            }

            // Parse document-info SECOND (contains document date, NOT book date)
            var documentInfo = description.Element(fb2Ns + "document-info");
            if (documentInfo != null)
            {
                ParseDocumentInfo(documentInfo, book, fileName);
            }

            // Parse publish-info THIRD (can override book date if more accurate)
            var publishInfo = description.Element(fb2Ns + "publish-info");
            if (publishInfo != null)
            {
                ParsePublishInfo(publishInfo, book, fileName);
            }

            // Нет автора в title-info (в <document-info> автор - это создатель fb2-файла, а не книги).
            // Без автора книга считалась бы невалидной и уходила в libmeta/bad.
            // Берём составителя из названия издания ("... / Составитель Сергей Алдонин"),
            // иначе - "Неизвестный автор".
            if (book.Authors == null || book.Authors.Count == 0)
            {
                string compiler = ExtractCompiler(publishInfo?.Element(fb2Ns + "book-name")?.Value)
                               ?? ExtractCompiler(description.Element(fb2Ns + "title-info")?.Element(fb2Ns + "book-title")?.Value);

                book.Authors = new List<string> { compiler ?? "Неизвестный автор" };
                authorUnknown = compiler == null;
                Log.WriteLine(LogLevel.Info, "No author in title-info, using \"{0}\" for {1}", book.Authors[0], fileName);
            }

            // NOTE: Date validation moved to EnsureValidDates() which is ALWAYS called
            return authorUnknown;
        }

        private static readonly Regex BodyTitlePattern = new Regex(
            @"<body[^>]*>\s*<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex ParagraphPattern = new Regex(
            @"<p[^>]*>(?<p>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex AnyTagPattern = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        private static string NormalizeForCompare(string text)
        {
            return new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        /// <summary>
        /// Автор из заголовка тела книги: <title><p>[картинка]</p><p>Автор</p><p>Название</p></title>.
        /// Берётся абзац непосредственно перед абзацем с названием книги.
        /// </summary>
        private string ExtractAuthorFromBodyTitle(byte[] buffer, string bookTitle)
        {
            try
            {
                string key = NormalizeForCompare(bookTitle);
                if (key.Length == 0) return null;

                Encoding encoding;
                using (var ms = new MemoryStream(buffer))
                {
                    encoding = DetectStreamEncoding(ms);
                }

                // тело стоит сразу после description; читаем с запасом, бинарники лежат в конце
                int len = Math.Min(buffer.Length, 4 * 1024 * 1024);
                string text = encoding.GetString(buffer, 0, len);
                int descEnd = text.IndexOf("</description>", StringComparison.OrdinalIgnoreCase);
                if (descEnd < 0) return null;

                var m = BodyTitlePattern.Match(text, descEnd);
                if (!m.Success) return null;

                var paragraphs = new List<string>();
                foreach (Match pm in ParagraphPattern.Matches(m.Groups["t"].Value))
                {
                    string t = System.Net.WebUtility.HtmlDecode(AnyTagPattern.Replace(pm.Groups["p"].Value, " "));
                    t = Regex.Replace(t, @"\s+", " ").Trim();
                    if (t.Length > 0) paragraphs.Add(t);
                }

                for (int i = 1; i < paragraphs.Count; i++)
                {
                    string cmp = NormalizeForCompare(paragraphs[i]);
                    if (cmp.Length == 0 || (!cmp.Contains(key) && !key.Contains(cmp))) continue;

                    string candidate = paragraphs[i - 1];
                    var words = candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 0 || words.Length > 4 || candidate.Length > 60) return null;
                    if (candidate.Any(char.IsDigit)) return null;
                    if (candidate.IndexOf("составител", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                    if (NormalizeForCompare(candidate) == key) return null;

                    var parts = words.Select(w => (w.Length > 2 ? w.TrimEnd('.') : w).Capitalize()).ToList();
                    if (parts.Count == 2) return parts[1] + " " + parts[0];
                    if (parts.Count == 3) return parts[2] + " " + parts[0] + " " + parts[1];
                    return string.Join(" ", parts);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Warning, "Author lookup in body title failed: {0}", ex.Message);
            }
            return null;
        }

        private static readonly Regex CompilerPattern = new Regex(
            @"(?:автор[\s\-]*)?составител[ьяи]\w*[\s:\-]+(?<name>(?-i:[А-ЯЁA-Z][\p{L}\.\-]*(?:\s+[А-ЯЁA-Z][\p{L}\.\-]*){0,2}))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// "Составитель Сергей Алдонин" -> "Алдонин Сергей" (порядок как в ParsePersonName).
        /// </summary>
        private static string ExtractCompiler(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var m = CompilerPattern.Match(text);
            if (!m.Success) return null;

            var words = m.Groups["name"].Value
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => (w.Length > 2 ? w.TrimEnd('.') : w).Trim().Capitalize())
                .ToList();
            if (words.Count == 0) return null;

            // Имя Фамилия -> Фамилия Имя; Имя Отчество Фамилия -> Фамилия Имя Отчество
            if (words.Count == 2) return words[1] + " " + words[0];
            if (words.Count == 3) return words[2] + " " + words[0] + " " + words[1];
            return string.Join(" ", words);
        }

        private void ParseTitleInfo(XElement titleInfo, Book book, string fileName)
        {
            // Genres
            var genres = titleInfo.Elements(fb2Ns + "genre").Select(g => g.Value).ToList();
            if (genres.Any())
            {
                book.Genres = genres;
            }

            // Authors
            var authors = titleInfo.Elements(fb2Ns + "author");
            if (authors.Any())
            {
                book.Authors = new List<string>();
                foreach (var author in authors)
                {
                    string authorName = ParsePersonName(author);
                    if (!string.IsNullOrEmpty(authorName))
                    {
                        book.Authors.Add(authorName);
                    }
                }
            }

            // Book title
            var bookTitle = titleInfo.Element(fb2Ns + "book-title")?.Value;
            if (!string.IsNullOrEmpty(bookTitle))
            {
                book.Title = bookTitle;
            }

            // Annotation
            var annotation = titleInfo.Element(fb2Ns + "annotation");
            if (annotation != null)
            {
                book.Annotation = ExtractTextFromAnnotation(annotation);
            }

            // Date - this is the BOOK creation date
            var date = titleInfo.Element(fb2Ns + "date");
            if (date != null)
            {
                book.BookDate = DateParser.ParseFB2Date(date, fileName);
            }
            // If no date, EnsureValidDates() will handle it

            // Language
            var lang = titleInfo.Element(fb2Ns + "lang")?.Value;
            if (!string.IsNullOrEmpty(lang))
            {
                book.Language = lang;
            }

            // Translators
            var translators = titleInfo.Elements(fb2Ns + "translator");
            if (translators.Any())
            {
                book.Translators = new List<string>();
                foreach (var translator in translators)
                {
                    string translatorName = ParsePersonName(translator);
                    if (!string.IsNullOrEmpty(translatorName))
                    {
                        book.Translators.Add(translatorName);
                    }
                }
            }

            // Sequences (series)
            var sequences = titleInfo.Elements(fb2Ns + "sequence");
            if (sequences.Any())
            {
                var firstSequence = sequences.First();
                var seqName = firstSequence.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(seqName))
                {
                    book.Sequence = seqName.Capitalize(true);

                    var seqNumber = firstSequence.Attribute("number")?.Value;
                    if (!string.IsNullOrEmpty(seqNumber) && uint.TryParse(seqNumber, out uint num))
                    {
                        book.NumberInSequence = num;
                    }
                }
            }

            var coverpage = titleInfo.Element(fb2Ns + "coverpage");
        }

        private void ParseDocumentInfo(XElement documentInfo, Book book, string fileName)
        {
            // Extract document ID from FB2
            var documentId = documentInfo.Element(fb2Ns + "id")?.Value;
            if (!string.IsNullOrEmpty(documentId))
            {
                // Clean up the ID (remove any whitespace or extra characters)
                documentId = documentId.Trim();

                // Set the ID - the setter in Book.cs will validate if it's a trusted GUID
                book.ID = documentId;
            }

            // Document date - FIXED: use DocumentDate, NOT BookDate!
            var date = documentInfo.Element(fb2Ns + "date");
            if (date != null)
            {
                book.DocumentDate = DateParser.ParseFB2Date(date, fileName);
            }
            // If no date, EnsureValidDates() will handle it

            // Version
            var version = documentInfo.Element(fb2Ns + "version")?.Value;
            if (!string.IsNullOrEmpty(version) && float.TryParse(version, NumberStyles.Any, CultureInfo.InvariantCulture, out float ver))
            {
                book.Version = ver;
            }
        }

        private void ParsePublishInfo(XElement publishInfo, Book book, string fileName)
        {
            // Year from publish-info can be more accurate than from title-info
            var year = publishInfo.Element(fb2Ns + "year")?.Value;
            if (!string.IsNullOrEmpty(year) && int.TryParse(year, out int yearNum))
            {
                // Validate the year
                if (yearNum > 1800 && yearNum <= DateTime.Now.Year)
                {
                    // Use publish year if book date is clearly invalid
                    if (book.BookDate == DateTime.MinValue ||
                        book.BookDate == default ||
                        book.BookDate.Year <= 1)
                    {
                        book.BookDate = new DateTime(yearNum, 1, 1);
                        Log.WriteLine(LogLevel.Info, "Using publish year {0} for book date in {1}", yearNum, fileName);
                    }
                    // Or if publish year seems more reasonable than parsed date
                    else if (book.BookDate.Year < 1800 || book.BookDate.Year > DateTime.Now.Year + 10)
                    {
                        book.BookDate = new DateTime(yearNum, 1, 1);
                        Log.WriteLine(LogLevel.Info, "Replacing suspicious book date with publish year {0} in {1}", yearNum, fileName);
                    }
                }
            }

            // Publisher and ISBN could be stored if Book class had these fields
            // For now, we just parse them for validation
            _ = publishInfo.Element(fb2Ns + "publisher")?.Value;
            _ = publishInfo.Element(fb2Ns + "isbn")?.Value;
        }

        private string ParsePersonName(XElement person)
        {
            var firstName = person.Element(fb2Ns + "first-name")?.Value ?? "";
            var middleName = person.Element(fb2Ns + "middle-name")?.Value ?? "";
            var lastName = person.Element(fb2Ns + "last-name")?.Value ?? "";
            var nickName = person.Element(fb2Ns + "nickname")?.Value ?? "";

            // Build name in format: LastName FirstName MiddleName
            var nameParts = new List<string>();

            if (!string.IsNullOrEmpty(lastName))
            {
                nameParts.Add(lastName.Trim().Capitalize());
            }

            if (!string.IsNullOrEmpty(firstName))
            {
                nameParts.Add(firstName.Trim().Capitalize());
            }

            if (!string.IsNullOrEmpty(middleName))
            {
                nameParts.Add(middleName.Trim().Capitalize());
            }

            // Use nickname if no other name parts
            if (nameParts.Count == 0 && !string.IsNullOrEmpty(nickName))
            {
                nameParts.Add(nickName.Trim().Capitalize());
            }

            return string.Join(" ", nameParts);
        }

        private string ExtractTextFromAnnotation(XElement annotation)
        {
            var sb = new StringBuilder();

            foreach (var node in annotation.DescendantNodes())
            {
                if (node is XText textNode)
                {
                    sb.Append(textNode.Value);
                }
            }

            return sb.ToString().Trim();
        }

        public override Image GetCoverImage(Stream stream, string fileName)
        {
            Image image = null;
            try
            {
                XDocument xml = null;

                // Parse full file for cover extraction
                if (!stream.CanSeek)
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        stream.CopyTo(memoryStream);
                        memoryStream.Position = 0;
                        xml = ParseFullDocument(memoryStream, fileName);
                    }
                }
                else
                {
                    stream.Position = 0;
                    xml = ParseFullDocument(stream, fileName);
                }

                if (xml == null) return null;

                // Get namespaces from document
                XNamespace fb2Namespace = xml.Root?.Name.Namespace ?? XNamespace.None;
                XNamespace xlinkNs = "http://www.w3.org/1999/xlink";

                // Find coverpage in title-info
                var titleInfo = xml.Root?.Element(fb2Namespace + "description")?.Element(fb2Namespace + "title-info");
                if (titleInfo == null) return null;

                var coverpage = titleInfo.Element(fb2Namespace + "coverpage");
                if (coverpage == null) return null;

                // Get image href - FB2 uses xlink:href
                var imageElement = coverpage.Element(fb2Namespace + "image");
                if (imageElement == null) return null;

                string href = imageElement.Attribute(xlinkNs + "href")?.Value ??
                             imageElement.Attribute("href")?.Value ?? // fallback for non-standard FB2
                             imageElement.Attribute("l:href")?.Value; // sometimes used in broken files

                if (string.IsNullOrEmpty(href)) return null;

                // Remove # prefix if present
                if (href.StartsWith("#"))
                    href = href.Substring(1);

                // Find binary element with this id
                var binaries = xml.Root.Elements(fb2Namespace + "binary");
                foreach (var binary in binaries)
                {
                    var id = binary.Attribute("id")?.Value;
                    if (id == href)
                    {
                        // Get content type if available
                        var contentType = binary.Attribute("content-type")?.Value ?? "image/jpeg";

                        // Decode base64 image
                        string base64Data = binary.Value.Replace("\r", "").Replace("\n", "").Replace(" ", "");

                        try
                        {
                            byte[] imageData = Convert.FromBase64String(base64Data);

                            using (var memStream = new MemoryStream(imageData))
                            {
                                image = Image.FromStream(memStream);

                                // Resize to standard cover size
                                image = image.Resize(CoverImage.CoverSize);
                            }
                        }
                        catch (FormatException)
                        {
                            Log.WriteLine(LogLevel.Warning, "Invalid base64 image data for {0}", fileName);
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.WriteLine(LogLevel.Error, "GetCoverImage() exception {0} on file: {1}", e.Message, fileName);
            }
            return image;
        }

        private XDocument ParseFullDocument(Stream stream, string fileName)
        {
            try
            {
                // Simple full document parsing for cover extraction
                stream.Position = 0;
                return XDocument.Load(stream);
            }
            catch
            {
                // Try with encoding detection
                try
                {
                    stream.Position = 0;
                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, Math.Min(buffer.Length, (int)stream.Length));
                    string start = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    Encoding encoding = DetectEncoding(start);

                    stream.Position = 0;
                    using (var reader = new StreamReader(stream, encoding, false, 1024, true))
                    {
                        string xmlStr = reader.ReadToEnd();
                        return XDocument.Parse(xmlStr, LoadOptions.PreserveWhitespace);
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLine(LogLevel.Warning, "Error parsing full document for {0}: {1}", fileName, ex.Message);
                    return null;
                }
            }
        }
    }
}