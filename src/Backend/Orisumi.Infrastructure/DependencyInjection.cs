using Orisumi.Application.Management;
using Orisumi.Infrastructure.Database;
using Orisumi.Application.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Orisumi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrisumiInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<OrisumiDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDataStore, PostgresDataStore>();
        services.AddScoped<DataService>();
        services.AddScoped<WriteService>();
        services.AddScoped<ContentService>();
        services.AddSingleton<IManagementStore, PostgresManagementStore>();
        return services;
    }
}
