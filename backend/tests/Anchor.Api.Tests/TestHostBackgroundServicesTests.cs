using Anchor.Api.Events;
using Anchor.Api.Realtime;
using Anchor.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anchor.Api.Tests;

/// <summary>
/// The test hosts switch off Program.cs's background services (#353), so tests
/// drive <see cref="HeartbeatMonitor.ScanOnceAsync"/>,
/// <see cref="EventPruner.PruneOnceAsync"/> and
/// <see cref="SessionAutoEnder.EndForgottenSessionsAsync"/> themselves instead
/// of racing a loop on the real clock. Program.cs reads the Enable* flags
/// before <c>Build()</c>, which a flag supplied through
/// <c>ConfigureAppConfiguration</c> reaches too late: all three services ran in
/// every test host while the flags said otherwise.
/// </summary>
public sealed class TestHostBackgroundServicesTests
    : IClassFixture<AnchorApiFactory>, IClassFixture<DevImpersonationRestTests.RealAuthDevFactory>
{
    private readonly AnchorApiFactory _factory;
    private readonly DevImpersonationRestTests.RealAuthDevFactory _devFactory;

    public TestHostBackgroundServicesTests(
        AnchorApiFactory factory, DevImpersonationRestTests.RealAuthDevFactory devFactory)
    {
        _factory = factory;
        _devFactory = devFactory;
    }

    [Theory]
    [InlineData(typeof(HeartbeatMonitor))]
    [InlineData(typeof(EventPruner))]
    [InlineData(typeof(SessionAutoEnder))]
    public void AnchorApiFactory_does_not_run_the_background_service(Type service)
    {
        Assert.DoesNotContain(HostedServiceTypes(_factory), t => t == service);
    }

    [Theory]
    [InlineData(typeof(HeartbeatMonitor))]
    [InlineData(typeof(EventPruner))]
    [InlineData(typeof(SessionAutoEnder))]
    public void The_Development_test_host_does_not_run_the_background_service(Type service)
    {
        Assert.DoesNotContain(HostedServiceTypes(_devFactory), t => t == service);
    }

    /// <summary>
    /// The flags themselves decide, so a test that needs a background service
    /// opts back in with the same host setting — and the absence asserted
    /// above is the flag's doing, not a registration that went missing.
    /// </summary>
    [Theory]
    [InlineData("Heartbeat:EnableMonitor", typeof(HeartbeatMonitor))]
    [InlineData("EventRetention:EnablePruner", typeof(EventPruner))]
    [InlineData("SessionAutoEnd:EnableAutoEnder", typeof(SessionAutoEnder))]
    public async Task A_test_can_opt_back_in_to_a_background_service(string flag, Type service)
    {
        await using var optedIn = _factory.WithWebHostBuilder(b => b.UseSetting(flag, "true"));

        var running = HostedServiceTypes(optedIn);

        Assert.Contains(service, running);
        Assert.Single(running, t => t == typeof(HeartbeatMonitor) || t == typeof(EventPruner) || t == typeof(SessionAutoEnder));
    }

    private static List<Type> HostedServiceTypes(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();
}
