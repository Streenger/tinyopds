using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TinyOPDS.Data
{
    /// <summary>
    /// Механическая починка нарушений XSD FictionBook 2.0.
    /// Не изменяет текст книги: только порядок элементов, обязательные
    /// поля метаданных, идентификаторы и вложенность блоков.
    /// Возвращает количество применённых правок.
    /// </summary>
    public static class Fb2Repair
    {
        private static readonly XNamespace fb = "http://www.gribuser.ru/xml/fictionbook/2.0";
        private static readonly XNamespace xlink = "http://www.w3.org/1999/xlink";

        // Канонический порядок детей по схеме FB2.0
        private static readonly string[] TitleInfoOrder = {
            "genre", "author", "book-title", "annotation", "keywords",
            "date", "coverpage", "lang", "src-lang", "translator", "sequence" };

        private static readonly string[] DocInfoOrder = {
            "author", "program-used", "date", "src-url", "src-ocr",
            "id", "version", "history", "publisher" };

        private static readonly string[] PubInfoOrder = {
            "book-name", "publisher", "city", "year", "isbn", "sequence" };

        private static readonly string[] AuthorOrder = {
            "first-name", "middle-name", "last-name", "nickname", "home-page", "email" };

        // Что разрешено внутри <p>
        private static readonly HashSet<string> InlineInP = new HashSet<string> {
            "strong", "emphasis", "style", "a", "strikethrough",
            "sub", "sup", "code", "image" };
        // Подставляется, когда в title-info нет ни одного жанра
        private const string DefaultGenre = "prose";

        // Подставляется, когда у автора нет ни фамилии, ни ника
        private const string UnknownAuthorLastName = "Сборник";

        // Что разрешено прямо в <body>
        private static readonly HashSet<string> BodyAllowed = new HashSet<string> {
            "image", "title", "epigraph", "section" };

        public static int Repair(XDocument doc)
        {
            int light;
            return Repair(doc, null, null, out light);
        }

        public static int Repair(XDocument doc, string idSeed, DateTime? fileDate)
        {
            int light;
            return Repair(doc, idSeed, fileDate, out light);
        }

        /// <summary>
        /// idSeed - строка, из которой строится идентификатор, если в книге его нет
        /// (null - не трогать id). fileDate - дата для document-info/date, если своей нет
        /// или она нечитаема (null - не трогать дату).
        /// </summary>
        public static int Repair(XDocument doc, string idSeed, DateTime? fileDate, out int lightFixes)
        {
            lightFixes = 0;
            if (doc == null || doc.Root == null) return 0;
            int fixes = 0;

            XElement desc = doc.Root.Element(fb + "description");
            if (desc != null)
            {
                // "Лёгкие" правки: только недостающие id/даты и порядок элементов описания.
                // Текст и структура книги не меняются, резервная копия для них не нужна.
                int light = EnsureDocumentInfo(desc, idSeed, fileDate);
                if (idSeed != null || fileDate != null) light += EnsureBookDate(desc);
                fixes += light;
                fixes += NormalizeGenres(desc);
                fixes += EnsureGenre(desc);
                fixes += FixAuthors(desc);
                int order = Reorder(desc.Element(fb + "title-info"), TitleInfoOrder);
                order += Reorder(desc.Element(fb + "src-title-info"), TitleInfoOrder);
                order += Reorder(desc.Element(fb + "document-info"), DocInfoOrder);
                order += Reorder(desc.Element(fb + "publish-info"), PubInfoOrder);
                fixes += order;
                lightFixes = light + order;
            }

            fixes += FixBinaryIds(doc);
            fixes += LiftBlocksFromParagraphs(doc);
            fixes += CleanBodyChildren(doc);
            return fixes;
        }

        // --- метаданные документа ----------------------------------------------

        /// <summary>
        /// document-info обязателен по схеме и должен содержать author, date, id, version.
        /// Недостающее добавляется: id строится из idSeed (UUID v5), дата берётся из fileDate.
        /// Существующие корректные значения не меняются.
        /// </summary>
        private static int EnsureDocumentInfo(XElement desc, string idSeed, DateTime? fileDate)
        {
            if (idSeed == null && fileDate == null) return 0;
            int fixes = 0;

            XElement info = desc.Element(fb + "document-info");
            if (info == null)
            {
                info = new XElement(fb + "document-info");
                XElement after = desc.Elements(fb + "src-title-info").LastOrDefault()
                              ?? desc.Elements(fb + "title-info").LastOrDefault();
                if (after != null) after.AddAfterSelf(info);
                else desc.AddFirst(info);
                fixes++;
            }

            if (idSeed != null)
            {
                XElement id = info.Element(fb + "id");
                string value = id == null ? null : id.Value.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    string guid = Utils.CreateGuid(Utils.IsoOidNamespace, idSeed).ToString();
                    if (id == null) info.Add(new XElement(fb + "id", guid));
                    else { id.RemoveNodes(); id.Value = guid; }
                    fixes++;
                }
            }

            if (fileDate != null)
            {
                XElement date = info.Element(fb + "date");
                if (date == null || !IsReadableDate(date))
                {
                    string iso = fileDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    XElement fresh = new XElement(fb + "date", new XAttribute("value", iso), iso);
                    if (date == null) info.Add(fresh); else date.ReplaceWith(fresh);
                    fixes++;
                }
            }

            // Обязательные author и version добавляем только если их нет вовсе
            if (info.Element(fb + "author") == null)
            {
                info.Add(new XElement(fb + "author", new XElement(fb + "nickname", "unknown")));
                fixes++;
            }
            if (info.Element(fb + "version") == null)
            {
                info.Add(new XElement(fb + "version", "1.0"));
                fixes++;
            }
            return fixes;
        }

        /// <summary>
        /// Если в title-info нет читаемой даты книги, а в publish-info есть правдоподобный год,
        /// записываем дату из года - так же, как это делает парсер при сканировании.
        /// Если года издания нет - ставится текущая дата (внесение в библиотеку).
        /// Текстовая дата без атрибута value (например "конец XVI века") сохраняется,
        /// добавляется только value.
        /// </summary>
        private static int EnsureBookDate(XElement desc)
        {
            XElement title = desc.Element(fb + "title-info");
            if (title == null) return 0;

            XElement date = title.Element(fb + "date");
            if (date != null && IsReadableDate(date)) return 0;

            XElement year = desc.Element(fb + "publish-info") == null
                ? null : desc.Element(fb + "publish-info").Element(fb + "year");
            int y;
            if (year == null || !int.TryParse(year.Value.Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out y) || y <= 1800 || y > DateTime.Now.Year)
            {
                // Года издания нет: ставим текущую дату (дата внесения книги в библиотеку)
                string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (date == null)
                {
                    title.Add(new XElement(fb + "date", new XAttribute("value", today), today));
                }
                else
                {
                    bool hasText2 = date.Value.Trim().Length > 0;
                    date.SetAttributeValue("value", today);
                    if (!hasText2) date.Value = today;
                }
                return 1;
            }

            string iso = y.ToString("0000", CultureInfo.InvariantCulture) + "-01-01";
            if (date == null)
            {
                XElement fresh = new XElement(fb + "date", new XAttribute("value", iso),
                    y.ToString(CultureInfo.InvariantCulture));
                // место по схеме определит Reorder
                title.Add(fresh);
            }
            else
            {
                bool hasText = date.Value.Trim().Length > 0;
                date.SetAttributeValue("value", iso);
                if (!hasText) date.Value = y.ToString(CultureInfo.InvariantCulture);
            }
            return 1;
        }

        // Дата читаема, если value или текст разбираются и год в разумных пределах
        private static bool IsReadableDate(XElement date)
        {
            string[] candidates = { (string)date.Attribute("value"), date.Value };
            foreach (string c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                DateTime d;
                if ((DateTime.TryParse(c.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                  || DateTime.TryParse(c.Trim(), new CultureInfo("ru-RU"), DateTimeStyles.None, out d))
                    && d.Year >= 1800 && d.Year <= DateTime.Now.Year + 10)
                    return true;

                // просто год ("1985") или дата вида "лето 1985"
                System.Text.RegularExpressions.Match m =
                    System.Text.RegularExpressions.Regex.Match(c, @"(?<!\d)(1[89]\d\d|20\d\d)(?!\d)");
                if (m.Success && int.Parse(m.Value) <= DateTime.Now.Year + 10) return true;
            }
            return false;
        }

        // --- порядок элементов -------------------------------------------------

        private static int Reorder(XElement parent, string[] order)
        {
            if (parent == null) return 0;
            List<XElement> kids = parent.Elements().ToList();
            if (kids.Count < 2) return 0;

            // OrderBy в LINQ стабилен: одноимённые элементы сохраняют исходный порядок
            List<XElement> sorted = kids.OrderBy(e => Rank(e.Name.LocalName, order)).ToList();

            bool changed = false;
            for (int i = 0; i < kids.Count; i++)
                if (!ReferenceEquals(kids[i], sorted[i])) { changed = true; break; }
            if (!changed) return 0;

            parent.ReplaceNodes(sorted);
            return 1;
        }

        private static int Rank(string name, string[] order)
        {
            int i = Array.IndexOf(order, name);
            return (i < 0) ? int.MaxValue : i;
        }
        /// <summary>
        /// Приводит теги жанров к канонической форме справочника FB2:
        /// нижний регистр, без пробелов по краям, подчёркивание вместо дефиса.
        /// В стандартном списке жанров дефиса нет ни в одном теге, поэтому
        /// замена безусловна и не может испортить корректное значение.
        /// Пустые <genre/> удаляются: иначе они обманывают EnsureGenre,
        /// и книга остаётся вообще без жанра.
        /// </summary>
        private static int NormalizeGenres(XElement desc)
        {
            if (desc == null) return 0;
            int fixes = 0;

            foreach (XElement ti in desc.Elements().ToList())
            {
                string block = ti.Name.LocalName;
                if (block != "title-info" && block != "src-title-info") continue;

                foreach (XElement g in ti.Elements().ToList())
                {
                    if (g.Name.LocalName != "genre") continue;
                    if (g.HasElements) continue;

                    string original = g.Value;

                    // пустой тег - удаляем, чтобы жанр подставился по общему правилу
                    if (string.IsNullOrWhiteSpace(original))
                    {
                        g.Remove();
                        fixes++;
                        continue;
                    }

                    string normalized = original.Trim().ToLowerInvariant().Replace('-', '_');

                    while (normalized.Contains("__"))
                        normalized = normalized.Replace("__", "_");

                    normalized = normalized.Trim('_');

                    if (normalized.Length == 0)
                    {
                        g.Remove();
                        fixes++;
                        continue;
                    }

                    if (!string.Equals(normalized, original, StringComparison.Ordinal))
                    {
                        g.Value = normalized;
                        fixes++;
                    }
                }
            }
            return fixes;
        }
        // --- жанр --------------------------------------------------------------

        /// <summary>
        /// Схема требует в title-info хотя бы один непустой genre, и IsValid
        /// проверяет то же самое. Ставим первым ребёнком: genre открывает
        /// канонический порядок title-info.
        /// </summary>
        private static int EnsureGenre(XElement desc)
        {
            XElement ti = desc.Element(fb + "title-info");
            if (ti == null) return 0;

            List<XElement> genres = ti.Elements(fb + "genre").ToList();

            if (genres.Any(g => !string.IsNullOrWhiteSpace(g.Value)))
                return 0;

            // убрать пустые заглушки <genre></genre>, если они есть
            foreach (XElement empty in genres) empty.Remove();

            ti.AddFirst(new XElement(fb + "genre", DefaultGenre));
            return 1;
        }

        // --- авторы ------------------------------------------------------------

        private static int FixAuthors(XElement desc)
        {
            int fixes = 0;
            foreach (XElement a in desc.Descendants(fb + "author").ToList())
            {
                // 1. Выбросить пустые части имени. Схема их формально допускает,
                //    но парсер по ним имя не соберёт, а пустой first-name
                //    маскирует отсутствие фамилии.
                foreach (XElement part in a.Elements().ToList())
                {
                    string n = part.Name.LocalName;
                    if ((n == "first-name" || n == "middle-name" ||
                         n == "last-name" || n == "nickname") &&
                        !part.HasElements && string.IsNullOrWhiteSpace(part.Value))
                    {
                        part.Remove();
                        fixes++;
                    }
                }

                bool hasLast = a.Element(fb + "last-name") != null;
                bool hasNick = a.Element(fb + "nickname") != null;

                if (!hasLast && !hasNick)
                {
                    XElement first = a.Element(fb + "first-name");

                    if (first != null && !string.IsNullOrWhiteSpace(first.Value))
                    {
                        // Одиночное имя без фамилии: переносим его в last-name.
                        // Схема после first-name требует middle-name или last-name,
                        // а строка, которую соберёт парсер, не меняется.
                        string value = first.Value;
                        first.Remove();
                        a.Add(new XElement(fb + "last-name", value));
                        fixes++;
                    }
                    else
                    {
                        a.Add(new XElement(fb + "last-name", UnknownAuthorLastName));
                        fixes++;
                    }
                }

                fixes += Reorder(a, AuthorOrder);
            }
            return fixes;
        }

        // --- идентификаторы картинок -------------------------------------------

        private static int FixBinaryIds(XDocument doc)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            foreach (XElement b in doc.Root.Elements(fb + "binary").ToList())
            {
                XAttribute id = b.Attribute("id");
                if (id == null || IsNcName(id.Value)) continue;

                string old = id.Value;
                if (map.ContainsKey(old)) { id.Value = map[old]; continue; }

                string neu = "i_" + SanitizeNcName(old);
                map[old] = neu;
                id.Value = neu;
            }

            if (map.Count == 0) return 0;

            // переписать ссылки xlink:href="#старый-id"
            foreach (XElement e in doc.Descendants())
            {
                XAttribute href = e.Attribute(xlink + "href");
                if (href == null || href.Value.Length < 2 || href.Value[0] != '#') continue;

                string neu;
                if (map.TryGetValue(href.Value.Substring(1), out neu))
                    href.Value = "#" + neu;
            }
            return map.Count;
        }

        private static bool IsNcName(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!char.IsLetter(s[0]) && s[0] != '_') return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_') return false;
            }
            return true;
        }

        private static string SanitizeNcName(string s)
        {
            char[] buf = s.ToCharArray();
            for (int i = 0; i < buf.Length; i++)
            {
                char c = buf[i];
                if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_') buf[i] = '_';
            }
            return new string(buf);
        }

        // --- блоки внутри <p> --------------------------------------------------

        private static int LiftBlocksFromParagraphs(XDocument doc)
        {
            int fixes = 0;
            foreach (XElement p in doc.Descendants(fb + "p").ToList())
            {
                if (p.Parent == null) continue;
                if (!p.Elements().Any(e => !InlineInP.Contains(e.Name.LocalName))) continue;

                List<XNode> pieces = new List<XNode>();
                XElement current = new XElement(fb + "p");

                foreach (XNode n in p.Nodes().ToList())
                {
                    XElement el = n as XElement;
                    if (el != null && !InlineInP.Contains(el.Name.LocalName))
                    {
                        if (HasContent(current)) pieces.Add(current);
                        current = new XElement(fb + "p");
                        el.Remove();
                        pieces.Add(el);
                    }
                    else
                    {
                        n.Remove();
                        current.Add(n);
                    }
                }
                if (HasContent(current)) pieces.Add(current);

                foreach (XNode piece in pieces) p.AddBeforeSelf(piece);
                p.Remove();
                fixes++;
            }
            return fixes;
        }

        private static bool HasContent(XElement e)
        {
            return e.HasElements || !string.IsNullOrWhiteSpace(e.Value);
        }

        // --- прямые дети <body> ------------------------------------------------

        private static int CleanBodyChildren(XDocument doc)
        {
            int fixes = 0;
            foreach (XElement body in doc.Root.Elements(fb + "body").ToList())
            {
                foreach (XElement child in body.Elements().ToList())
                {
                    string n = child.Name.LocalName;
                    if (BodyAllowed.Contains(n)) continue;

                    if (n == "empty-line") { child.Remove(); fixes++; continue; }

                    // прочее оборачиваем в секцию, чтобы не потерять содержимое
                    XElement sec = new XElement(fb + "section");
                    child.AddBeforeSelf(sec);
                    child.Remove();
                    sec.Add(child);
                    fixes++;
                }
            }
            return fixes;
        }
    }
}