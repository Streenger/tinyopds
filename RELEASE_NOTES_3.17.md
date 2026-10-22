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
