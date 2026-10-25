/*
 * This file is part of TinyOPDS server project
 * https://github.com/sensboston/tinyopds
 *
 * Copyright (c) 2013-2025 SeNSSoFT
 * SPDX-License-Identifier: MIT
 *
 * This module defines the Genre class (book genre)
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TinyOPDS.Data
{
    public class Genre
    {
        public string Tag { get; set; }
        public string Name { get; set; }
        public string Translation { get; set; }
        public List<Genre> Subgenres = new List<Genre>();
    }

    /// <summary>
    /// Нестандартные теги жанров, которые встречаются в книгах, -> теги справочника FB2.
    /// Встроенная таблица дополняется (и переопределяется) файлом genre-aliases.txt
    /// в папке данных программы. Формат файла: одна пара на строку "старый_тег=новый_тег",
    /// строки с # - комментарии. Регистр не важен.
    /// </summary>
    public static class GenreAliases
    {
        public const string FileName = "genre-aliases.txt";

        private static readonly object sync = new object();
        private static Dictionary<string, string> map;

        private static readonly string[,] Defaults =
        {
            { "upbringing_book", "family_parenting" },
            { "psy_social", "sci_psychology" },
            { "psy_personal", "sci_psychology" },
            { "psy_generic", "sci_psychology" },
            { "foreign_detective", "detective" },
            { "foreign_sf", "sf" },
            { "foreign_fantasy", "sf_fantasy" },
            { "foreign_adventure", "adventure" },
            { "foreign_contemporary", "prose_contemporary" },
            { "foreign_romance", "love_contemporary" },
            { "foreign_humor", "humor" },
            { "foreign_poetry", "poetry" },
            { "foreign_dramaturgy", "drama" },
            { "foreign_esoterics", "religion_esoterics" },
            { "foreign_business", "sci_business" },
            { "foreign_comp", "computers" },
            { "dark_fantasy", "sf_fantasy" },
            { "litrpg", "sf_litrpg" },
            { "comp_all", "computers" },
            { "thriller_mystery", "thriller" },
            { "unrecognised", "other" },
            { "unrecognized", "other" },
            { "fantasy", "sf_fantasy" },
            { "scifi", "sf" },
            { "sci_fi", "sf" },
            { "science_fiction", "sf" },
            { "horror", "sf_horror" },
            { "cooking", "home_cooking" },
            { "biography", "nonf_biography" },
            { "non_fiction", "nonfiction" },
            { "prose_modern", "prose_contemporary" },
            { "sociology_book", "sci_social_studies" },
            { "nonfiction_sociology", "sci_social_studies" },
            { "histor_military", "military_history" },
            { "music_dancing", "music" },
            { "psy_alassic", "sci_psychology" },
            { "psy_classic", "sci_psychology" },
            { "sf_writing", "fanfiction" },
            { "slash", "fanfiction" },
            { "deathfic", "fanfiction" },
            { "biogr_professionals", "nonf_biography" },
            { "maigret", "det_classic" },
            { "general", "other" },
            { "professional_law", "sci_juris" },
            { "nonfiction_philosophy", "sci_philosophy" },
            { "thriller_police", "det_police" },
            { "foreign_story", "story" },
            { "юмор", "humor" },
            { "журналы", "periodic" },
            { "novel", "roman" },
            { "geo_guide", "geo_guides" },
            { "travel_spec", "travel_notes" },
            { "travel_polar", "travel_notes" },
            { "f_fantasy", "sf_fantasy" },
            { "performance", "theatre" },
            { "chris_edu", "religion_christianity" },
            { "prose_teen", "teens_literature" },
        };

        private static Dictionary<string, string> Load()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Defaults.GetLength(0); i++)
                result[Defaults[i, 0]] = Defaults[i, 1];

            try
            {
                string path = Path.Combine(Utils.ServiceFilesLocation, FileName);
                if (File.Exists(path))
                {
                    foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || eq == line.Length - 1) continue;
                        string from = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string to = line.Substring(eq + 1).Trim().ToLowerInvariant();
                        if (from.Length > 0 && to.Length > 0) result[from] = to;
                    }
                    Log.WriteLine(LogLevel.Info, "GenreAliases: {0} aliases (including {1})", result.Count, FileName);
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine(LogLevel.Warning, "GenreAliases: cannot read {0}: {1}", FileName, ex.Message);
            }
            return result;
        }

        private static readonly Dictionary<char, char> Homoglyphs = new Dictionary<char, char>
        {
            { '\u0430', 'a' }, { '\u0435', 'e' }, { '\u043E', 'o' }, { '\u0440', 'p' },
            { '\u0441', 'c' }, { '\u0443', 'y' }, { '\u0445', 'x' }, { '\u0456', 'i' },
            { '\u0410', 'A' }, { '\u0415', 'E' }, { '\u041E', 'O' }, { '\u0420', 'P' },
            { '\u0421', 'C' }, { '\u0425', 'X' }, { '\u0406', 'I' },
        };

        /// <summary>
        /// Приводит "грязный" тег к виду, пригодному для поиска: убирает невидимые символы и
        /// пробелы, достаёт тег из скобок ("Детективная фантастика(sf_detective"), заменяет
        /// кириллические двойники латинских букв ("sf_horrоr"). Регистр не меняется.
        /// </summary>
        public static string Clean(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";

            var sb = new StringBuilder(tag.Length);
            foreach (char c in tag)
            {
                if (c < ' ' || c == '\u00A0' || (c >= '\u200B' && c <= '\u200F') || c == '\uFEFF') continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();

            // тег внутри скобок: берём то, что после последней "("
            int open = s.LastIndexOf('(');
            if (open >= 0)
            {
                string inner = s.Substring(open + 1).Trim().TrimEnd(')').Trim();
                if (IsPlainTag(inner)) s = inner;
            }

            // кириллические двойники -> латиница, но только если получился ASCII-тег
            bool hasHomoglyph = false;
            foreach (char c in s) { if (Homoglyphs.ContainsKey(c)) { hasHomoglyph = true; break; } }
            if (hasHomoglyph)
            {
                var m = new StringBuilder(s.Length);
                foreach (char c in s) { char r; m.Append(Homoglyphs.TryGetValue(c, out r) ? r : c); }
                if (IsPlainTag(m.ToString())) s = m.ToString();
            }
            return s;
        }

        private static bool IsPlainTag(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    return false;
            return true;
        }

        /// <summary>Возвращает тег справочника для нестандартного тега (если он известен).</summary>
        public static bool TryGet(string tag, out string target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            if (map == null)
            {
                lock (sync)
                {
                    if (map == null) map = Load();
                }
            }
            return map.TryGetValue(tag.Trim(), out target);
        }
    }
}
