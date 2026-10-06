namespace TidalDbTests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using TidalDbLib;

    /// <summary>
    /// Seeds a small, canonical TPC-H data set into the active data root so the
    /// <see cref="TpchMultiPartTests"/> integration tests have real data to run against instead of
    /// being skipped. Only the two fixed reference tables are generated — <c>region</c> (5 rows) and
    /// <c>nation</c> (25 rows) — using the standard TPC-H values from the specification. Each table is
    /// laid out as a genuine multi-part table (<c>databases/tpch/parquet/&lt;table&gt;/part-*.parquet</c>)
    /// so the tests actually exercise reading across parts.
    ///
    /// <para>The Parquet part files are produced by the engine itself: a throwaway database is created,
    /// a chunk of rows is inserted (which writes a single-file Parquet table), and the resulting file is
    /// moved under the <c>tpch</c> database's <c>parquet/</c> container as one part. This keeps the data
    /// set self-contained and deterministic without committing binary Parquet blobs to the repository.</para>
    /// </summary>
    internal static class TpchDataSet
    {
        private static readonly object SeedGate = new object();

        /// <summary>
        /// Ensures the <c>tpch</c> database exists with multi-part <c>region</c> and <c>nation</c> tables.
        /// Idempotent: returns immediately when the data is already present.
        /// </summary>
        internal static void EnsureSeeded()
        {
            lock (SeedGate)
            {
                var tpchPath = Path.Combine(TidalDb.DatabasesRoot, "tpch");
                var regionDir = Path.Combine(tpchPath, "parquet", "region");
                if (Directory.Exists(regionDir) &&
                    Directory.GetFiles(regionDir, "part-*.parquet").Length > 0)
                {
                    return;
                }

                var admin = new TidalDb();
                admin.ExecuteStatement("create database tpch");

                SeedTable(
                    tpchPath,
                    table: "region",
                    columnsDdl: "r_regionkey INT, r_name NVARCHAR(25), r_comment NVARCHAR(152)",
                    columns: new[] { "r_regionkey", "r_name", "r_comment" },
                    rows: RegionRows(),
                    partCount: 2);

                SeedTable(
                    tpchPath,
                    table: "nation",
                    columnsDdl: "n_nationkey INT, n_name NVARCHAR(25), n_regionkey INT, n_comment NVARCHAR(152)",
                    columns: new[] { "n_nationkey", "n_name", "n_regionkey", "n_comment" },
                    rows: NationRows(),
                    partCount: 2);
            }
        }

        private static void SeedTable(
            string tpchPath, string table, string columnsDdl, string[] columns, List<string> rows, int partCount)
        {
            var destDir = Path.Combine(tpchPath, "parquet", table);
            Directory.CreateDirectory(destDir);

            var chunks = Split(rows, partCount);
            var columnList = string.Join(", ", columns);

            for (var part = 0; part < chunks.Count; part++)
            {
                if (chunks[part].Count == 0)
                {
                    continue;
                }

                var seedDbName = "tpchseed_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var session = new TidalDb();
                Require(session.ExecuteQuery($"create database {seedDbName}"));
                Require(session.ExecuteQuery($"use {seedDbName}"));
                var seedDbPath = session.CurrentDatabasePath!;

                Require(session.ExecuteQuery($"CREATE TABLE dbo.{table} ({columnsDdl});"));
                var values = string.Join(", ", chunks[part]);
                Require(session.ExecuteQuery($"INSERT INTO {table} ({columnList}) VALUES {values};"));

                var source = Path.Combine(seedDbPath, "tables", table + ".parquet");
                if (!File.Exists(source))
                {
                    throw new InvalidOperationException($"Expected seed Parquet file was not produced: {source}");
                }

                File.Copy(source, Path.Combine(destDir, $"part-{part}.parquet"), overwrite: true);

                try { Directory.Delete(seedDbPath, recursive: true); } catch { /* best effort cleanup */ }
            }
        }

        private static void Require(QueryResult result)
        {
            if (!result.Success)
            {
                throw new InvalidOperationException("TPC-H seeding statement failed: " + result.Message);
            }
        }

        private static List<List<string>> Split(List<string> rows, int partCount)
        {
            var parts = new List<List<string>>();
            for (var i = 0; i < partCount; i++)
            {
                parts.Add(new List<string>());
            }

            for (var i = 0; i < rows.Count; i++)
            {
                parts[i % partCount].Add(rows[i]);
            }

            return parts;
        }

        // Canonical TPC-H REGION table (5 rows). Comments are left empty; the tests only depend on
        // row counts and table presence, not comment text.
        private static List<string> RegionRows()
        {
            var names = new[] { "AFRICA", "AMERICA", "ASIA", "EUROPE", "MIDDLE EAST" };
            return names
                .Select((name, key) => $"({key}, '{name}', '')")
                .ToList();
        }

        // Canonical TPC-H NATION table (25 rows: n_nationkey, n_name, n_regionkey).
        private static List<string> NationRows()
        {
            var nations = new (int Key, string Name, int RegionKey)[]
            {
                (0, "ALGERIA", 0), (1, "ARGENTINA", 1), (2, "BRAZIL", 1), (3, "CANADA", 1),
                (4, "EGYPT", 4), (5, "ETHIOPIA", 0), (6, "FRANCE", 3), (7, "GERMANY", 3),
                (8, "INDIA", 2), (9, "INDONESIA", 2), (10, "IRAN", 4), (11, "IRAQ", 4),
                (12, "JAPAN", 2), (13, "JORDAN", 4), (14, "KENYA", 0), (15, "MOROCCO", 0),
                (16, "MOZAMBIQUE", 0), (17, "PERU", 1), (18, "CHINA", 2), (19, "ROMANIA", 3),
                (20, "SAUDI ARABIA", 4), (21, "VIETNAM", 2), (22, "RUSSIA", 3),
                (23, "UNITED KINGDOM", 3), (24, "UNITED STATES", 1),
            };

            return nations
                .Select(n => $"({n.Key}, '{n.Name}', {n.RegionKey}, '')")
                .ToList();
        }
    }
}
