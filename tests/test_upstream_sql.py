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


LEGACY_TRIGGERS = (
    "CREATE TRIGGER books_ai AFTER INSERT ON Books BEGIN INSERT INTO BooksFTS(BookID, Title) VALUES (new.ID, new.Title); END",
    "CREATE TRIGGER books_au AFTER UPDATE ON Books BEGIN UPDATE BooksFTS SET Title = new.Title WHERE BookID = new.ID; END",
    "CREATE TRIGGER books_ad AFTER DELETE ON Books BEGIN DELETE FROM BooksFTS WHERE BookID = old.ID; END",
)
BOOKS = 20000


class BooksFtsByRowidChecks(unittest.TestCase):
    """BookID in BooksFTS is UNINDEXED, so a trigger searching by it scans the whole index.
    The index rows therefore live under the rowid of their Books row."""

    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.addCleanup(self.db.close)
        self.db.execute(query("CreateBooksTable"))
        self.db.execute(query("CreateBooksFTSTable"))

    def create_triggers(self):
        for name in ("CreateBookInsertTrigger", "CreateBookUpdateTrigger", "CreateBookDeleteTrigger"):
            self.db.execute(query(name))

    def add_books(self, count=BOOKS):
        self.db.executemany(query("InsertBook"), (
            {"ID": "id%d" % i, "Version": 1.0, "FileName": "f%d.fb2" % i, "Title": "Title%d" % i,
             "Language": "ru", "BookDate": 1, "DocumentDate": 1, "Annotation": None, "DocumentSize": 1,
             "AddedDate": 1, "DocumentIDTrusted": 0, "DuplicateKey": None, "ReplacedByID": None,
             "ContentHash": None} for i in range(count)))

    def vm_steps(self, sql, params=()):
        steps = [0]
        def tick():
            steps[0] += 1
            return 0
        self.db.set_progress_handler(tick, 100)
        self.db.execute(sql, params)
        self.db.set_progress_handler(None, 0)
        return steps[0]

    def fts_rows(self, match):
        return self.db.execute("SELECT rowid, BookID, Title FROM BooksFTS WHERE BooksFTS MATCH ?",
                               (match,)).fetchall()

    def test_index_row_has_the_rowid_of_its_book(self):
        self.create_triggers()
        self.add_books(50)
        rows = self.db.execute("SELECT b.rowid, f.rowid FROM Books b JOIN BooksFTS f ON f.BookID = b.ID").fetchall()
        self.assertEqual(50, len(rows))
        self.assertTrue(all(a == b for a, b in rows))

    def test_resaving_a_book_with_the_same_title_does_not_scan_the_index(self):
        self.create_triggers()
        self.add_books()
        steps = self.vm_steps(query("UpdateBook"), {
            "ID": "id777", "Version": 1.0, "FileName": "moved/f777.fb2", "Title": "Title777", "Language": "ru",
            "BookDate": 1, "DocumentDate": 1, "Annotation": None, "DocumentSize": 1, "AddedDate": 1,
            "DocumentIDTrusted": 0, "DuplicateKey": None, "ReplacedByID": None, "ContentHash": None})
        self.assertLess(steps, 20)

    def test_new_title_replaces_the_old_one_in_search(self):
        self.create_triggers()
        self.add_books(100)
        self.db.execute("UPDATE Books SET Title = 'Zebra' WHERE ID = 'id5'")
        self.assertEqual([("id5",)], [(r[1],) for r in self.fts_rows("Zebra")])
        self.assertEqual([], self.fts_rows("Title5"))

    def test_deleting_a_book_removes_it_without_scanning_the_index(self):
        self.create_triggers()
        self.add_books()
        steps = self.vm_steps("DELETE FROM Books WHERE ID = ?", ("id777",))
        self.assertEqual([], self.fts_rows("Title777"))
        self.assertLess(steps, 20)

    def test_trigger_survives_a_leftover_index_row_with_the_same_rowid(self):
        # INSERT OR REPLACE on Books skips the delete trigger; the next book may reuse that rowid
        self.create_triggers()
        self.add_books(3)
        self.db.execute("DROP TRIGGER books_ad")
        self.db.execute("DELETE FROM Books WHERE ID = 'id2'")
        self.add_books_with_id("late", "Late")
        self.assertEqual([("late",)], [(r[1],) for r in self.fts_rows("Late")])

    def add_books_with_id(self, book_id, title):
        self.db.execute(query("InsertBook"), {
            "ID": book_id, "Version": 1.0, "FileName": book_id + ".fb2", "Title": title, "Language": "ru",
            "BookDate": 1, "DocumentDate": 1, "Annotation": None, "DocumentSize": 1, "AddedDate": 1,
            "DocumentIDTrusted": 0, "DuplicateKey": None, "ReplacedByID": None, "ContentHash": None})

    def test_migration_moves_an_existing_index_to_book_rowids(self):
        for sql in LEGACY_TRIGGERS:
            self.db.execute(sql)
        self.add_books(200)
        self.db.execute("UPDATE Books SET Title = Title")  # legacy rows may sit under any rowid
        self.db.execute("INSERT INTO BooksFTS(rowid, BookID, Title) VALUES (99999, 'orphan', 'Orphan')")
        for name in ("books_ai", "books_au", "books_ad"):
            self.db.execute("DROP TRIGGER IF EXISTS " + name)
        self.db.execute(query("DeleteAllBooksFTS"))
        self.db.execute(query("RebuildBooksFTSByRowid"))
        self.create_triggers()
        mismatched = self.db.execute(
            "SELECT COUNT(*) FROM Books b LEFT JOIN BooksFTS f ON f.rowid = b.rowid AND f.BookID = b.ID "
            "WHERE f.rowid IS NULL").fetchone()[0]
        self.assertEqual(0, mismatched)
        self.assertEqual([], self.fts_rows("Orphan"))
        self.assertEqual(1, len(self.fts_rows("Title77")))


if __name__ == "__main__":
    unittest.main()
