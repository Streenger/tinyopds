using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

// Run: UnknownStatsChecks.exe <TinyOPDS GUI exe>
internal static class UnknownStatsChecks
{
    private static Assembly app;
    private static int failures;
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    private static void Equal(object e, object a)
    {
        if (!object.Equals(e, a)) throw new Exception("Expected [" + e + "], got [" + a + "]");
    }

    private static string Key(string title)
    {
        var t = app.GetType("TinyOPDS.Data.Book", true);
        object b = Activator.CreateInstance(t, new object[] { "x.fb2" });
        t.GetProperty("Title").SetValue(b, title, null);
        t.GetProperty("Authors").SetValue(b, new List<string> { "Ivanov Ivan" }, null);
        return (string)t.GetMethod("GenerateDuplicateKey").Invoke(b, null);
    }

    private static bool Refresh(bool scanning, ref int counter)
    {
        var m = app.GetType("TinyOPDS.Data.Library", true).GetMethod("NextBatchRefreshesStats", All);
        var args = new object[] { scanning, counter };
        bool r = (bool)m.Invoke(null, args);
        counter = (int)args[1];
        return r;
    }

    private static string Sanitize(string text, out int fixes)
    {
        var m = app.GetType("TinyOPDS.Data.Fb2Sanitizer", true).GetMethod("Sanitize", new[] { typeof(string), typeof(int).MakeByRefType() });
        var args = new object[] { text, 0 };
        string r = (string)m.Invoke(null, args);
        fixes = (int)args[1];
        return r;
    }

    private static int Main(string[] argv)
    {
        app = Assembly.LoadFrom(argv[0]);

        Check("normal title gets a key", () => { if (Key("War and Peace") == "") throw new Exception("empty"); });
        Check("Unknown title: no key", () => Equal("", Key("Unknown")));
        Check("unknown (case, spaces): no key", () => Equal("", Key("  unKnown ")));
        Check("blank title: no key", () => Equal("", Key("   ")));
        Check("title starting with Unknown still gets key", () => { if (Key("Unknown Soldier") == "") throw new Exception("empty"); });

        Check("stats: outside scan every batch refreshes", () => { int c = 0; Equal(true, Refresh(false, ref c)); Equal(0, c); });
        Check("stats: scan refreshes once per 10 batches", () =>
        {
            int c = 0, n = 0;
            for (int i = 0; i < 20; i++) if (Refresh(true, ref c)) n++;
            Equal(2, n);
        });

        Check("encoding=\"UTF8\" in declaration is fixed", () =>
        {
            int f;
            string r = Sanitize("<?xml version=\"1.0\" encoding=\"UTF8\"?><FictionBook><a>x</a></FictionBook>", out f);
            if (f < 1) throw new Exception("fixes=" + f);
            if (r.IndexOf("encoding=\"utf-8\"", StringComparison.Ordinal) < 0) throw new Exception(r);
        });
        Check("valid utf-8 declaration untouched", () =>
        {
            int f;
            Sanitize("<?xml version=\"1.0\" encoding=\"utf-8\"?><FictionBook><a>x</a></FictionBook>", out f);
            Equal(0, f);
        });

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
