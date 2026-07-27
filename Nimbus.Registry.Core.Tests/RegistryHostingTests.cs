using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>Covers how AddNimbusRegistry wires the clock into the container.</summary>
public class RegistryHostingTests
{
    private static WebApplicationBuilder NewBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        return builder;
    }

    [Fact]
    public void AddNimbusRegistry_RegistersTheSystemClock_WhenTheHostHasNone()
    {
        var builder = NewBuilder();
        builder.AddNimbusRegistry(new RegistryConfig(), withMasterServer: false);

        using var app = builder.Build();

        Assert.Same(TimeProvider.System, app.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void AddNimbusRegistry_KeepsAClockTheHostRegisteredFirst()
    {
        // A host that embeds the registry may own the clock (tests, or a process that
        // wants one clock across several subsystems). TryAdd inside AddNimbusRegistry is
        // what preserves it; a plain Add would register last and win.
        var hostClock = new FakeClock();
        var builder = NewBuilder();
        builder.Services.AddSingleton<TimeProvider>(hostClock);
        builder.AddNimbusRegistry(new RegistryConfig(), withMasterServer: false);

        using var app = builder.Build();

        Assert.Same(hostClock, app.Services.GetRequiredService<TimeProvider>());
    }
}
