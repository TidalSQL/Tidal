using ParqBaseApi;
using ParqBaseLib;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddParqBase();
builder.Services.AddSingleton<AuthTokenStore>();

var app = builder.Build();

// Seed the default administrator login on first run so there is always a way to sign in.
using (var scope = app.Services.CreateScope())
{
    var seed = scope.ServiceProvider.GetRequiredService<ParqBase>();
    var adminPassword = Environment.GetEnvironmentVariable("PARQBASE_ADMIN_PASSWORD");
    if (string.IsNullOrEmpty(adminPassword))
    {
        adminPassword = "admin";
    }

    var init = seed.InitializeServer(adminPassword);
    if (init.Created)
    {
        app.Logger.LogWarning("First run: created default administrator login '{Login}' with password '{Password}'. Please change it.", init.AdminLogin, init.Password);
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- Authentication -------------------------------------------------------

// Sign in with a server login. On success an opaque session token is stored server-side and returned
// to the browser as an HttpOnly cookie; subsequent requests resume the login from that cookie.
app.MapPost("/api/login", (LoginRequest request, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || request.Password == null || !db.Login(request.Username, request.Password))
    {
        return Results.Json(new { message = "Invalid login or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var token = tokens.Issue(request.Username, db.CurrentLoginIsSysadmin);
    http.Response.Cookies.Append(AuthTokenStore.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
    });

    return Results.Ok(new { login = request.Username, isAdmin = db.CurrentLoginIsSysadmin });
});

// Sign out: revoke the server-side session and clear the cookie.
app.MapPost("/api/logout", (AuthTokenStore tokens, HttpContext http) =>
{
    var token = http.Request.Cookies[AuthTokenStore.CookieName];
    tokens.Revoke(token);
    http.Response.Cookies.Delete(AuthTokenStore.CookieName);
    return Results.Ok(new { ok = true });
});

// Who is signed in (used by the UI on load to decide whether to show the login screen).
app.MapGet("/api/me", (AuthTokenStore tokens, HttpContext http) =>
{
    if (tokens.TryGet(http.Request.Cookies[AuthTokenStore.CookieName], out var s))
    {
        return Results.Ok(new { authenticated = true, login = s.Login, isAdmin = s.IsSysadmin });
    }

    return Results.Ok(new { authenticated = false });
});

// ---- Data endpoints (all require an authenticated session) ----------------

// Run an arbitrary statement/script against the authenticated session. When a database is supplied it
// is selected first (USE) in its own batch, separated by GO, so statements that must be first in a
// batch (CREATE/ALTER/CREATE OR ALTER PROCEDURE, etc.) still parse correctly.
app.MapPost("/api/sql", (SqlRequest request, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    var statement = request.Statement ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(request.Database))
    {
        statement = $"USE {request.Database};\nGO\n{statement}";
    }

    try
    {
        var results = db.ExecuteScript(statement, continueOnError: false, http.RequestAborted);
        return Results.Ok(QueryResponse.From(results));
    }
    catch (OperationCanceledException)
    {
        // The client aborted the request (Cancel button). 499 is the conventional
        // "client closed request" status; the browser has already stopped listening.
        return Results.StatusCode(499);
    }
});

// Explorer: list databases the signed-in user can access (all for admin; empty when signed out).
app.MapGet("/api/databases", (ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    return Results.Ok(db.ListAccessibleDatabases());
});

// The signed-in user's effective permissions in a database (their "T-SQL access").
app.MapGet("/api/me/permissions", (string database, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    try
    {
        return Results.Ok(db.GetMyPermissions(database));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

// Explorer: list the tables in a database (filtered to those the user may read).
app.MapGet("/api/databases/{database}/tables", (string database, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    var result = db.ExecuteScript($"USE {database};\nGO\nSELECT name FROM sys.tables;", continueOnError: false).Last();
    if (!result.Success)
    {
        return Results.BadRequest(new { message = result.Message });
    }

    return Results.Ok(Column(result, "name"));
});

// Table overview: row count, size, and per-column metadata.
app.MapGet("/api/databases/{database}/tables/{table}", (string database, string table, string? schema, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    if (!db.CanViewObject(database, table, schema ?? "dbo"))
    {
        return Results.Json(new { message = $"Permission denied: VIEW DEFINITION on [{table}]." }, statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        return Results.Ok(db.GetTableInfo(database, table, schema ?? "dbo"));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

// Explorer: list the stored procedures in a database.
app.MapGet("/api/databases/{database}/procedures", (string database, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    var result = db.ExecuteScript($"USE {database};\nGO\nSELECT name FROM sys.procedures;", continueOnError: false).Last();
    if (!result.Success)
    {
        return Results.BadRequest(new { message = result.Message });
    }

    return Results.Ok(Column(result, "name"));
});

// Stored procedure definition (CREATE OR ALTER script) for the editor.
app.MapGet("/api/databases/{database}/procedures/{procedure}", (string database, string procedure, string? schema, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        return Unauthorized();
    }

    if (!db.CanViewObject(database, procedure, schema ?? "dbo"))
    {
        return Results.Json(new { message = $"Permission denied: VIEW DEFINITION on [{procedure}]." }, statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        return Results.Ok(new { definition = db.GetProcedureDefinition(database, procedure, schema ?? "dbo") });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

// Stream a table's rows to the client as chunked NDJSON so large tables (e.g. 500k rows) load
// incrementally with bounded server memory and natural backpressure. The first line is a header
// object { columns, types, totalRows }; each subsequent line is a batch { start, rows: [[...]] }
// where each row is positional and aligned to columns. Honors ?batchSize=&offset=&limit=.
app.MapGet("/api/databases/{database}/tables/{table}/stream",
    async (string database, string table, string? schema, int? batchSize, long? offset, long? limit, ParqBase db, AuthTokenStore tokens, HttpContext http) =>
{
    if (!Resume(db, tokens, http))
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await http.Response.WriteAsJsonAsync(new { message = "Not authenticated." });
        return;
    }

    if (!db.CanReadTable(database, table, schema ?? "dbo"))
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsJsonAsync(new { message = $"Permission denied: SELECT on [{table}]." });
        return;
    }

    TableStreamResult stream;
    try
    {
        stream = db.OpenTableStream(
            database,
            table,
            schema ?? "dbo",
            batchSize ?? 10_000,
            offset ?? 0,
            limit);
    }
    catch (Exception ex)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new { message = ex.Message });
        return;
    }

    var ct = http.RequestAborted;
    http.Response.ContentType = "application/x-ndjson";
    http.Response.Headers["Cache-Control"] = "no-cache";

    var header = JsonSerializer.Serialize(new
    {
        columns = stream.Columns.Select(c => c.Name).ToList(),
        types = stream.Columns.Select(c => c.Type).ToList(),
        totalRows = stream.TotalRows,
    });
    await http.Response.WriteAsync(header + "\n", ct);
    await http.Response.Body.FlushAsync(ct);

    try
    {
        await foreach (var batch in stream.Batches.WithCancellation(ct))
        {
            var line = JsonSerializer.Serialize(new { start = batch.StartIndex, rows = batch.Rows });
            await http.Response.WriteAsync(line + "\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Client navigated away / cancelled the stream; nothing more to send.
    }
});

app.Run();

// Resumes the authenticated login for this request from the session cookie. Returns false when the
// request is not authenticated, in which case the caller should return 401.
static bool Resume(ParqBase db, AuthTokenStore tokens, HttpContext http)
{
    if (tokens.TryGet(http.Request.Cookies[AuthTokenStore.CookieName], out var s))
    {
        db.ResumeLogin(s.Login);
        return true;
    }

    return false;
}

static IResult Unauthorized() =>
    Results.Json(new { message = "Not authenticated." }, statusCode: StatusCodes.Status401Unauthorized);

static List<string> Column(QueryResult result, string column) =>
    result.Rows
        .Select(r => r.TryGetValue(column, out var v) ? v?.ToString() ?? string.Empty : string.Empty)
        .Where(s => s.Length > 0)
        .ToList();

record LoginRequest(string Username, string Password);

record SqlRequest(string Statement, string? Database);

/// <summary>Flattened response for the web UI: the grid comes from the last result that produced
/// columns; messages concatenate every statement's message.</summary>
record QueryResponse(bool Success, string Message, List<string> Columns, List<Dictionary<string, object?>> Rows, int RowCount)
{
    public static QueryResponse From(IReadOnlyList<QueryResult> results)
    {
        var success = results.All(r => r.Success);
        var grid = results.LastOrDefault(r => r.Columns.Count > 0);
        var message = string.Join("\n", results.Select(r => r.Message).Where(m => !string.IsNullOrWhiteSpace(m)));

        return grid != null
            ? new QueryResponse(success, message, grid.Columns, grid.Rows, grid.RowCount)
            : new QueryResponse(success, message, new(), new(), 0);
    }
}
