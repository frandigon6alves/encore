using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Encore.Core.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// One NpgsqlDataSource (connection pool) shared by EF Core and Dapper.
    /// EF Core is registered as a pooled factory so background services and GraphQL
    /// resolvers can create short-lived contexts; a scoped context is built from it for endpoints.
    /// </summary>
    public static IServiceCollection AddEncoreData(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        DefaultTypeMap.MatchNamesWithUnderscores = true; // Dapper: snake_case columns -> PascalCase props

        services.AddNpgsqlDataSource(connectionString);
        services.AddPooledDbContextFactory<EncoreDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npg => npg.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention());
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<EncoreDbContext>>().CreateDbContext());

        return services;
    }
}
