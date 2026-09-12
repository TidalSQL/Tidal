using ParqBaseLib;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddParqBase();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Run an arbitrary statement/script against a fresh session. When a database is supplied it is
// selected first (USE) in its own batch, separated by GO, so statements that must be first in a
// batch (CREATE/ALTER/CREATE OR ALTER PROCEDURE, etc.) still parse correctly.
app.MapPost("/api/sql", (SqlRequest request, ParqBase db) =>
{
    var statement = request.Statement ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(request.Database))
    {
        statement = $"USE {request.Database};\nGO\n{statement}";
    }

    var results = db.ExecuteScript(statement, continueOnError: false);
    return Results.Ok(QueryResponse.From(results));
});

// Explorer: list databases.
app.MapGet("/api/databases", (ParqBase db) =>
{
    var result = db.ExecuteQuery("SELECT name FROM sys.databases;");
    return Results.Ok(Column(result, "name"));
});

// Explorer: list the tables in a database.
app.MapGet("/api/databases/{database}/tables", (string database, ParqBase db) =>
{
    var result = db.ExecuteScript($"USE {database};\nSELECT name FROM sys.tables;", continueOnError: false).Last();
    if (!result.Success)
    {
        return Results.BadRequest(new { message = result.Message });
    }

    return Results.Ok(Column(result, "name"));
});

// Table overview: row count, size, and per-column metadata.
app.MapGet("/api/databases/{database}/tables/{table}", (string database, string table, string? schema, ParqBase db) =>
{
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
app.MapGet("/api/databases/{database}/procedures", (string database, ParqBase db) =>
{
    var result = db.ExecuteScript($"USE {database};\nGO\nSELECT name FROM sys.procedures;", continueOnError: false).Last();
    if (!result.Success)
    {
        return Results.BadRequest(new { message = result.Message });
    }

    return Results.Ok(Column(result, "name"));
});

// Stored procedure definition (CREATE OR ALTER script) for the editor.
app.MapGet("/api/databases/{database}/procedures/{procedure}", (string database, string procedure, string? schema, ParqBase db) =>
{
    try
    {
        return Results.Ok(new { definition = db.GetProcedureDefinition(database, procedure, schema ?? "dbo") });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.Run();

static List<string> Column(QueryResult result, string column) =>
    result.Rows
        .Select(r => r.TryGetValue(column, out var v) ? v?.ToString() ?? string.Empty : string.Empty)
        .Where(s => s.Length > 0)
        .ToList();

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
