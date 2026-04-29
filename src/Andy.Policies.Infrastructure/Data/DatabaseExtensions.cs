// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Infrastructure.Data;

public static class DatabaseExtensions
{
    public static IServiceCollection AddAppDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Database:Provider") ?? "PostgreSql";
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<AppDbContext>(options =>
        {
            switch (provider)
            {
                case "Sqlite":
                    options.UseSqlite(connectionString ?? "Data Source=andy_policies.db");
                    break;
                default:
                    options.UseNpgsql(connectionString);
                    break;
            }
        });

        return services;
    }

    /// <summary>
    /// Resolves <see cref="AppDbContext"/> from the root provider in a fresh scope
    /// and runs <see cref="PolicySeeder.SeedStockPoliciesAsync"/> against it
    /// (P1.3, #73). Idempotent — safe to call on every boot. Must run after
    /// migrations have applied; if the schema is missing the underlying
    /// <c>AnyAsync</c> probe throws and boot fails loudly.
    /// </summary>
    public static async Task EnsureSeedDataAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PolicySeeder.SeedStockPoliciesAsync(db, ct).ConfigureAwait(false);
        // P4.1 (rivoli-ai/andy-policies#28): seed a single root Org node so
        // P4.2's CRUD endpoints have a parent to attach children under.
        // Idempotent — short-circuits when any root (ParentId IS NULL) exists.
        await ScopeSeeder.SeedRootScopeAsync(db, ct).ConfigureAwait(false);
    }
}
