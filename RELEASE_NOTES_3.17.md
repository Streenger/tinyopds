# TinyOPDS v3.17.0 (Streenger Edition) - Release Notes

Комплексное обновление форка: интеграция актуальных решений из upstream 3.16–3.17 (`sensboston/tinyopds`) с сохранением расширенных инструментов санации FB2 и устранением критических ошибок парсера.

---

## Основные изменения / Highlights

### 1. Интеграция улучшений upstream (3.16 – 3.17)
- **Поддержка нестандартных архивов**: Добавлена библиотека `SharpCompress` и модуль `ZipEntryReader`. Книги и обложки теперь читаются из ZIP-архивов со сжатием PPMd, LZMA, BZip2 и Deflate64.
- **Оптимизация поиска и FTS**: Исправлен полнотекстовый поиск для запросов с пунктуацией и кавычками. Однократная автоматическая перестройка индекса `BooksFTS` устраняет дубликаты и повисшие записи.
- **Производительность каталогов**: Введено кэширование статистики жанров, серий и алфавита авторов. Догрузка связей книг переведена на постраничный режим, существенно снижая пиковое потребление памяти.
- **Безопасность и авторизация**: Обновлена обработка HTTP Basic Auth (RFC 7617, поддержка паролей с символом двоеточия). Улучшена защита от XSS в веб-читалке.
- **Совместимость скачивания**: Исправлены имена файлов в заголовке `Content-Disposition` (ASCII-safe) и MIME-тип `application/fb2+zip` для корректной работы сторонних OPDS-читалок (FBReader, AlReader и др.).

### 2. Доработки и исправления форка (Streenger)
- **Устранение сбоя необъявленных префиксов XML**: В `FB2Parser` добавлена авторегистрация пространств имён `xmlns:xlink` и `xmlns:l` непосредственно в блоке `<description>`. Устранена ошибка, из-за которой книги с атрибутами обложек `l:href` и `xlink:href` помечались как повреждённые (`Book.IsValid=false`).
- **Корректные кодировки архивов**: В `CoverImage` добавлена поддержка кодировки `Latin1` при открытии архивов, обеспечив извлечение обложек для файлов с кириллическими именами внутри ZIP.
- **Санация проблемных заголовков и серий**: В `Fb2Sanitizer` добавлены правила удаления пустых тегов серий вида `<sequence name="" number="" />`, очистка поврежденных конструкций и подстановка резервного названия для книг с заголовками из знаков вопроса (`???`).
- **Синхронизация версий**: Номера версий GUI и CLI синхронизированы на `3.17.0.0`.

### 3. Нормализация библиотеки и защита от ложных дубликатов
- **Починка вложенности тегов FB2**: в `Fb2Sanitizer` добавлен проход по стеку тегов (закрытие незакрытых `<p>`, `<emphasis>`, `<strong>`, `<a>`, удаление закрывающих тегов без пары, приведение тегов FB2 к нижнему регистру, удаление HTML-тегов, которых нет в FB2, с сохранением текста). Проход запускается только для файлов, которые иначе не разбираются.
- **Повреждённые абзацы и пространства имён**: открывающий `<p>` с мусором вместо атрибутов теперь трактуется как обычный `<p>`; при использовании `l:href` без объявления `xmlns:l` оно добавляется в корневой тег.
- **Индекс нормализованных архивов** (`normalized.index` рядом с `.exe`): неизменившиеся архивы пропускаются без распаковки. Отказы перепроверяются только при увеличении `NormalizerIndex.Version`. Для полной перепроверки удалите файл индекса. В лог добавлено поле `skipped(indexed)`.
- **Ложные дубликаты**: книги с одинаковым `document-info/id`, но разным названием, серией и размером (разница более 25 %) получают собственный идентификатор и не перезаписывают друг друга в базе. В лог пишется `Document ID collision`.
- **Разбор FB2**: книги без `<author>` получают автора по цепочке (составитель из названия издания, автор из заголовка тела книги, `Неизвестный автор`) вместо перемещения в `libmeta/bad`; объявления `xmlns:l` и `xmlns:xlink` проверяются в теге `<description>`, а не по всему блоку.
- **Первый прогон нормализации долгий**: индекс изначально пуст, поэтому проверяется каждый архив; на больших библиотеках это занимает несколько часов. Следите за ходом по логу (`LibraryNormalizer: …`, итог `LibraryNormalizer: done. …`). Следующие запуски пропускают обработанные архивы.
- **Прогон по большой библиотеке**: нормализация 498 331 архива завершена без ошибок (починено 3 303 архива, 10 837 исправлений); при сканировании в `libmeta` перенесено 4 битых файла и 489 дубликатов.

---

### 4. Исправления и ускорение после 3.17.0 (Streenger)
- **Начало блока `<description>`.** Раньше парсер принимал за начало описания первое вхождение слова `description`, в том числе в объявлении пространства имён (`xmlns:fb3d=".../FictionBook3/description"`), вырезал кусок, начинающийся с `<FictionBook …`, и книга считалась пустой (`title=EMPTY`) и уходила в `libmeta/bad`. Теперь ищется именно открывающий тег `<description>` (с префиксом или без), включая случай, когда тег попадает на границу блоков чтения.
- **Санитайзер только для начала файла.** Для каталога нужен лишь блок описания, поэтому конец описания ищется по байтам, декодируется и проверяется только начало файла. Чтение описания ускорилось примерно со 75–436 мс до 1 мс на книгу.
- **Исправлена порча текста.** `FixCorruptedCyrillic` принимала украинские буквы (`і`, `ї`, `є`) и тире в английском тексте за «испорченную кодировку» и превращала названия в мусор (`Ігор` → `╡ЦНП`). Теперь перекодировка применяется только при заметном росте кириллицы, без символов псевдографики.
- **Корректный XML не переписывается санитайзером.** Неизвестный, но корректный тег (например `<book-author>` в `publish-info`) остаётся тегом.
- **Исправления в самих книгах** (нормализатор, версия индекса 3): недостающий `document-info/id`, дата документа, дата книги по году издания (или текущая), удаление HTML-мусора (`br`, `b`, `i`, `font`, `div` …) из описания. Повторные сканирования больше не лечат одно и то же.
- **Безопасная запись архивов.** Временный файл, сброс данных на диск, проверка и атомарная подмена `File.Replace` вместо «удалить и переименовать» (после сбоя питания оставались файлы нужного размера, заполненные нулями).
- **Копии оригиналов только для содержательных правок.** Правки метаданных не копируют архив в `libmeta/orig`.
- **Известные файлы и замены.** Имена известных файлов загружаются в память одним запросом; старые версии, заменённые в одной пачке, отправляются в `libmeta/dup` и не разбираются заново.
- **Жанры.** Поиск тега без учёта регистра с написанием из справочника, очистка невидимых символов, скобок и кириллических двойников, таблица подстановок (около 70 соответствий) и файл `genre-aliases.txt` (`старый_тег=новый_тег`).
- **Журнал.** Буферизация (сброс раз в 30 секунд, при ошибке и при выходе); ограничение повторяющихся предупреждений `Document ID collision`; убрано предупреждение о коротких названиях; разбивка правок санитайзера по видам в строке `auto-sanitized`; строки `Scan profile` с подробным профилем сканирования.
- **Результаты.** Повторное сканирование библиотеки из 498 тысяч архивов с 6 047 новыми книгами — около 3,5 минут; тестовый набор из 3 363 проблемных книг после нормализации даёт 0 строк `auto-sanitized`, 0 невалидных книг и 0 нераспознанных жанров.

---

## English Version

**TinyOPDS v3.17.0 (Streenger Edition)**

Upstream synchronization (3.16–3.17) with preserved extended FB2 repair toolchain and critical XML namespace fixes.

### Key Changes:
- **Upstream 3.16–3.17 sync**:
  - Added `SharpCompress` & `ZipEntryReader` fallback for PPMd, LZMA, and BZip2 compressed archives.
  - Full-text search (FTS5) queries properly handle punctuation and quotes.
  - Automated one-time `BooksFTS` rebuild via `PRAGMA user_version`.
  - In-memory caching for genre and sequence counts; paginated book relation loading.
  - Improved HTTP authentication (RFC 7617) and XSS sanitization in Web Reader.
  - Safe ASCII download filenames and proper `application/fb2+zip` MIME type.
- **FB2 Parser & Encoding fixes**:
  - Fixed XML parser crash caused by undeclared `xmlns:l` and `xmlns:xlink` prefixes inside the `<description>` block.
  - Fixed Cyrillic cover image extraction from ZIP archives in `CoverImage` (`Latin1` encoding support).
  - Enhanced `Fb2Sanitizer`: stripped empty sequence tags and handled question-mark placeholder titles.
  - Added a stack-based FB2 tag nesting repair (closes unclosed `<p>`, `<emphasis>`, `<strong>`, `<a>`, drops unmatched closing tags, normalizes FB2 tag case, strips non-FB2 HTML tags while keeping text). It runs only for files that fail to parse otherwise.
  - Malformed opening `<p>` tags with garbage instead of attributes are treated as plain `<p>`; `xmlns:l` is added when `l:href` is used without a declaration.
  - `normalized.index` next to the executable lets the normalizer skip unchanged archives. Unrepairable archives are rechecked only when `NormalizerIndex.Version` is raised. Delete the file to force a full recheck.
  - Books sharing a `document-info/id` but differing in title, series and size (more than 25%) get their own ID instead of replacing each other as false duplicates (`Document ID collision` warning in the log).
  - FB2 parsing: books without `<author>` get an author from the compiler in the publication title, then from the body title, then `Неизвестный автор`, instead of being moved to `libmeta/bad`; `xmlns:l` and `xmlns:xlink` are checked on the `<description>` tag itself, not anywhere in the block.
  - The first normalization run is slow: the index starts empty, so every archive is checked (hours on very large libraries). Watch the progress in the log; later runs skip processed archives.
  - Real run: 498,331 archives normalized without errors (3,303 repaired, 10,837 fixes); the scan moved 4 bad files and 489 duplicates to `libmeta`.

### Fixes and speed-ups after 3.17.0:
  - **Description start**: the parser no longer mistakes a namespace declaration containing the word `description` (e.g. `xmlns:fb3d=".../FictionBook3/description"`) for the description tag; such books used to end up in `libmeta/bad` with an empty title.
  - **Sanitizer on the file head only**: the end of `<description>` is located by bytes and only the head is decoded and checked. Reading a description dropped from about 75-436 ms to about 1 ms per book.
  - **No more mangled text**: Ukrainian letters and typographic dashes are no longer recoded into garbage; recoding applies only when it clearly increases Cyrillic and yields no box-drawing characters. Well-formed XML with unknown tags is left alone.
  - **Persistent fixes**: the normalizer (index version 3) writes missing document ids and dates, derives the book date from the publication year (or today), and removes HTML junk from the description.
  - **Crash-safe rewrite** (temp file, flush, verification, atomic `File.Replace`); originals are backed up to `libmeta/orig` only for content fixes.
  - **Known-file set and replaced versions**: older versions replaced inside a batch go to `libmeta/dup` and are not parsed again.
  - **Genres**: case-insensitive lookup, cleanup of hidden characters/brackets/homoglyphs, a built-in alias table (about 70 entries) and `genre-aliases.txt` (`old_tag=new_tag`).
  - **Log**: buffered (30 s flush, immediate on errors and exit), rate-limited ID collision warnings, per-kind breakdown of sanitizer fixes, detailed `Scan profile`.
  - **Results**: rescan of a 498k-archive library with 6,047 new books in about 3.5 minutes; a 3,363-book problem set shows 0 `auto-sanitized`, 0 invalid books and 0 unknown genres after normalization.
