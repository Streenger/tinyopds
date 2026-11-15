# Проверки выборочного переноса upstream

Проверки не используют рабочую библиотеку, не запускают сканирование и не меняют настоящую базу. EPUB создаются в памяти, а SQL выполняется в отдельной временной базе SQLite.

## Windows

Запустить из корня репозитория в Developer PowerShell для Visual Studio с установленным .NET Framework 4.8 Developer Pack:

```powershell
msbuild TinyOPDS.sln /p:Configuration=Release '/p:Platform=Any CPU' /p:LangVersion=latest /m
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /out:$env:TEMP\UpstreamSafeChecks.exe /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll tests\UpstreamSafeChecks.cs
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $env:TEMP\UpstreamSafeChecks.exe TinyOPDS\bin\Release\TinyOpds3.15.5\TinyOPDS.exe
if ($LASTEXITCODE -ne 0) { throw 'GUI assembly checks failed' }
& $env:TEMP\UpstreamSafeChecks.exe TinyOPDSCLI\bin\Release\TinyOPDSCLI.exe
if ($LASTEXITCODE -ne 0) { throw 'CLI assembly checks failed' }
python -m unittest discover -s tests -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) { throw 'SQL checks failed' }
node tests/reader_translation_check.js
if ($LASTEXITCODE -ne 0) { throw 'Reader checks failed' }
```

Python 3 и Node.js нужны только для проверок, не для самой программы. Workflow `Upstream safe checks` повторяет эти команды и прикладывает сборки как Actions artifacts, не создавая release.

## Mono

Для сборки нужен современный Roslyn, запускаемый через Mono: штатный компилятор старого xbuild не поддерживает уже существующий код проекта. Ниже `ROSLYN_DIR` указывает на каталог с `csc.exe` из `Microsoft.Net.Compilers.Toolset`:

```bash
# Только на регистрозависимой ФС: csproj использует другое написание существующего имени.
ln -s fb2Parser.cs TinyOPDS/Parsers/FB2Parser.cs
xbuild TinyOPDS.sln /p:Configuration=Release \
  /p:CscToolPath="$ROSLYN_DIR" /p:CscToolExe=csc.exe \
  /p:LangVersion=latest /p:PostBuildEvent= /verbosity:minimal
mcs -r:System.Drawing -r:System.Xml.Linq \
  -r:System.IO.Compression -r:System.IO.Compression.FileSystem \
  -out:/tmp/UpstreamSafeChecks.exe tests/UpstreamSafeChecks.cs
mono /tmp/UpstreamSafeChecks.exe TinyOPDS/bin/Release/TinyOpds3.15.5/TinyOPDS.exe
mono /tmp/UpstreamSafeChecks.exe TinyOPDSCLI/bin/Release/TinyOPDSCLI.exe
python3 -m unittest discover -s tests -p 'test_*.py' -v
node tests/reader_translation_check.js
# Удалять только созданную выше временную ссылку, не исходный fb2Parser.cs.
test ! -L TinyOPDS/Parsers/FB2Parser.cs || rm TinyOPDS/Parsers/FB2Parser.cs
```

## Что именно проверяется

- **C# harness:** 25 проверок для каждой сборки: правила семи языков, пять путей к EPUB-обложкам, четыре состояния кеша, порядок связей, наличие типов нормализатора и санитайзера, компиляция и результат встроенного XSL.
- **SQLite:** три поведенческих проверки настоящих SQL-констант на настоящих таблицах: порядок авторов, порядок переводчиков и исключение авторов только заменённых книг из Soundex.
- **JavaScript:** метод реального класса читалки, без браузерных зависимостей: язык перевода, запасной язык браузера, восстановление исходного языка и отсутствие открытой книги.

Наличие типов нормализатора не доказывает корректность нормализации; её исходники дополнительно оставлены неизменными. Эти проверки также не заменяют полноразмерное сканирование, проверку OPDS-клиентов и интерактивный тест Windows.

## Ручная проверка перед использованием

- **Резервная копия:** закрыть сервер и сохранить EXE, конфигурацию, `books.sqlite`, `normalized.index` и `libmeta`.
- **Малая коллекция:** проверить обычное сканирование, нормализацию и повторное сканирование на копии 1–2 тысяч архивов, затем проверить «отслеживать изменения».
- **Windows:** скрыть окно в трей, сменить разрешение или отключить монитор и вернуть его, проверить размер и положение; проверить щелчок по трею при перекрытом и открытом окне.
- **Обложки:** открыть одну книгу в каталоге, затем получить полную обложку; проверить EPUB с пробелами и `..` в пути.
- **Веб-каталог:** на ширине 390 px проверить сворачивание и раскрытие длинного описания; на обычной ширине проверить полный текст.
- **Читалка:** отдельно проверить настоящий перевод страницы браузером; автоматический JS-тест проверяет реакцию на атрибуты, но не сам сервис перевода.

Для воспроизводимой браузерной проверки C# harness принимает вторым аргументом путь вывода синтетического каталога. Рядом с ним нужно разместить `smart-header.js`, `infinite-scroll.js`, `favicon.ico` и `book_cover.jpg` из `TinyOPDS/Resources`, затем раздать каталог локальным HTTP-сервером.
