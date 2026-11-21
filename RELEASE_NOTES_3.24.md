# TinyOPDS v3.24 (Streenger Edition) - Release Notes

## Основные изменения / Highlights

- **Параллельная починка архивов в памяти**: нормализатор чинит архивы в `физических ядер - 1` потоков, книги пачками собираются в памяти (бюджет 2 ГБ) и записываются на диск пачками; на проверке 1000 книг 7,6 с -> 2,1 с.
- **Быстрая вставка книг в базу**: строки полнотекстового индекса книг хранятся под rowid книги, триггеры работают по ключу, а не полным перебором индекса (около 211 мс -> 1 мс на повторное сохранение известной книги при 500 тысячах книг). При первом запуске индекс перестраивается (около минуты на 500 тысяч книг; `PRAGMA user_version = 2`). Перед запуском сделайте копию `books.sqlite`.
- **Вкладка Misc**: чекбоксы «Хранить оригиналы исправленных книг в libmeta», «Хранить битые книги в libmeta» и «Хранить дубликаты в libmeta». Снятая галочка для битых книг и дубликатов означает удаление; `MoveBadBooks` и `MoveDuplicates` раньше не читались, и такие файлы переносились всегда. Архив с несколькими книгами не удаляется, а остаётся нормализатору для следующего скана. По умолчанию все три опции включены.

## English Version

- **Parallel in-memory archive repair** (physical cores minus one, 2 GB budget, batched disk writes): 7.6 s -> 2.1 s per 1000 books.
- **Faster book inserts**: the books full-text index rows use the book rowid and the triggers go by key (about 211 ms -> 1 ms per re-saved known book at 500k books). The index is rebuilt on first launch (about a minute at 500k books, `PRAGMA user_version = 2`); back up `books.sqlite` first.
- **Misc tab**: checkboxes to keep originals, bad books and duplicates in `libmeta`; unchecked bad books and duplicates are deleted (the settings were never read before, so they were always moved). A multi-book archive is never deleted and is left for the normalizer on the next scan. All three default to on.
