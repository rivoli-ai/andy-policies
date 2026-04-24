// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Tests.Integration.Controllers;

/// <summary>
/// Test factory that swaps the registered <see cref="AppDbContext"/> for a SQLite
/// in-memory backing store and runs <c>EnsureCreated</c> at startup. Avoids the
/// pre-existing <c>ItemsControllerTests</c> failure mode (default factory tries to
/// migrate against Postgres on :5439 because of the auto-migrate in <c>Program.cs</c>).
/// </summary>
public sealed class PoliciesApiFactory : WebApplicationFactory<Program>
{
    // Connection lives for the lifetime of the factory; closing it would drop the
    // in-memory database. SQLite's :memory: store is per-connection, so a single
    // shared SqliteConnection underpins every DbContext scoped from DI.
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public PoliciesApiFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" environment skips the Development-gated auto-migrate block in
        // Program.cs (which would otherwise try Postgres on :5439).
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                // Empty string disables andy-auth bearer setup → AllowAnonymous default.
                ["AndyAuth:Authority"] = "",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Drop the production DbContext registration before re-adding ours.
            var ctxDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (ctxDescriptor is not null) services.Remove(ctxDescriptor);

            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

            // Replace the auth-bypass scheme (which actually fails [Authorize] because
            // it has no default scheme) with a test handler that always issues a
            // principal. Production auth (JWT Bearer) is unchanged outside this factory.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(opts =>
            {
                opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            // Build the schema once on the shared connection.
            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connection.Dispose();
        base.Dispose(disposing);
    }
}
