using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TinyOPDS.Data
{
    /// <summary>
    /// Механическая починка синтаксиса XML в файлах, которые XDocument
    /// не может разобрать вовсе. Работает с текстом, не с деревом.
    /// Правки только те, что не меняют смысл содержимого:
    /// управляющие символы, голые амперсанды, HTML-сущности, шальные "менее".
    /// </summary>
    internal static class Fb2Sanitizer
    {
        // Элементы FB2 2.0 - при них "<" считается началом тега
        private static readonly HashSet<string> KnownTags =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FictionBook", "description", "title-info", "src-title-info",
            "document-info", "publish-info", "custom-info", "output",
            "genre", "author", "first-name", "middle-name", "last-name",
            "nickname", "home-page", "email", "id", "book-title", "annotation",
            "keywords", "date", "coverpage", "lang", "src-lang", "translator",
            "sequence", "program-used", "version", "history", "src-url", "src-ocr",
            "publisher", "book-name", "city", "year", "isbn",
            "body", "title", "epigraph", "section", "p", "empty-line", "subtitle",
            "cite", "text-author", "poem", "stanza", "v",
            "table", "tr", "td", "th",
            "strong", "emphasis", "style", "a", "strikethrough",
            "sub", "sup", "code", "image", "binary", "part", "stylesheet",
            // HTML-мусор: НЕ чиним, но и не портим - такой файл честно пропустим
            "b", "i", "u", "em", "br", "center", "div", "span", "font",
            "ul", "ol", "li", "dl", "dt", "dd", "hr", "img", "pre", "tt",
            "small", "big", "s", "strike", "nobr", "blockquote",
            "h1", "h2", "h3", "h4", "h5", "h6", "html", "head", "meta", "link"
        };

        // Сущности HTML, которых нет в XML. Заменяем на числовые ссылки:
        // так безопасно для любой кодировки записи.
        private static readonly Dictionary<string, string> Entities =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            {"nbsp","160"},   {"trade","8482"}, {"copy","169"},   {"reg","174"},
            {"mdash","8212"}, {"ndash","8211"}, {"laquo","171"},  {"raquo","187"},
            {"hellip","8230"},{"deg","176"},    {"plusmn","177"}, {"sect","167"},
            {"para","182"},   {"middot","183"}, {"bull","8226"},  {"ldquo","8220"},
            {"rdquo","8221"}, {"lsquo","8216"}, {"rsquo","8217"}, {"sbquo","8218"},
            {"bdquo","8222"}, {"times","215"},  {"divide","247"}, {"minus","8722"},
            {"frac12","189"}, {"frac14","188"}, {"frac34","190"},
            {"sup2","178"},   {"sup3","179"},   {"euro","8364"},  {"pound","163"},
            {"yen","165"},    {"cent","162"},   {"dagger","8224"},{"Dagger","8225"},
            {"permil","8240"},{"prime","8242"}, {"Prime","8243"}, {"ne","8800"},
            {"le","8804"},    {"ge","8805"},    {"infin","8734"}, {"larr","8592"},
            {"rarr","8594"},  {"uarr","8593"},  {"darr","8595"},  {"alpha","945"},
            {"beta","946"},   {"gamma","947"},  {"pi","960"},     {"micro","181"},
            {"shy","173"},    {"ensp","8194"},  {"emsp","8195"},  {"thinsp","8201"}
        };

        // --- декодирование байтов ---------------------------------------------

        private static readonly Regex DeclEncoding = new Regex(
            @"encoding\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);

        /// <summary>
        /// Байты -> текст с учётом BOM и объявленной кодировки.
        /// </summary>
        public static string BytesToText(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return new UTF8Encoding(false).GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);

            string declared = ReadDeclaredEncoding(data);

            // Объявленную кодировку проверяем строго: если объявлен utf-8,
            // а байты на самом деле cp1251, молча получим кракозябры.
            if (declared == null || declared.IndexOf("utf", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { return new UTF8Encoding(false, true).GetString(data); }
                catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(data); }
            }

            try { return Encoding.GetEncoding(declared).GetString(data); }
            catch { return Encoding.GetEncoding(1251).GetString(data); }
        }

        private static string ReadDeclaredEncoding(byte[] data)
        {
            int n = Math.Min(data.Length, 512);
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(data[i] < 0x80 ? (char)data[i] : ' ');
            Match m = DeclEncoding.Match(sb.ToString());
            return m.Success ? m.Groups[1].Value : null;
        }

        // --- санация ----------------------------------------------------------

        public static string Sanitize(string text, out int fixes)
        {
            int count = 0;
            text = StripNameSpaces(text, ref count);

            if (text.Contains("</FictionBook>") == false && text.Contains("</ns0:FictionBook>"))
            {
                text = text.Replace("</ns0:FictionBook>", "</FictionBook>");
                count++;
            }
            // Если используется префикс xlink, но в корневом теге FictionBook не объявлен namespace xlink — добавляем
            if (text.Contains("xlink:") && !text.Contains("xmlns:xlink"))
            {
                text = text.Replace("<FictionBook", "<FictionBook xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
                count++;
            }
            // Автоматическое исправление поврежденной кириллицы на лету
            string fixedText = FixCorruptedCyrillic(text);
            if (fixedText != text)
            {
                text = fixedText;
                count++;
            }
            // Удаляем пустые или некорректные теги серий вида <sequence name="" number="" />
            text = Regex.Replace(text, @"<sequence\s+name\s*=\s*""\s*""\s+number\s*=\s*""\s*""\s*/?>", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<sequence\s+number\s*=\s*""\s*""\s+name\s*=\s*""\s*""\s*/?>", "", RegexOptions.IgnoreCase);
            // Если заголовок книги состоит только из знаков вопроса или пустой — подстраховываем
            text = Regex.Replace(text, @"<book-title>\?[?,\s]*</book-title>", "<book-title>Неизвестная книга</book-title>", RegexOptions.IgnoreCase);
            text = StripControlChars(text, ref count);
            text = FixAmpersands(text, ref count);
            text = EscapeStrayLt(text, ref count);
            fixes = count;
            return text;
        }
        private static readonly Regex NsPrefixPattern = new Regex(@"(</?\s*)[a-zA-Z0-9_-]+:", RegexOptions.Compiled);

        /// <summary>
        /// Удаляет мусорные префиксы пространств имён (например, <ns0:tag> -> <tag> и </ns0:tag> -> </tag>)
        /// </summary>
        private static string StripNameSpaces(string text, ref int fixes)
        {
            int local = 0;
            string result = NsPrefixPattern.Replace(text, m =>
            {
                local++;
                return m.Groups[1].Value; // Оставляем только скобку "<" или "</"
            });
            fixes += local;
            return result;
        }
        /// <summary>Управляющие символы, запрещённые в XML 1.0 при любой схеме.</summary>
        private static string StripControlChars(string text, ref int fixes)
        {
            bool need = false;
            for (int i = 0; i < text.Length; i++)
                if (IsForbidden(text[i])) { need = true; break; }
            if (!need) return text;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (IsForbidden(c)) { fixes++; continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsForbidden(char c)
        {
            if (c == '\t' || c == '\n' || c == '\r') return false;
            if (c < 0x20) return true;
            return c == '\uFFFE' || c == '\uFFFF';
        }

        private static readonly Regex AmpPattern = new Regex(
            @"&(?:#[0-9]+;|#x[0-9A-Fa-f]+;|([A-Za-z][A-Za-z0-9]{0,12});)?",
            RegexOptions.Compiled);

        private static string FixAmpersands(string text, ref int fixes)
        {
            int local = 0;
            string result = AmpPattern.Replace(text, m =>
            {
                // одинокий "&" без ссылки
                if (m.Length == 1) { local++; return "&amp;"; }

                // числовая ссылка - оставляем
                if (!m.Groups[1].Success) return m.Value;

                string name = m.Groups[1].Value;
                if (name == "amp" || name == "lt" || name == "gt" ||
                    name == "quot" || name == "apos") return m.Value;

                string code;
                if (Entities.TryGetValue(name, out code)) { local++; return "&#" + code + ";"; }

                // незнакомая сущность: это был обычный амперсанд в тексте
                local++;
                return "&amp;" + name + ";";
            });
            fixes += local;
            return result;
        }

        private static readonly Regex LtPattern = new Regex(
            @"<(/?)([^\s<>/]*)", RegexOptions.Compiled);

        /// <summary>
        /// Экранирует "&lt;", который не может начинать тег FB2.
        /// Проверяется не только имя, но и форма конструкции целиком:
        /// "&lt;имя атрибут="значение" ... &gt;" либо "&lt;/имя&gt;".
        /// Если после известного имени идёт нечто, не похожее на атрибуты,
        /// это текст, а не разметка - иначе парсер уйдёт в режим тега
        /// и упадёт на первом же русском слове.
        /// </summary>
        private static string EscapeStrayLt(string text, ref int fixes)
        {
            int local = 0;
            string src = text;

            string result = LtPattern.Replace(text, m =>
            {
                string name = m.Groups[2].Value;


                // <?xml ... ?>   <!-- ... -->   <![CDATA[ ... ]]>   <!DOCTYPE ...>
                // Проверяем форму целиком: одинокий "<?" в тексте книги
                // парсер примет за инструкцию обработки и упадёт.
                if (name.Length > 0 && (name[0] == '?' || name[0] == '!'))
                {
                    if (LooksLikeSpecial(src, m.Index)) return m.Value;
                    local++;
                    return "&lt;" + m.Value.Substring(1);
                }
                // "<" + пробел, цифра, точка, дефис, кавычка, "$" и прочее
                if (name.Length == 0) { local++; return "&lt;" + m.Value.Substring(1); }
                if (!char.IsLetter(name[0]) && name[0] != '_')
                { local++; return "&lt;" + m.Value.Substring(1); }

                // имя тега с кириллицей: в FB2 таких нет, значит это текст
                bool ascii = true;
                foreach (char c in name) if (c > 0x7F) { ascii = false; break; }
                if (!ascii) { local++; return "&lt;" + m.Value.Substring(1); }

                if (KnownTags.Contains(name))
                {
                    bool closing = m.Groups[1].Length > 0;
                    if (LooksLikeTag(src, m.Index + m.Length, closing)) return m.Value;

                    // имя знакомое, но продолжение на тег не похоже
                    local++;
                    return "&lt;" + m.Value.Substring(1);
                }

                local++;
                return "&lt;" + m.Value.Substring(1);
            });

            fixes += local;
            return result;
        }

        // Дальше этого предела продолжение тега не ищем: настоящих
        // открывающих тегов такой длины в FB2 не бывает.
        private const int MaxTagTail = 4096;

        /// <summary>
        /// Проверяет служебную конструкцию, начинающуюся с "&lt;?" или "&lt;!":
        /// требуется корректное начало И наличие закрывающей последовательности.
        /// </summary>
        private static bool LooksLikeSpecial(string s, int lt)
        {
            if (lt + 1 >= s.Length) return false;

            if (s[lt + 1] == '?')
            {
                int i = lt + 2;
                if (i >= s.Length) return false;
                char f = s[i];
                bool okName = (f >= 'A' && f <= 'Z') || (f >= 'a' && f <= 'z') || f == '_';
                if (!okName) return false;
                int end = s.IndexOf("?>", i, StringComparison.Ordinal);
                return end >= 0 && end - lt <= MaxTagTail;
            }

            if (StartsWithAt(s, lt, "<!--"))
                return s.IndexOf("-->", lt + 4, StringComparison.Ordinal) >= 0;

            if (StartsWithAt(s, lt, "<![CDATA["))
                return s.IndexOf("]]>", lt + 9, StringComparison.Ordinal) >= 0;

            if (StartsWithAt(s, lt, "<!DOCTYPE"))
                return s.IndexOf('>', lt + 9) >= 0;

            return false;
        }

        private static bool StartsWithAt(string s, int pos, string what)
        {
            if (pos < 0 || pos + what.Length > s.Length) return false;
            for (int k = 0; k < what.Length; k++)
                if (s[pos + k] != what[k]) return false;
            return true;
        }
        /// <summary>
        /// Проверяет, что начиная с pos идёт корректное продолжение тега:
        /// список атрибутов вида имя="значение" и закрывающая скобка.
        /// </summary>
        private static bool LooksLikeTag(string s, int pos, bool closing)
        {
            int limit = Math.Min(s.Length, pos + MaxTagTail);
            int i = pos;

            if (closing)
            {
                while (i < limit && IsSpace(s[i])) i++;
                return i < limit && s[i] == '>';
            }

            while (i < limit)
            {
                while (i < limit && IsSpace(s[i])) i++;
                if (i >= limit) return false;

                if (s[i] == '>') return true;

                // пустой элемент: "/>" строго без пробела между символами
                if (s[i] == '/')
                    return i + 1 < limit && s[i + 1] == '>';

                // новый "<" раньше, чем ">" - значит закрывающей скобки нет
                if (s[i] == '<') return false;

                // имя атрибута: начинается с латинской буквы или "_"
                char f = s[i];
                bool okFirst = (f >= 'A' && f <= 'Z') || (f >= 'a' && f <= 'z') || f == '_';
                if (!okFirst) return false;

                i++;
                while (i < limit && IsAttrNameChar(s[i])) i++;

                while (i < limit && IsSpace(s[i])) i++;
                if (i >= limit || s[i] != '=') return false;
                i++;

                while (i < limit && IsSpace(s[i])) i++;
                if (i >= limit) return false;

                char q = s[i];
                if (q != '"' && q != '\'') return false;

                int close = s.IndexOf(q, i + 1);
                if (close < 0 || close >= limit) return false;
                i = close + 1;
            }
            return false;
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r';
        }

        private static bool IsAttrNameChar(char c)
        {
            if (c >= 'A' && c <= 'Z') return true;
            if (c >= 'a' && c <= 'z') return true;
            if (c >= '0' && c <= '9') return true;
            return c == '_' || c == '-' || c == ':' || c == '.';
        }
        // --- Восстановление поврежденной кириллицы (кракозябров и знаков ?) ---

        private static readonly Regex CyrillicCheck = new Regex(@"[а-яА-ЯёЁ]", RegexOptions.Compiled);

        /// <summary>
        /// Пытается восстановить текст, превратившийся в знаки вопроса или кракозябры,
        /// перебирая стандартные русские кодировки.
        /// </summary>
        public static string FixCorruptedCyrillic(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Если в тексте нет знаков вопроса и он уже содержит кириллицу — он в порядке
            if (!text.Contains('?') && CyrillicCheck.IsMatch(text)) return text;

            // Если слишком много знаков вопроса, пробуем восстановить из UTF-8/ANSI сдвигов
            Encoding[] encodingsToTry = new Encoding[]
            {
                Encoding.GetEncoding(1251), // Windows-1251
                Encoding.GetEncoding(866),  // CP866
                Encoding.GetEncoding(20866) // KOI8-R
            };

            string bestResult = text;
            int maxCyrillicCount = CountCyrillic(text);

            foreach (var enc in encodingsToTry)
            {
                try
                {
                    // Пробуем переинтерпретировать текущие символы как байты другой кодировки
                    byte[] bytes = Encoding.Default.GetBytes(text);
                    string decoded = enc.GetString(bytes);

                    int cyrillicCount = CountCyrillic(decoded);
                    int questionCount = decoded.Count(c => c == '?');

                    // Если стало больше русских букв и меньше/ровно знаков вопроса — это лучший кандидат
                    if (cyrillicCount > maxCyrillicCount && questionCount <= text.Count(c => c == '?'))
                    {
                        maxCyrillicCount = cyrillicCount;
                        bestResult = decoded;
                    }
                }
                catch
                {
                    // Игнорируем ошибки кодировок
                }
            }

            return bestResult;
        }

        private static int CountCyrillic(string s)
        {
            int count = 0;
            foreach (char c in s)
            {
                if ((c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё')
                    count++;
            }
            return count;
        }
    }
}
