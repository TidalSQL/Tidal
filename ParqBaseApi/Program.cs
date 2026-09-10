using ParqBaseLib;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddParqBase();

var app = builder.Build();

app.MapPost("/api/sql", (SqlRequest request, ParqBase db) =>
{
    var result = db.ExecuteQuery(request.Statement);
    return Results.Ok(result);
});

app.Run();

record SqlRequest(string Statement);
