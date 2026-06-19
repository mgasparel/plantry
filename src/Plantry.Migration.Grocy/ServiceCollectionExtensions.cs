using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Plantry.Migration.Grocy;

/// <summary>
/// DI registration for the Grocy import library.
/// Call <see cref="AddGrocyImport"/> from <c>Plantry.Web/Program.cs</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GrocyClient"/> as a typed <see cref="System.Net.Http.HttpClient"/>
    /// and <see cref="ExtractCommand"/> as a scoped service.
    /// Configuration is read from the <c>Grocy</c> section (user secrets in dev, env vars in prod).
    /// </summary>
    public static IServiceCollection AddGrocyImport(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<GrocyOptions>(configuration.GetSection(GrocyOptions.SectionName));

        services.AddHttpClient<GrocyClient>((sp, http) =>
        {
            // BaseAddress and API key header are set inside GrocyClient's constructor
            // from the injected IOptions<GrocyOptions> — nothing to configure here.
        });

        services.AddScoped<ExtractCommand>();
        services.AddScoped<UnitCommitService>();
        services.AddScoped<CategoryCommitService>();
        services.AddScoped<LocationCommitService>();
        services.AddScoped<ProductCommitService>();

        return services;
    }
}
