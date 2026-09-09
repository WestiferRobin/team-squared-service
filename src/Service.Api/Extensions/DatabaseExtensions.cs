using Microsoft.EntityFrameworkCore;
using Service.Api.Infrastructure.Database;
using Service.Api.Infrastructure.Database.Repositories.Action;
using Service.Api.Infrastructure.Database.Repositories.Item;

namespace Service.Api.Extensions;

public static class DatabaseExtensions
{
    public static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<ServiceDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres"),
                postgres => postgres.CommandTimeout(5)));
        services.AddScoped<IItemRepository, ItemRepository>();
        services.AddScoped<IActionRepository, ActionRepository>();
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

        return services;
    }
}
