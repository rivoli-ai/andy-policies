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
    /// and runs the boot-time seeders against it. Order is load-bearing:
    /// <list type="number">
    ///   <item><see cref="PolicySeeder.SeedStockPoliciesAsync"/> (P1.3 #73,
    ///     extended by SD4.1 #1181) — the six canonical lifecycle policies
    ///     in <see cref="Domain.Enums.LifecycleState.Active"/>.</item>
    ///   <item><see cref="ScopeSeeder.SeedRootScopeAsync"/> (P4.1 #28) —
    ///     the single root Org node so P4.2's CRUD endpoints have a parent
    ///     to attach children under.</item>
    ///   <item><see cref="BindingSeeder.SeedDefaultBindingsAsync"/> (SD4.2
    ///     #1182) — default agent → policy bindings for the six seeded
    ///     agents from SD2. Runs after the policy seeder so the Active
    ///     version ids exist to bind against.</item>
    /// </list>
    /// All three are idempotent — safe to call on every boot. Must run
    /// after migrations have applied; if the schema is missing the
    /// underlying probe throws and boot fails loudly.
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
        // SD4.2 (rivoli-ai/andy-policies#1182): bind each seeded agent
        // (triage / research / planning / coding / validation / review)
        // to its required policies at root scope. Must run after
        // PolicySeeder so the active version ids exist.
        await BindingSeeder.SeedDefaultBindingsAsync(db, ct).ConfigureAwait(false);
    }
}
