using System.Security.Claims;
using System.Threading.RateLimiting;
using Drive;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

if (args.Contains("--healthcheck"))
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var result = await client.GetAsync("http://127.0.0.1:8080/api/health");
        Environment.Exit(result.IsSuccessStatusCode ? 0 : 1);
    }
    catch { Environment.Exit(1); }
    return;
}
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var settings = new Settings(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(settings.Database));
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<Storage>();
builder.Services.AddScoped<Files>();
builder.Services.AddScoped<Uploads>();
builder.Services.AddScoped<Auth>();
builder.Services.AddSingleton<IPasswordHasher<string>>(new PasswordHasher<string>(
    Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 210000 })));
builder.Services.AddHostedService<Cleanup>();
builder.Services.AddDataProtection().SetApplicationName("google-drive-clone")
    .PersistKeysToFileSystem(new DirectoryInfo(settings.KeyDirectory));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "drive.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = settings.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = Settings.PartSize + 65536);
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        // A custom header makes every mutation non-simple; no cross-origin CORS policy is enabled.
        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && context.Request.Headers["X-Drive-Request"] != "1")
        {
            throw new ApiError(403, "Missing request verification header.");
        }

        await next();
    }
    catch (ApiError error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new
        {
            error = error.Message
        });
    }
    catch (PostgresException error) when (error.SqlState == "23505")
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "An account or item with that name already exists."
        });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Endpoint {Endpoint} failed", context.GetEndpoint()?.DisplayName);
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "The operation could not finish. Retry shortly; completed uploads can be safely retried."
        });
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/api/health", () => Results.Ok(new { status = "ready" }));
app.MapPost("/api/auth/register", (HttpContext c, Credentials body, Auth auth) => auth.Register(c, body)).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", (HttpContext c, Credentials body, Auth auth) => auth.Login(c, body)).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization();
app.MapGet("/api/auth/me", (HttpContext c) => new { id = Auth.User(c), email = c.User.FindFirstValue(ClaimTypes.Email) }).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/stats", (HttpContext c, Files files) => files.Stats(Auth.User(c)));
api.MapGet("/entries", (HttpContext c, Files files, Guid? parent, string? search, string? view, int? offset) => files.List(Auth.User(c), parent, search, view, offset ?? 0));
api.MapPost("/folders", (HttpContext c, FolderRequest body, Files files) => files.CreateFolder(Auth.User(c), body));
api.MapGet("/entries/{id:guid}", (HttpContext c, Guid id, Files files) => files.Detail(Auth.User(c), id));
api.MapPatch("/entries/{id:guid}", async (HttpContext c, Guid id, ChangeRequest body, Files files) => { await files.Change(Auth.User(c), id, body); return Results.NoContent(); });
api.MapPost("/entries/{id:guid}/trash", async (HttpContext c, Guid id, Files files) => { await files.Trash(Auth.User(c), id, false); return Results.NoContent(); });
api.MapPost("/entries/{id:guid}/restore", async (HttpContext c, Guid id, Files files) => { await files.Trash(Auth.User(c), id, true); return Results.NoContent(); });
api.MapDelete("/entries/{id:guid}", async (HttpContext c, Guid id, Files files) => { await files.Delete(Auth.User(c), id); return Results.NoContent(); });
api.MapGet("/entries/{id:guid}/versions", (HttpContext c, Guid id, Files files) => files.Versions(Auth.User(c), id));
api.MapPost("/entries/{id:guid}/versions/{version:guid}/restore", async (HttpContext c, Guid id, Guid version, Files files) => { await files.RestoreVersion(Auth.User(c), id, version); return Results.NoContent(); });
api.MapGet("/entries/{id:guid}/download", async (HttpContext c, Guid id, Guid? version, Files files) => Results.Redirect(await files.Download(Auth.User(c), id, version)));
api.MapGet("/entries/{id:guid}/shares", (HttpContext c, Guid id, Files files) => files.Shares(Auth.User(c), id));
api.MapPost("/entries/{id:guid}/shares", (HttpContext c, Guid id, ShareRequest body, Files files) => files.Share(Auth.User(c), id, body));
api.MapPut("/entries/{id:guid}/grants", async (HttpContext c, Guid id, GrantRequest body, Files files) => { await files.Grant(Auth.User(c), id, body); return Results.NoContent(); });
api.MapDelete("/entries/{id:guid}/shares/{share:guid}", async (HttpContext c, Guid id, Guid share, Files files) => { await files.Revoke(Auth.User(c), id, share); return Results.NoContent(); });
api.MapGet("/uploads", (HttpContext c, Uploads uploads) => uploads.List(Auth.User(c)));
api.MapPost("/uploads", (HttpContext c, UploadRequest body, Uploads uploads) => uploads.Start(Auth.User(c), body));
api.MapGet("/uploads/{id:guid}", (HttpContext c, Guid id, Uploads uploads) => uploads.Status(Auth.User(c), id));
api.MapPut("/uploads/{id:guid}/parts/{number:int}", (HttpContext c, Guid id, int number, Uploads uploads) => uploads.Part(Auth.User(c), id, number, c.Request, c.RequestAborted));
api.MapPost("/uploads/{id:guid}/complete", (HttpContext c, Guid id, Uploads uploads) => uploads.Complete(Auth.User(c), id));
api.MapDelete("/uploads/{id:guid}", async (HttpContext c, Guid id, Uploads uploads) => { await uploads.Cancel(Auth.User(c), id); return Results.NoContent(); });
app.MapGet("/api/public/{token}", (string token, Files files) => files.PublicShare(token, false));
app.MapGet("/api/public/{token}/download", async (string token, Files files) => Results.Redirect((string)await files.PublicShare(token, true)));
await app.Services.GetRequiredService<Database>().Initialize();
await app.Services.GetRequiredService<Storage>().Initialize();
app.Run();
