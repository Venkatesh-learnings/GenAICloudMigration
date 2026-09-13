using Microsoft.EntityFrameworkCore;
using UrlShortener.Api;
using UrlShortener.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "URL Shortener API",
        Version = "v1",
        Description = "Core APIs for creating short URLs, redirecting, and viewing click analytics."
    });
});

builder.Services.AddUrlShortenerInfrastructure(builder.Configuration);
builder.Services.AddUrlShortenerRateLimiting();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<UrlShortenerDbContext>();
    dbContext.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program
{
}
