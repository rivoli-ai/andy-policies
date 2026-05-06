// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Manifest;
using Andy.Policies.Infrastructure.Manifest;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Manifest;

/// <summary>
/// P10.3 (#38): orchestration semantics for the hosted service.
///
/// Asserts:
///   1. AutoRegister=false (or unset) → no client is invoked.
///   2. AutoRegister=true happy path → all three clients invoked in order.
///   3. Auth failure stops dispatch before rbac+settings.
///   4. Rbac failure stops dispatch before settings.
/// </summary>
public class ManifestRegistrationHostedServiceTests
{
    [Fact]
    public async Task AutoRegister_Unset_SkipsAllDispatch()
    {
        var auth = new RecordingAuthClient();
        var rbac = new RecordingRbacClient();
        var settings = new RecordingSettingsClient();
        var loader = new FailingLoader(); // would throw if invoked
        var sut = MakeService(autoRegister: null, loader, auth, rbac, settings);

        await sut.StartAsync(CancellationToken.None);

        auth.Calls.Should().Be(0);
        rbac.Calls.Should().Be(0);
        settings.Calls.Should().Be(0);
        loader.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task AutoRegister_False_SkipsAllDispatch()
    {
        var auth = new RecordingAuthClient();
        var rbac = new RecordingRbacClient();
        var settings = new RecordingSettingsClient();
        var loader = new FailingLoader();
        var sut = MakeService(autoRegister: "false", loader, auth, rbac, settings);

        await sut.StartAsync(CancellationToken.None);

        auth.Calls.Should().Be(0);
        rbac.Calls.Should().Be(0);
        settings.Calls.Should().Be(0);
        loader.Invoked.Should().BeFalse();
    }

    [Fact]
    public async Task AutoRegister_True_DispatchesAuthThenRbacThenSettings()
    {
        var order = new List<string>();
        var auth = new RecordingAuthClient(_ => order.Add("auth"));
        var rbac = new RecordingRbacClient(_ => order.Add("rbac"));
        var settings = new RecordingSettingsClient(_ => order.Add("settings"));
        var sut = MakeService(autoRegister: "true",
            new StaticLoader(StubManifest()), auth, rbac, settings);

        await sut.StartAsync(CancellationToken.None);

        order.Should().Equal("auth", "rbac", "settings");
        auth.Calls.Should().Be(1);
        rbac.Calls.Should().Be(1);
        settings.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AuthFailure_StopsBeforeRbacAndSettings()
    {
        var auth = new RecordingAuthClient(_ =>
            throw new ManifestRegistrationException("auth", "boom"));
        var rbac = new RecordingRbacClient();
        var settings = new RecordingSettingsClient();
        var sut = MakeService(autoRegister: "true",
            new StaticLoader(StubManifest()), auth, rbac, settings);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ManifestRegistrationException>()
            .Where(e => e.Block == "auth");
        rbac.Calls.Should().Be(0);
        settings.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RbacFailure_StopsBeforeSettings()
    {
        var auth = new RecordingAuthClient();
        var rbac = new RecordingRbacClient(_ =>
            throw new ManifestRegistrationException("rbac", "boom"));
        var settings = new RecordingSettingsClient();
        var sut = MakeService(autoRegister: "true",
            new StaticLoader(StubManifest()), auth, rbac, settings);

        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ManifestRegistrationException>()
            .Where(e => e.Block == "rbac");
        auth.Calls.Should().Be(1);
        settings.Calls.Should().Be(0);
    }

    private static ManifestRegistrationHostedService MakeService(
        string? autoRegister,
        IManifestLoader loader,
        IAuthManifestClient auth,
        IRbacManifestClient rbac,
        ISettingsManifestClient settings)
    {
        var dict = new Dictionary<string, string?>();
        if (autoRegister is not null) dict["Registration:AutoRegister"] = autoRegister;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new ManifestRegistrationHostedService(
            config, loader, auth, rbac, settings,
            NullLogger<ManifestRegistrationHostedService>.Instance);
    }

    private static ManifestDocument StubManifest() => new(
        new ManifestService("andy-policies", "Andy Policies", "test"),
        new ManifestAuth("urn:test",
            new ManifestApiClient("andy-policies-api", "confidential", "X", "API", "API",
                Array.Empty<string>(), Array.Empty<string>()),
            new ManifestWebClient("andy-policies-web", "public", "Web", "Web",
                Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>())),
        new ManifestRbac("andy-policies", "Andy Policies", "test",
            Array.Empty<ManifestResourceType>(),
            Array.Empty<ManifestPermission>(),
            Array.Empty<ManifestRole>(),
            null),
        new ManifestSettings(Array.Empty<ManifestSettingDefinition>()));

    private sealed class StaticLoader : IManifestLoader
    {
        private readonly ManifestDocument _doc;
        public StaticLoader(ManifestDocument doc) => _doc = doc;
        public Task<ManifestDocument> LoadAsync(CancellationToken ct) => Task.FromResult(_doc);
    }

    private sealed class FailingLoader : IManifestLoader
    {
        public bool Invoked { get; private set; }
        public Task<ManifestDocument> LoadAsync(CancellationToken ct)
        {
            Invoked = true;
            throw new InvalidOperationException("loader must not be invoked");
        }
    }

    private sealed class RecordingAuthClient : IAuthManifestClient
    {
        private readonly Action<ManifestAuth>? _onCall;
        public RecordingAuthClient(Action<ManifestAuth>? onCall = null) => _onCall = onCall;
        public int Calls { get; private set; }
        public Task RegisterAsync(ManifestAuth auth, CancellationToken ct)
        {
            Calls++;
            _onCall?.Invoke(auth);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRbacClient : IRbacManifestClient
    {
        private readonly Action<ManifestRbac>? _onCall;
        public RecordingRbacClient(Action<ManifestRbac>? onCall = null) => _onCall = onCall;
        public int Calls { get; private set; }
        public Task RegisterAsync(ManifestRbac rbac, CancellationToken ct)
        {
            Calls++;
            _onCall?.Invoke(rbac);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSettingsClient : ISettingsManifestClient
    {
        private readonly Action<ManifestSettings>? _onCall;
        public RecordingSettingsClient(Action<ManifestSettings>? onCall = null) => _onCall = onCall;
        public int Calls { get; private set; }
        public Task RegisterAsync(ManifestSettings settings, CancellationToken ct)
        {
            Calls++;
            _onCall?.Invoke(settings);
            return Task.CompletedTask;
        }
    }
}
