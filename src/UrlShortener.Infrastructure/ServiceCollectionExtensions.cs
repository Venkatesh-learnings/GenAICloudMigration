using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUrlShortenerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("UrlShortener") ?? "Data Source=urlshortener.db";

        services.AddDbContext<UrlShortenerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IShortUrlRepository, ShortUrlRepository>();
        services.AddScoped<IClickEventRepository, ClickEventRepository>();
        services.AddSingleton<IShortCodeGenerator, ShortCodeGenerator>();
        services.AddScoped<IUrlShortenerService, UrlShortenerService>();
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
