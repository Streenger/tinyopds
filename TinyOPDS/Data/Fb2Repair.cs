using System;
using System.Collections.Generic;
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
            if (doc == null || doc.Root == null) return 0;
            int fixes = 0;

            XElement desc = doc.Root.Element(fb + "description");
            if (desc != null)
            {
                fixes += NormalizeGenres(desc);
                fixes += EnsureGenre(desc);
                fixes += FixAuthors(desc);
                fixes += Reorder(desc.Element(fb + "title-info"), TitleInfoOrder);
                fixes += Reorder(desc.Element(fb + "src-title-info"), TitleInfoOrder);
                fixes += Reorder(desc.Element(fb + "document-info"), DocInfoOrder);
                fixes += Reorder(desc.Element(fb + "publish-info"), PubInfoOrder);
            }

            fixes += FixBinaryIds(doc);
            fixes += LiftBlocksFromParagraphs(doc);
            fixes += CleanBodyChildren(doc);
            return fixes;
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