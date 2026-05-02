// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Infrastructure.Services;
using Andy.Settings.Client;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Database ---
builder.Services.AddAppDatabase(builder.Configuration);

// --- Authentication (Andy Auth) ---
// SECURITY: There is no auth-bypass branch by design. If AndyAuth:Authority is
// not configured the service refuses to start — failing loud beats silently
// shipping an open-to-anonymous deployment. Tests register their own
// authentication scheme inside their WebApplicationFactory; they do not depend
// on a production bypass. See rivoli-ai/andy-policies#103.
var andyAuthAuthority = builder.Configuration["AndyAuth:Authority"];
if (string.IsNullOrEmpty(andyAuthAuthority))
{
    throw new InvalidOperationException(
        "AndyAuth:Authority is not configured. Set it via appsettings, environment " +
        "(AndyAuth__Authority=https://...), or Andy Settings. For local dev, run " +
        "andy-auth (e.g. `docker compose up andy-auth`) and point at https://localhost:5001.");
}

var audience = builder.Configuration["AndyAuth:Audience"] ?? "urn:andy-policies-api";
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = andyAuthAuthority;
        options.Audience = audience;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        if (builder.Environment.IsDevelopment())
        {
            options.BackchannelHttpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            options.TokenValidationParameters.ValidIssuers = new[]
            {
                andyAuthAuthority, andyAuthAuthority.TrimEnd('/') + "/",
                "https://localhost:5001", "https://localhost:5001/"
            };
        }
    });
builder.Services.AddAuthorization();

// --- RBAC (Andy.Rbac.Client) ---
var rbacBaseUrl = builder.Configuration["Rbac:ApiBaseUrl"];
if (!string.IsNullOrEmpty(rbacBaseUrl) && builder.Environment.IsDevelopment())
{
    builder.Services.ConfigureHttpClientDefaults(b =>
    {
        b.ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
    });
}

// --- Andy Settings (centralized configuration) ---
// SECURITY: Same fail-loud posture as #103 — no silent dev bypass. If
// AndySettings:ApiBaseUrl isn't configured the service refuses to start.
// andy-policies sources its policy gates (rationale required, override
// enablement, bundle pinning, audit retention) from andy-settings; missing
// config would silently default behaviors and is unsafe. See #108.
//
// AddAndySettingsClient registers IAndySettingsClient (HTTP), an
// ISettingsSnapshot (cached settings), and a hosted SettingsRefreshService
// that pulls updates on a configurable cadence. Reads are lazy through the
// snapshot — the service starts even if andy-settings is briefly unreachable,
// but consumer code that demands a setting before the first refresh will
// surface as 5xx.
var settingsBaseUrl = builder.Configuration["AndySettings:ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(settingsBaseUrl))
{
    throw new InvalidOperationException(
        "AndySettings:ApiBaseUrl is not configured. Set it via appsettings, " +
        "environment (AndySettings__ApiBaseUrl=https://...), or Andy Settings " +
        "bootstrap. For local dev, run andy-settings (e.g. `docker compose up " +
        "andy-settings`) and point at https://localhost:5300 — or use the " +
        "compose stack in docker-compose.e2e.yml.");
}
builder.Services.AddAndySettingsClient(builder.Configuration);

// --- Services ---
builder.Services.AddScoped<IItemService, ItemService>();
builder.Services.AddScoped<IPolicyService, PolicyService>();
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IBindingService, Andy.Policies.Infrastructure.Services.BindingService>();
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IBindingResolver, Andy.Policies.Infrastructure.Services.BindingResolver>();
// P4.2 (#29): hierarchy CRUD + walk-up. Consumed by P4.3 binding
// resolution and P4.4 tighten-only validation.
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IScopeService, Andy.Policies.Infrastructure.Services.ScopeService>();
// P4.3 (#30): stricter-tightens-only resolver — walks the scope chain
// from root to leaf and merges ScopeNode-targeted bindings with the
// bridge bindings that target each chain node's external Ref.
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IBindingResolutionService, Andy.Policies.Infrastructure.Services.BindingResolutionService>();
// P4.4 (#32): write-time enforcement of stricter-tightens-only.
// Injected into BindingService so CreateAsync refuses Recommended
// proposals that would shadow a Mandatory ancestor binding.
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.ITightenOnlyValidator, Andy.Policies.Infrastructure.Services.TightenOnlyValidator>();
// P3.2 (#20): Binding mutations call IAuditWriter — Epic P6
// (rivoli-ai/andy-policies#6) replaces the no-op with the real
// hash-chained writer. Singleton because the P6 implementation will own a
// content-addressed sequence pointer.
builder.Services.AddSingleton<Andy.Policies.Application.Interfaces.IAuditWriter, Andy.Policies.Infrastructure.Services.NoopAuditWriter>();
// P6.2 (#42): tamper-evident catalog audit chain. Scoped because
// AuditChain depends on the scoped AppDbContext; downstream callers
// invoke AppendAsync inside their own DbContext transaction so
// state-change + audit-row commit atomically.
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IAuditChain, Andy.Policies.Infrastructure.Audit.AuditChain>();
// P6.6 (#46): cursor-paginated query over audit_events. Scoped
// because AuditQuery depends on the scoped AppDbContext; the
// service is read-only (no transactions, no advisory locks).
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IAuditQuery, Andy.Policies.Infrastructure.Audit.AuditQuery>();
// P6.7 (#48): streaming NDJSON exporter for the audit chain.
// Scoped because AuditExporter depends on the scoped AppDbContext;
// the exporter itself is read-only (no transactions).
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IAuditExporter, Andy.Policies.Infrastructure.Audit.AuditExporter>();
// P6.3 (#43): RFC 6902 JSON Patch diff generator. Singleton
// because the implementation is pure + caches reflection
// metadata per type; injected into every mutating service so
// audit envelope diffs are produced uniformly.
builder.Services.AddSingleton<Andy.Policies.Application.Interfaces.IAuditDiffGenerator, Andy.Policies.Infrastructure.Audit.JsonPatchDiffGenerator>();
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.ILifecycleTransitionService, Andy.Policies.Infrastructure.Services.LifecycleTransitionService>();
// P5.2 (#52): override propose/approve/revoke service. AllowAllRbacChecker
// is a placeholder until P7.2 (#51) wires the real andy-rbac client; the
// JWT layer at the API edge still enforces authentication, so the
// "allow-all" semantics apply only to the subject→permission check.
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IOverrideService, Andy.Policies.Infrastructure.Services.OverrideService>();
builder.Services.AddScoped<Andy.Policies.Application.Interfaces.IRbacChecker, Andy.Policies.Infrastructure.Services.AllowAllRbacChecker>();
// P5.3 (#53): periodic sweep that transitions Approved overrides past
// ExpiresAt into Expired. Runs even when the experimental-overrides
// gate is off — turning the feature off must not strand previously
// approved overrides past expiry. Cadence reads live from
// andy-settings (key andy.policies.overrideExpiryReaperCadenceSeconds).
builder.Services.AddHostedService<Andy.Policies.Infrastructure.BackgroundServices.OverrideExpiryReaper>();
// P5.4 (#56): runtime gate over andy.policies.experimentalOverridesEnabled.
// Singleton because the impl owns the OTel gauge meter; the underlying
// ISettingsSnapshot is also a singleton. Surface filters (P5.5+) read
// IExperimentalOverridesGate.IsEnabled per-request and translate "off"
// to the surface's equivalent of HTTP 403.
builder.Services.AddSingleton<Andy.Policies.Application.Settings.IExperimentalOverridesGate, Andy.Policies.Infrastructure.Settings.ExperimentalOverridesGate>();
// P2.4 (#14): the rationale policy reads andy.policies.rationaleRequired from
// the andy-settings snapshot on every check (fail-safe to required=true if the
// snapshot has not yet observed the key). Registered as a singleton because it
// owns the metric Meter; the underlying ISettingsSnapshot is also a singleton.
builder.Services.AddSingleton<Andy.Policies.Application.Interfaces.IRationalePolicy, Andy.Policies.Infrastructure.Services.AndySettingsRationalePolicy>();
builder.Services.AddSingleton<Andy.Policies.Application.Interfaces.IDomainEventDispatcher, Andy.Policies.Infrastructure.Services.InProcessDomainEventDispatcher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDataProtection();

// --- Exception handlers ---
builder.Services.AddExceptionHandler<Andy.Policies.Api.ExceptionHandlers.PolicyExceptionHandler>();
builder.Services.AddProblemDetails();

// --- OpenTelemetry ---
var otelServiceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "andy-policies-api";
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(res => res.AddService(otelServiceName))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddEntityFrameworkCoreInstrumentation();
        if (!string.IsNullOrEmpty(otlpEndpoint))
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
               .AddHttpClientInstrumentation()
               .AddRuntimeInstrumentation()
               .AddMeter(Andy.Policies.Infrastructure.Services.AndySettingsRationalePolicy.MeterName)
               .AddMeter(Andy.Policies.Infrastructure.BackgroundServices.OverrideExpiryReaper.MeterName)
               .AddMeter(Andy.Policies.Infrastructure.Settings.ExperimentalOverridesGate.MeterName);
        if (!string.IsNullOrEmpty(otlpEndpoint))
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
    });

// --- Swagger ---
builder.Services.AddControllers(options =>
    {
        // P6.4 (#44): defense-in-depth rationale enforcement.
        // The filter checks IRationalePolicy.IsRequired on every
        // mutating request and rejects empty / whitespace-only
        // rationales with 400 ProblemDetails *before* the service
        // layer runs. The service-level RationaleRequiredException
        // path remains as a second guarantee for code paths that
        // bypass the filter (e.g. background workers).
        options.Filters.Add<Andy.Policies.Api.Filters.RationaleRequiredFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Andy Policies API",
        Version = "v1",
        Description = "Governance policy catalog — structured, versioned policy documents with lifecycle and audit trail, consumed by Conductor for story admission, verification, and compliance reporting (content only; enforcement lives in consumers).",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "Rivoli AI",
            Url = new Uri("https://github.com/rivoli-ai/andy-policies"),
        },
        License = new Microsoft.OpenApi.Models.OpenApiLicense
        {
            Name = "Apache-2.0",
            Url = new Uri("https://www.apache.org/licenses/LICENSE-2.0"),
        },
    });

    // Stable, code-derived operationIds keep the schema diff-friendly. Without
    // this Swashbuckle synthesises names that depend on overload order, which
    // makes the drift check noisy. Spectral's `operation-operationId` rule
    // requires every operation to have a non-null operationId. The fallbacks
    // cover minimal-API endpoints (e.g. `/health`) that have no controller /
    // action route values.
    options.CustomOperationIds(api =>
    {
        var routeValues = api.ActionDescriptor.RouteValues;
        if (routeValues.TryGetValue("controller", out var ctrl)
            && routeValues.TryGetValue("action", out var act)
            && !string.IsNullOrEmpty(ctrl) && !string.IsNullOrEmpty(act))
        {
            return $"{ctrl}_{act}";
        }
        return api.RelativePath?.Replace('/', '_').Trim('_') ?? api.HttpMethod ?? "operation";
    });

    foreach (var xml in new[] { "Andy.Policies.Api.xml", "Andy.Policies.Application.xml" })
    {
        var path = Path.Combine(AppContext.BaseDirectory, xml);
        if (File.Exists(path))
        {
            options.IncludeXmlComments(path, includeControllerXmlComments: true);
        }
    }

    options.SchemaFilter<Andy.Policies.Api.Swagger.PolicyDimensionSchemaFilter>();
    options.DocumentFilter<Andy.Policies.Api.Swagger.OverridesDocumentFilter>();

    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT Authorization header using the Bearer scheme."
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// --- CORS ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp", policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "https://localhost:4200",
                "http://localhost:4206",
                "https://localhost:4206")
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });

    options.AddPolicy("AllowMcpClients", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// --- gRPC ---
builder.Services.AddGrpc();

// P2.5 (#15) — MCP lifecycle tools read the caller subject id from the
// HttpContext claims (same source as REST, gRPC, CLI). Without this
// registration `IHttpContextAccessor.HttpContext` is always null and the
// tools can't authenticate the actor.
builder.Services.AddHttpContextAccessor();

// --- MCP Server ---
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// --- Middleware ---
// Swagger document + UI are exposed in Development and the integration-test
// "Testing" environment. The OpenAPI document drives the committed
// `docs/openapi/andy-policies-v1.yaml` and the CI drift check (P1.9, #79).
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowAngularApp");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// --- gRPC endpoint ---
app.MapGrpcService<Andy.Policies.Api.GrpcServices.ItemsGrpcService>();
app.MapGrpcService<Andy.Policies.Api.GrpcServices.PolicyGrpcService>();
app.MapGrpcService<Andy.Policies.Api.GrpcServices.LifecycleGrpcService>();
app.MapGrpcService<Andy.Policies.Api.GrpcServices.BindingsGrpcService>();
app.MapGrpcService<Andy.Policies.Api.GrpcServices.ScopesGrpcService>();
app.MapGrpcService<Andy.Policies.Api.GrpcServices.OverridesGrpcService>();

// --- MCP endpoint ---
app.MapMcp("/mcp")
    .RequireCors("AllowMcpClients")
    .RequireAuthorization();

// --- Health check ---
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .AllowAnonymous();

app.MapFallbackToFile("index.html");

// --- Auto-migrate in development ---
// Postgres migrations are operator-driven outside Development; SQLite EnsureCreated
// is also allowed in the integration-test "Testing" environment (and in turn the
// OpenAPI export pipeline that boots Program.cs in Testing) so the boot-time
// stock-policy seeder below has a schema to land into.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connectionString))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsNpgsql() && app.Environment.IsDevelopment())
    {
        await db.Database.MigrateAsync();
    }
    else if (db.Database.IsSqlite()
             && (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
    {
        await db.Database.EnsureCreatedAsync();
    }
}

// --- Seed stock policies (P1.3, #73) ---
// Idempotent. Runs in every environment after migrations have applied; if the
// schema is missing the underlying AnyAsync probe throws and boot fails loudly.
await app.Services.EnsureSeedDataAsync();

app.Run();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { }
