# TidalSql

**A T-SQL query engine over a Parquet data lake.** TidalSql lets you keep your
data as plain [Apache Parquet](https://parquet.apache.org/) files on disk and
query, join, and modify it with familiar **T-SQL** syntax — no server product,
no proprietary storage format. It parses SQL with the same grammar SQL Server
uses ([`Microsoft.SqlServer.TransactSql.ScriptDom`](https://learn.microsoft.com/dotnet/api/microsoft.sqlserver.transactsql.scriptdom))
and materializes results directly from your Parquet files.

> Status: **active development / pre-release.** The engine, console, and web app
> are functional and covered by 150 automated tests, but the SQL surface is a
> growing subset of T-SQL rather than a full implementation. See
> [Feature status](#feature-status).

---

## Highlights

- **T-SQL over Parquet** — `SELECT` with `JOIN`s, subqueries, `WHERE`, `GROUP BY`,
  aggregates, and DML, executed against Parquet files as tables.
- **Real T-SQL parser** — statements are parsed with SQL Server's `ScriptDom`,
  not a hand-rolled grammar.
- **Databases, schemas, and tables** — a familiar catalog model backed by
  directories of Parquet files.
- **Stored procedures** — define and `EXEC` T-SQL stored procedures.
- **Security model** — server logins, roles, per-database read/write roles, and
  `EXECUTE AS USER` impersonation.
- **Read-only Delta Lake interop** — point at an external Delta table (with a
  `_delta_log`) and read its current snapshot, including partition columns.
  Unsupported reader features are rejected rather than silently misread.
- **Concurrency & durability** — per-table locking, atomic writes, and query
  cancellation.
- **Streaming & GPU paths** — streaming aggregate pushdown for large scans and an
  optional GPU column-math path via [ILGPU](https://ilgpu.net/).
- **Two front ends** — an interactive console (REPL) and a web app with a SQL
  editor plus a REST API.
- **Locked-down storage** — data lives in a machine-wide `%ProgramData%\TidalSql`
  root that can be ACL-hardened to a dedicated service account, the way SQL
  Server protects its data files.

---

## Repository layout

| Path | Description |
|------|-------------|
| `TidalSqlLib/` | The engine: T-SQL visitor, Parquet I/O, Delta reader, security, locking, GPU math. |
| `TidalSqlConsole/` | Interactive console (REPL) host. |
| `TidalSqlApi/` | ASP.NET Core web app: static SQL-editor UI (`wwwroot/`) + REST API. |
| `TidalSqlTests/` | MSTest suite (150 tests). |
| `TidalScripts/` | Example SQL (e.g. `build.sql`). |
| `tools/Secure-DataDir.ps1` | Elevated script to ACL-lock the data directory. |
| `docs/` | User guide (`TidalSql-User-Guide.docx`) and its generator. |

The engine is split into focused partial classes under `TidalSqlLib/`:
`TidalSqlStatementVisitor.{Joins,Aggregation,Dml,Procedures,Security,Metadata,MultiPart,Delta,StreamingAggregate,Expressions,Emitters}.cs`.

---

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows (the default data root uses `%ProgramData%`; other platforms work via
  the `TIDALSQL_DATA_ROOT` override)
- An optional GPU is auto-detected; the engine falls back to CPU when none is
  available.

Key dependencies: `Parquet.Net`, `Microsoft.SqlServer.TransactSql.ScriptDom`,
`ILGPU`, `Microsoft.Extensions.Hosting`.

---

## Getting started

Clone and build:

```powershell
git clone https://github.com/TidalSQL/Tidal.git
cd Tidal
dotnet build TidalSql.sln
```

### Run the console (REPL)

```powershell
dotnet run --project TidalSqlConsole
```

On first run an `admin` login is created. Set its initial password with the
`TIDALSQL_ADMIN_PASSWORD` environment variable (otherwise a temporary password is
generated and printed — change it after signing in).

In the REPL you can:

- Type any T-SQL statement and press Enter to execute it.
- Run a script file: `:r <path>` or `run <path>` (sqlcmd-style).
- Exit with `exit` or `quit`.

```sql
USE master;
CREATE DATABASE demo;
USE demo;
SELECT TOP 10 * FROM orders;
```

### Run the web app

```powershell
dotnet run --project TidalSqlApi
```

Then open the printed URL. The app serves a SQL editor from `wwwroot/` and a REST
API (log in, run SQL, browse databases/tables/procedures, manage logins/roles).

Selected REST endpoints:

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/api/login`, `/api/logout` | Authenticate / end session |
| `GET` | `/api/me`, `/api/me/permissions` | Current user + permissions |
| `POST` | `/api/sql` | Execute a SQL batch |
| `GET` | `/api/databases` | List databases |
| `GET` | `/api/databases/{db}/tables` | List tables (and `/{table}` for schema) |
| `GET` | `/api/databases/{db}/tables/{table}/stream` | Stream table rows |
| `GET` | `/api/databases/{db}/procedures` | List stored procedures |
| `GET` | `/api/server/logins`, `/api/server/roles` | Security catalog |
| `POST`/`DELETE` | `/api/server/logins`, `/api/server/roles/{role}/members` | Manage logins & role membership |
| `POST` | `/api/databases/{db}/access` | Grant per-database read/write access |

---

## Data storage & security

All persistent state lives under a single **data root**:

```
%ProgramData%\TidalSql\
  databases\   # one folder per database; tables are Parquet files
  system\      # server-scoped security/system catalog (logins, roles)
```

Override the location with the `TIDALSQL_DATA_ROOT` environment variable.

Because the data root is a machine-wide location outside any user's source tree,
it can be locked down at the filesystem level. `tools/Secure-DataDir.ps1`
(run elevated) resets inheritance and grants access only to a dedicated service
account, Administrators, and SYSTEM — removing `Users`/`Everyone` so ordinary
users cannot read or tamper with the Parquet files directly:

```powershell
# Preview
pwsh -File tools\Secure-DataDir.ps1 -ServiceAccount "DOMAIN\svcTidalSql" -WhatIf
# Apply
pwsh -File tools\Secure-DataDir.ps1 -ServiceAccount "DOMAIN\svcTidalSql"
```

### Environment variables

| Variable | Effect |
|----------|--------|
| `TIDALSQL_DATA_ROOT` | Override the data root (default `%ProgramData%\TidalSql`). |
| `TIDALSQL_ADMIN_PASSWORD` | Initial password for the auto-created `admin` login. |

---

## Delta Lake interop (read-only)

TidalSql can read an **external Delta Lake table** by replaying its `_delta_log`
(commit JSON + checkpoint Parquet), returning the current snapshot with
tombstoned files excluded and partition columns projected in. This is
**read-only**: TidalSql does not write Delta tables, and it rejects tables whose
reader-protocol features it does not support (e.g. deletion vectors) rather than
returning wrong rows. Time-travel-by-version is implemented in the reader but not
yet exposed through SQL syntax.

---

## Feature status

**Supported:** databases/schemas/tables over Parquet; `SELECT` with `JOIN`s and
subqueries; `WHERE`/`GROUP BY`/aggregates; DML; stored procedures; logins, roles,
per-database read/write roles and `EXECUTE AS USER`; multi-part Parquet tables;
read-only Delta interop; per-table locking, atomic writes, and query
cancellation; streaming aggregate pushdown; optional GPU column math.

**Not yet supported / in progress:** full T-SQL surface area (it is a growing
subset), writing Delta tables, and Delta time-travel SQL syntax.

---

## Testing

```powershell
dotnet test TidalSql.sln
```

The suite (`TidalSqlTests`, MSTest, run sequentially) currently has **150 tests**
covering joins/subqueries, aggregation, DML, procedures, security, multi-part and
Delta tables, streaming, locking, and the web access layer.

---

## Documentation

A full user guide is in `docs/TidalSql-User-Guide.docx`. It is generated from
`docs/_generate_user_guide.py` (via `python-docx`) — edit the script and re-run it
to regenerate the document:

```powershell
python docs/_generate_user_guide.py
```

---

## License

_No license file is currently present in the repository. Add one before public
release to clarify usage terms._
