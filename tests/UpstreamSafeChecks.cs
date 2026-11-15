using System;
using System.IO;
using System.IO.Compression;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;

// No test framework or production dependency changes. Run against each built EXE.
internal static class UpstreamSafeChecks
{
    private static Assembly app;
    private static int failures;
    private static string renderedPage;

    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }

    private static void Equal(object expected, object actual)
    {
        if (!object.Equals(expected, actual))
            throw new Exception("Expected [" + expected + "], got [" + actual + "]");
    }

    private static Type AppType(string name) { return app.GetType(name, true); }

    private static object StaticCall(string type, string method, params object[] args)
    {
        return AppType(type).GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
    }

    private static void Plural(string language, string input, string expected)
    {
        Equal(expected, StaticCall("TinyOPDS.Pluralizer", "Apply", 0, language, input));
    }

    private static void AddText(ZipArchive zip, string path, string text)
    {
        using (var writer = new StreamWriter(zip.CreateEntry(path).Open())) writer.Write(text);
    }

    private static void Cover(string href, string entryName)
    {
        using (var epub = new MemoryStream())
        {
            using (var zip = new ZipArchive(epub, ZipArchiveMode.Create, true))
            {
                AddText(zip, "META-INF/container.xml",
                    "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles>" +
                    "<rootfile full-path='OPS/Text/package.opf'/></rootfiles></container>");
                AddText(zip, "OPS/Text/package.opf",
                    "<package xmlns='http://www.idpf.org/2007/opf'><metadata><meta name='cover' content='art'/></metadata>" +
                    "<manifest><item id='art' href='" + href + "' media-type='image/png'/></manifest></package>");
                using (var bitmap = new Bitmap(12, 20))
                using (var stream = zip.CreateEntry(entryName).Open())
                    bitmap.Save(stream, ImageFormat.Png);
            }
            epub.Position = 0;
            var parser = Activator.CreateInstance(AppType("TinyOPDS.Parsers.ePubParser"));
            var method = parser.GetType().GetMethod("GetCoverImage", new[] { typeof(Stream), typeof(string) });
            using (var image = (Image)method.Invoke(parser, new object[] { epub, "synthetic.epub" }))
            {
                if (image == null) throw new Exception("Manifest cover was not extracted");
            }
        }
    }

    private static void CachedImage(bool cover, bool thumbnail)
    {
        var type = AppType("TinyOPDS.Data.CachedCoverImage");
        var cached = Activator.CreateInstance(type, new object[] {
            "test", cover ? new byte[] { 1 } : null, thumbnail ? new byte[] { 2 } : null });
        Equal(cover, type.GetProperty("HasCover").GetValue(cached, null));
        Equal(thumbnail, type.GetProperty("HasThumbnail").GetValue(cached, null));
    }

    private static void QueryOrder(string name, string order)
    {
        string sql = (string)AppType("TinyOPDS.Data.DatabaseSchema").GetField(name).GetRawConstantValue();
        if (!sql.Contains("ORDER BY " + order)) throw new Exception("Book relation order was lost");
    }

    private static void RenderCatalog()
    {
        var transform = new XslCompiledTransform();
        using (var stream = app.GetManifestResourceStream(app.GetName().Name + ".Resources.xml2html.xsl"))
        using (var reader = XmlReader.Create(stream)) transform.Load(reader);
        string description = string.Concat(System.Linq.Enumerable.Repeat(
            "A long description to exercise the collapsed book card and its expand button. ", 40));
        string feed = "<feed xmlns='http://www.w3.org/2005/Atom' xmlns:dc='http://purl.org/dc/terms/'>" +
            "<id>tag:books</id><title>Upstream regression check</title><icon>/icons/books.ico</icon>" +
            "<entry><id>test-book</id><title>Test book</title><author><name>Zulu</name><uri>/author/test</uri></author>" +
            "<dc:language>uk</dc:language><content>" + description + "</content><format>fb2</format><size>1 KB</size>" +
            "<link type='image/jpeg' href='/book_cover.jpg'/><link type='application/fb2+zip' href='/download/test-book/fb2'/>" +
            "</entry></feed>";
        var parameters = new XsltArgumentList();
        parameters.AddParam("uiLanguage", "", "en");
        using (var input = XmlReader.Create(new StringReader(feed)))
        using (var output = new StringWriter())
        {
            transform.Transform(input, parameters, output);
            renderedPage = output.ToString();
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2) { Console.Error.WriteLine("Usage: UpstreamSafeChecks.exe <app.exe> [rendered-catalog.html]"); return 2; }
        app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Check("initialize embedded plural rules", () => {
            StaticCall("TinyOPDS.Localizer", "Init");
            Equal(true, StaticCall("TinyOPDS.Pluralizer", "IsLanguageSupported", "ru"));
        });
        Check("Russian preposition, singular", () => Plural("ru", "3 книги в 1 серий", "3 книги в 1 серии"));
        Check("Russian preposition, plural", () => Plural("ru", "3 книги в 5 серий", "3 книги в 5 сериях"));
        Check("Ukrainian preposition", () => Plural("uk", "3 книги у 5 серій", "3 книги у 5 серіях"));
        Check("Polish genitive singular", () => Plural("pl", "3 książki od 1 autorów", "3 książki od 1 autora"));
        Check("English singular", () => Plural("en", "1 books", "1 book"));
        Check("French zero", () => Plural("fr", "0 livres", "0 livre"));
        Check("German singular", () => Plural("de", "1 Bücher", "1 Buch"));
        Check("Spanish singular", () => Plural("es", "1 libros", "1 libro"));
        Check("EPUB: ordinary manifest path", () => Cover("Images/art.png", "OPS/Text/Images/art.png"));
        Check("EPUB: URL-encoded spaces", () => Cover("../Images/book%20art.png", "OPS/Images/book art.png"));
        Check("EPUB: dot segments and fragment", () => Cover("./../Images/sub/../art.png#image", "OPS/Images/art.png"));
        Check("EPUB: case-insensitive archive lookup", () => Cover("../Images/ART.PNG", "OPS/Images/art.png"));
        Check("EPUB: absolute archive path", () => Cover("/Images/art.png", "Images/art.png"));
        Check("Cache: thumbnail is not a cover", () => CachedImage(false, true));
        Check("Cache: cover is not a thumbnail", () => CachedImage(true, false));
        Check("Cache: both images", () => CachedImage(true, true));
        Check("Cache: no images", () => CachedImage(false, false));
        Check("Authors preserve book order", () => QueryOrder("SelectBookAuthors", "ba.rowid"));
        Check("Translators preserve book order", () => QueryOrder("SelectBookTranslators", "bt.rowid"));
        Check("Fork normalizer preserved", () => AppType("TinyOPDS.LibraryNormalizer"));
        Check("Fork FB2 sanitizer preserved", () => AppType("TinyOPDS.Data.Fb2Sanitizer"));
        Check("Catalog XSL compiles and renders", RenderCatalog);
        Check("Catalog declares interface language", () => {
            if (renderedPage == null || !Regex.IsMatch(renderedPage, "<html\\b[^>]*\\blang=\"en\"") ||
                !Regex.IsMatch(renderedPage, "<html\\b[^>]*\\btranslate=\"no\""))
                throw new Exception("Interface language missing");
        });
        Check("Catalog description uses book language", () => {
            if (renderedPage == null || !Regex.IsMatch(renderedPage, "class=\"book-descr\"\\s+lang=\"uk\""))
                throw new Exception("Book language missing");
        });
        if (args.Length == 2 && renderedPage != null) File.WriteAllText(args[1], renderedPage);
        Console.WriteLine("Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
