using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Infrastructure;

namespace UrlShortener.Tests;

/// <summary>
/// Boots the real API in-memory via WebApplicationFactory, but swaps the file-based SQLite
/// DbContext registration for one backed by a single open ":memory:" SQLite connection.
/// Program.cs still calls dbContext.Database.Migrate() during startup - that works fine
/// against a real (if in-memory) SQLite database via the project's existing EF Core
/// migrations, so schema creation stays on the same code path production uses.
/// Each factory instance gets its own fresh, isolated, empty database.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public CustomWebApplicationFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<UrlShortenerDbContext>>();
            services.AddDbContext<UrlShortenerDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
