// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Tests.Integration.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Andy.Policies.Tests.Integration.Fixtures;

/// <summary>
/// Test factory that wires the real
/// <see cref="Andy.Policies.Infrastructure.Services.Rbac.HttpRbacChecker"/>
/// to a <see cref="RbacStubFixture"/>'s <c>WireMockServer</c>. Unlike
/// <see cref="PoliciesApiFactory"/>, this factory does <b>not</b>
/// install an allow-all stub for <c>IRbacChecker</c> — it leaves the
/// production typed <c>HttpClient</c> in place so the full path
/// (controller attribute → handler → checker → HTTP body) runs end
/// to end.
/// </summary>
public sealed class RbacTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly RbacStubFixture _rbac;
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public RbacTestApplicationFactory(RbacStubFixture rbac)
    {
        _rbac = rbac;
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["AndyAuth:Authority"] = "https://test-auth.invalid",
                ["AndySettings:ApiBaseUrl"] = "https://test-settings.invalid",
                // Point HttpRbacChecker at the WireMock stub. This is the
                // critical difference vs PoliciesApiFactory.
                ["AndyRbac:BaseUrl"] = _rbac.BaseUrl,
            });
        });

        builder.ConfigureServices(services =>
        {
            var ctxDescriptor = services.SingleOrDefault(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (ctxDescriptor is not null) services.Remove(ctxDescriptor);
            services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(opts =>
            {
                opts.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build();
            });

            // P8.4 (#84): same stub posture as PoliciesApiFactory —
            // legacy P7.5 RBAC tests don't pass `?bundleId=` and
            // shouldn't be 400'd by the gate. The pinning gate has
            // its own dedicated tests.
            var pinDescriptors = services
                .Where(d => d.ServiceType == typeof(Andy.Policies.Application.Interfaces.IPinningPolicy))
                .ToList();
            foreach (var d in pinDescriptors) services.Remove(d);
            services.AddSingleton<Andy.Policies.Application.Interfaces.IPinningPolicy>(
                new PoliciesApiFactory.StaticPinningPolicy(required: false));

            // P9 follow-up #193: rationale gate stubbed off so RBAC-focused
            // tests don't have to thread rationale through every CreateDraft
            // call. RationaleEnforcementTests use their own snapshot factory.
            services.StubRationaleOff();

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
