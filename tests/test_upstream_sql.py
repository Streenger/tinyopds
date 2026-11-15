"""Run the actual SQL constants against a disposable SQLite database."""
import pathlib
import re
import sqlite3
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
SCHEMA = (ROOT / "TinyOPDS/Data/DatabaseSchema.cs").read_text(encoding="utf-8-sig")


def query(name):
    match = re.search(r'public const string ' + re.escape(name) + r'\s*=\s*@"(.*?)";',
                      SCHEMA, re.S)
    if not match:
        raise AssertionError("SQL constant not found: " + name)
    return match.group(1).replace('""', '"')


class UpstreamSqlChecks(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.addCleanup(self.db.close)
        for name in ("CreateBooksTable", "CreateAuthorsTable", "CreateTranslatorsTable",
                     "CreateBookAuthorsTable", "CreateBookTranslatorsTable"):
            self.db.execute(query(name))
        for book_id, replaced in (("active", None), ("old", "active")):
            self.db.execute(
                "INSERT INTO Books(ID,FileName,Title,AddedDate,ReplacedByID) VALUES(?,?,?,?,?)",
                (book_id, book_id + ".fb2", "Title", 1, replaced))
        # Deliberately reverse alphabetical order, as in a real coauthored book.
        for author_id, name in ((1, "Zulu"), (2, "Alpha"), (3, "Obsolete")):
            self.db.execute("INSERT INTO Authors(ID,Name,LastNameSoundex) VALUES(?,?,?)",
                            (author_id, name, "S530"))
            self.db.execute("INSERT INTO Translators(ID,Name) VALUES(?,?)", (author_id, name))
        self.db.executemany("INSERT INTO BookAuthors(BookID,AuthorID) VALUES(?,?)",
                            (("active", 1), ("active", 2), ("old", 3)))
        self.db.executemany("INSERT INTO BookTranslators(BookID,TranslatorID) VALUES(?,?)",
                            (("active", 1), ("active", 2)))

    def test_author_order(self):
        self.assertEqual([("Zulu",), ("Alpha",)],
                         self.db.execute(query("SelectBookAuthors"),
                                         {"BookID": "active"}).fetchall())

    def test_translator_order(self):
        self.assertEqual([("Zulu",), ("Alpha",)],
                         self.db.execute(query("SelectBookTranslators"),
                                         {"BookID": "active"}).fetchall())

    def test_soundex_ignores_replaced_books(self):
        self.assertEqual([("Alpha",), ("Zulu",)],
                         self.db.execute(query("SelectAuthorsBySoundex"),
                                         {"Soundex": "S530"}).fetchall())


if __name__ == "__main__":
    unittest.main()
