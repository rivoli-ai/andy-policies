// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Andy.Policies.Api.GrpcServices.Authorization;
using Andy.Policies.Api.Protos;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Integration.Authorization;

/// <summary>
/// P7.6 (#64) — reflection-driven coverage gate for the gRPC permission
/// map. The interceptor fail-closes on an unmapped RPC by throwing
/// <c>RpcException(Internal)</c>; that is the runtime safety net, but
/// production should never see it. This test catches the gap at CI time
/// by walking the proto-generated <c>*ServiceBase</c> classes and
/// asserting every RPC has a permission code in <see cref="GrpcMethodPermissionMap"/>.
/// Adding a new rpc without a map entry breaks this test.
/// </summary>
public class GrpcPermissionMapCoverageTests
{
    /// <summary>The proto-generated bases for every enforced gRPC service.</summary>
    public static IEnumerable<object[]> EnforcedServiceBases() => new[]
    {
        new object[] { typeof(PolicyService.PolicyServiceBase) },
        new object[] { typeof(LifecycleService.LifecycleServiceBase) },
        new object[] { typeof(BindingService.BindingServiceBase) },
        new object[] { typeof(ScopesService.ScopesServiceBase) },
        new object[] { typeof(OverridesService.OverridesServiceBase) },
        new object[] { typeof(AuditService.AuditServiceBase) },
    };

    [Theory]
    [MemberData(nameof(EnforcedServiceBases))]
    public void EveryRpcOnEnforcedServiceHasAPermissionCode(Type serviceBase)
    {
        var map = new GrpcMethodPermissionMap();
        var serviceName = serviceBase.DeclaringType!.Name; // e.g. "PolicyService"

        var rpcMethods = ProtoRpcMethodsOf(serviceBase);
        rpcMethods.Should().NotBeEmpty(
            $"{serviceBase.FullName} must expose at least one rpc — empty service is suspicious.");

        var missing = new List<string>();
        foreach (var m in rpcMethods)
        {
            var fqn = $"/andy_policies.{serviceName}/{m.Name}";
            if (!map.TryGetPermission(fqn, out _))
            {
                missing.Add(fqn);
            }
        }

        missing.Should().BeEmpty(
            "every enforced gRPC RPC must have a permission code in GrpcMethodPermissionMap; " +
            "the interceptor fail-closes on unmapped methods, so this is a security gap.");
    }

    [Fact]
    public void MapDoesNotContainStaleEntriesForRetiredRpcs()
    {
        var map = new GrpcMethodPermissionMap();
        var liveRpcs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in EnforcedServiceBases())
        {
            var serviceBase = (Type)row[0];
            var serviceName = serviceBase.DeclaringType!.Name;
            foreach (var m in ProtoRpcMethodsOf(serviceBase))
            {
                liveRpcs.Add($"/andy_policies.{serviceName}/{m.Name}");
            }
        }

        var stale = map.Entries.Keys.Where(k => !liveRpcs.Contains(k)).ToList();
        stale.Should().BeEmpty(
            "a permission entry that does not match any live rpc is dead code — " +
            "it can mask a rename or deletion silently.");
    }

    [Fact]
    public void ItemsServiceIsBypassedNotMapped()
    {
        var map = new GrpcMethodPermissionMap();
        // ItemsService ships as template scaffolding; it is intentionally
        // not enforced. Defend the bypass: no map entries, IsEnforcedService
        // returns false.
        map.Entries.Keys.Should().NotContain(k => k.StartsWith("/andy_policies.ItemsService/"));
        GrpcMethodPermissionMap.IsEnforcedService("/andy_policies.ItemsService/CreateItem").Should().BeFalse();
    }

    /// <summary>
    /// Returns the rpc-shaped virtual methods declared on the proto-generated
    /// <c>*ServiceBase</c>. Filters out <c>object</c> overrides and any
    /// non-virtual surface that the generator emits.
    /// </summary>
    private static MethodInfo[] ProtoRpcMethodsOf(Type serviceBase)
        => serviceBase
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsVirtual && !m.IsFinal)
            .ToArray();
}
