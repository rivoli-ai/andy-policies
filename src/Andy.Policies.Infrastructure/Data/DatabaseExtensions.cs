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
}
