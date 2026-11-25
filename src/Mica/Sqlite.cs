using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Mica
{
    /// <summary>Read-only SQLite queries using winsqlite3.dll, which ships with Windows 10 and 11 (no extra download).</summary>
    static class Sqlite
    {
        const string Dll = "winsqlite3.dll";
        [DllImport(Dll)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll)] static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
        [DllImport(Dll)] static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Dll)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll)] static extern int sqlite3_close(IntPtr db);

        const int SQLITE_ROW = 100, SQLITE_OPEN_READONLY = 1;

        static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

        /// <summary>Runs a query; each row is returned as strings (numbers as their text).</summary>
        public static List<string[]> Query(string file, string sql, int columns, int maxRows = 100000)
        {
            var rows = new List<string[]>();
            if (sqlite3_open_v2(Utf8Z(file), out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != 0) { sqlite3_close(db); throw new Exception("Couldn't open the database"); }
            try
            {
                if (sqlite3_prepare_v2(db, Utf8Z(sql), -1, out var stmt, IntPtr.Zero) != 0) throw new Exception("Couldn't read the database");
                try
                {
                    while (rows.Count < maxRows && sqlite3_step(stmt) == SQLITE_ROW)
                    {
                        var row = new string[columns];
                        for (int i = 0; i < columns; i++)
                        {
                            var p = sqlite3_column_text16(stmt, i);
                            row[i] = p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p);
                        }
                        rows.Add(row);
                    }
                }
                finally { sqlite3_finalize(stmt); }
            }
            finally { sqlite3_close(db); }
            return rows;
        }
    }
}
