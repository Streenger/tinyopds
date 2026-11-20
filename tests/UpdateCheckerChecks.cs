using System;
using System.Reflection;

// Checks the update source and tag parsing of TinyOPDS.UpdateChecker without network access.
internal static class UpdateCheckerChecks
{
    private static int checks, failures;

    private static void Check(string name, bool ok)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: UpdateCheckerChecks.exe <app.exe>"); return 2; }
        var app = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0]));
        var type = app.GetType("TinyOPDS.UpdateChecker", true);
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        object checker = Activator.CreateInstance(type);
        foreach (string field in new[] { "githubReleasesApi", "githubTagsApi", "githubReleasesPage" })
        {
            string url = (string)type.GetField(field, Any).GetValue(checker);
            Check(field + " points to Streenger/tinyopds", url.Contains("/Streenger/tinyopds/") || url.EndsWith("/Streenger/tinyopds/releases"));
        }

        MethodInfo parse = type.GetMethod("ParseVersion", Any);
        Func<string, Version> p = tag => (Version)parse.Invoke(parse.IsStatic ? null : checker, new object[] { tag });
        var current = new Version(3, 22, 5, 0);
        Check("v3.22.6 is newer than 3.22.5", p("v3.22.6") > current);
        Check("v3.23 is newer than 3.22.5", p("v3.23") > current);
        Check("v3.22.5 is not newer than 3.22.5", !(p("v3.22.5") > current));
        Check("v3.17.0 is not newer than 3.22.5", !(p("v3.17.0") > current));
        Check("garbage tag is rejected", p("vX.Y") == null);

        Console.WriteLine("Checks: " + checks + "; Failures: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
