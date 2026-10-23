using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.EfCore.Management;
using Ratatoskr.Management;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.EfCore;

public class DurabilityRegistrationTests
{
    [Test]
    public void AddRatatoskrGeneric_KeyedConsumeChannelWithoutUseInbox_UsesDefaultDbContextInbox()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr<TestDbContext>(bus =>
            bus.AddEventConsumeChannel(
                "keyed",
                c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key"))
            )
        );

        // Assert
        var channel = GetChannelRegistry(services).GetConsumeChannel("keyed");
        channel!.GetExtension<ChannelInboxConfig>()!.DbContextType.Should().Be<TestDbContext>();
    }

    [Test]
    public void AddRatatoskrGeneric_ConsumeChannelWithoutInbox_GetsNoInboxConfig()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr<TestDbContext>(bus =>
            bus.AddEventConsumeChannel(
                "fire-and-forget",
                c => c.WithoutInbox().Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
            )
        );

        // Assert
        var channel = GetChannelRegistry(services).GetConsumeChannel("fire-and-forget");
        channel!.GetExtension<ChannelInboxConfig>().Should().BeNull();
    }

    [Test]
    public void AddEfCoreDurability_TwoDbContexts_KeyedChannelUsesFirstAsDefault()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr(bus =>
        {
            bus.AddEventConsumeChannel(
                "keyed",
                c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key"))
            );
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseInbox());
        });

        // Assert
        var channel = GetChannelRegistry(services).GetConsumeChannel("keyed");
        channel!.GetExtension<ChannelInboxConfig>()!.DbContextType.Should().Be<TestDbContext>();
    }

    [Test]
    public void AddEfCoreDurability_TwoDbContexts_RegistersSharedServicesOnceAndPerContextServicesTwice()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr(bus =>
        {
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().UseOutbox());
            bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseOutbox());
        });

        // Assert
        services
            .Count(d =>
                d.ServiceType == typeof(IMessageSender)
                && d.ImplementationType == typeof(EfCoreMessageSender)
            )
            .Should()
            .Be(1);
        services.Count(d => d.ServiceType == typeof(EfCoreMetricsState)).Should().Be(1);
        services
            .Count(d =>
                d.ServiceType == typeof(IRatatoskrEndpointConfigurator)
                && d.ImplementationType == typeof(EfCoreEndpointConfigurator)
            )
            .Should()
            .Be(1);
        services
            .Count(d => d.ServiceType == typeof(IEfCoreManagementDbContextDescriptor))
            .Should()
            .Be(2);
        services
            .Count(d => d.ImplementationType == typeof(EfCoreMetricsBackgroundService<TestDbContext>))
            .Should()
            .Be(1);
        services
            .Count(d =>
                d.ImplementationType == typeof(EfCoreMetricsBackgroundService<SecondTestDbContext>)
            )
            .Should()
            .Be(1);
    }

    [Test]
    public void AddEfCoreDurability_WithoutBackgroundProcessing_RegistersOnlyMetricsHostedService()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr(bus =>
            bus.AddEfCoreDurability<TestDbContext>(d =>
                d.UseInbox(i => i.WithRetention(TimeSpan.FromDays(1)).WithoutBackgroundProcessing())
                    .UseOutbox(o =>
                        o.WithRetention(TimeSpan.FromDays(1)).WithoutBackgroundProcessing()
                    )
            )
        );

        // Assert
        services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Should()
            .ContainSingle()
            .Which.ImplementationType.Should()
            .Be<EfCoreMetricsBackgroundService<TestDbContext>>();
        services.Should().Contain(d => d.ServiceType == typeof(InboxProcessor<TestDbContext>));
        services.Should().Contain(d => d.ServiceType == typeof(OutboxProcessor<TestDbContext>));
    }

    [Test]
    public void AddEfCoreDurability_CalledTwiceForSameDbContext_Throws()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () =>
            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("AddEfCoreDurability<TestDbContext>() was called more than once*");
    }

    [Test]
    public void AddRatatoskrGeneric_WithoutDurabilityConfig_RegistersInboxAndOutbox()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr<TestDbContext>();

        // Assert
        services.Should().Contain(d => d.ServiceType == typeof(InboxProcessor<TestDbContext>));
        services.Should().Contain(d => d.ServiceType == typeof(OutboxProcessor<TestDbContext>));
    }

    [Test]
    public void AddRatatoskrGeneric_WithCustomDurabilityForSameDbContext_UsesCustomRegistration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr<TestDbContext>(bus =>
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox(o => o.WithBatchSize(7)))
        );

        // Assert
        services
            .Count(d => d.ServiceType == typeof(DurabilityMarker<TestDbContext>))
            .Should()
            .Be(1);
        services.Should().NotContain(d => d.ServiceType == typeof(InboxProcessor<TestDbContext>));
        var outboxOptions = (OutboxOptionsHolder<TestDbContext>)
            services
                .Single(d => d.ServiceType == typeof(OutboxOptionsHolder<TestDbContext>))
                .ImplementationInstance!;
        outboxOptions.Options.BatchSize.Should().Be(7);
    }

    [Test]
    public void AddRatatoskrGeneric_OtherDbContextRegisteredInConfigure_KeepsGenericAsDefault()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr<TestDbContext>(bus =>
        {
            bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseInbox());
            bus.AddEventConsumeChannel(
                "keyed",
                c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key"))
            );
        });

        // Assert
        var channel = GetChannelRegistry(services).GetConsumeChannel("keyed");
        channel!.GetExtension<ChannelInboxConfig>()!.DbContextType.Should().Be<TestDbContext>();
    }

    [Test]
    public void AddEfCoreDurability_BuilderWithoutBackgroundProcessing_RegistersNoHostedServices()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr(bus =>
            bus.AddEfCoreDurability<TestDbContext>(d =>
                d.WithoutBackgroundProcessing()
                    .UseInbox(i => i.WithRetention(TimeSpan.FromDays(1)))
                    .UseOutbox(o => o.WithRetention(TimeSpan.FromDays(1)))
            )
        );

        // Assert
        services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
        services.Should().Contain(d => d.ServiceType == typeof(InboxProcessor<TestDbContext>));
        services.Should().Contain(d => d.ServiceType == typeof(OutboxProcessor<TestDbContext>));
    }

    [Test]
    public void AddEfCoreDurability_AsDefaultOnLaterDbContext_BecomesDefaultRegardlessOfOrder()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddRatatoskr(bus =>
        {
            bus.AddEventConsumeChannel(
                "keyed",
                c => c.Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("key"))
            );
            bus.AddEventConsumeChannel(
                "explicit-default-inbox",
                c =>
                    c.UseInbox()
                        .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>("other-key"))
            );
            bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox());
            bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseInbox().AsDefault());
        });

        // Assert
        var registry = GetChannelRegistry(services);
        registry
            .GetConsumeChannel("keyed")!
            .GetExtension<ChannelInboxConfig>()!
            .DbContextType.Should()
            .Be<SecondTestDbContext>();
        registry
            .GetConsumeChannel("explicit-default-inbox")!
            .GetExtension<ChannelInboxConfig>()!
            .DbContextType.Should()
            .Be<SecondTestDbContext>();
        services.Should().ContainSingle(d => d.ServiceType == typeof(IRatatoskrDbContext));
    }

    [Test]
    public void AddEfCoreDurability_TwoDbContextsMarkedAsDefault_Throws()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () =>
            services.AddRatatoskr(bus =>
            {
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseInbox().AsDefault());
                bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseInbox().AsDefault());
            });

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Cannot make SecondTestDbContext the default DbContext*");
    }

    [Test]
    public void AddRatatoskrGeneric_OtherDbContextMarkedAsDefault_Throws()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () =>
            services.AddRatatoskr<TestDbContext>(bus =>
                bus.AddEfCoreDurability<SecondTestDbContext>(d => d.UseInbox().AsDefault())
            );

        // Assert
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*TestDbContext is already the default*");
    }

    private static ChannelRegistry GetChannelRegistry(IServiceCollection services) =>
        (ChannelRegistry)
            services.Single(d => d.ServiceType == typeof(ChannelRegistry)).ImplementationInstance!;
}
