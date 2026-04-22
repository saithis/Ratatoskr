using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;

namespace Ratatoskr.RabbitMq.WolverineMigration;

/// <summary>
/// Extension methods for configuring Wolverine to Ratatoskr migration.
/// </summary>
public static class WolverineMigrationExtensions
{
    /// <summary>
    /// Enables Wolverine to Ratatoskr migration mode with the specified configuration.
    /// This replaces the standard RabbitMQ topology manager and consumer with migration-aware versions
    /// that create .v2 suffixed queues alongside existing Wolverine topology.
    /// </summary>
    /// <param name="builder">The Ratatoskr builder</param>
    /// <param name="configure">Configuration action for migration options</param>
    /// <returns>The Ratatoskr builder for chaining</returns>
    /// <remarks>
    /// Usage:
    /// <code>
    /// services.AddRatatoskr(builder => 
    /// {
    ///     builder.WithServiceName("MyService");
    ///     
    ///     builder.UseRabbitMq(mq => 
    ///     {
    ///         mq.ConnectionString("amqp://localhost");
    ///     });
    ///     
    ///     builder.UseWolverineMigration(migration =>
    ///     {
    ///         migration.EnableMigration = true;
    ///         migration.QueueSuffix = ".v2";
    ///         migration.WolverineServiceName = "myservice";
    ///         migration.AddQueueMapping("myservice.subscriptions", "myservice.subscriptions");
    ///     });
    ///     
    ///     // Configure channels as normal
    ///     builder.AddEventConsumeChannel("external.events")
    ///            .WithRabbitMq(cfg => cfg.QueueName("myservice.subscriptions"))
    ///            .Consumes&lt;MyEvent&gt;();
    /// });
    /// </code>
    /// </remarks>
    public static RatatoskrBuilder UseWolverineMigration(
        this RatatoskrBuilder builder, 
        Action<WolverineMigrationOptions> configure)
    {
        var migrationOptions = new WolverineMigrationOptions();
        configure(migrationOptions);
        
        if (!migrationOptions.EnableMigration)
        {
            throw new InvalidOperationException(
                "Migration is disabled in WolverineMigrationOptions. " +
                "Either set EnableMigration=true or remove UseWolverineMigration() call.");
        }
        
        // Register migration options as singleton
        builder.Services.AddSingleton(migrationOptions);
        
        // Replace RabbitMqTopologyManager with WolverineMigrationTopologyManager
        builder.Services.RemoveAll<RabbitMqTopologyManager>();
        builder.Services.AddSingleton<WolverineMigrationTopologyManager>();
        
        // Replace RabbitMqConsumer with WolverineMigrationConsumer
        // Remove the standard consumer first
        var consumerDescriptor = builder.Services.FirstOrDefault(d => 
            d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && 
            d.ImplementationType == typeof(RabbitMqConsumer));
        
        if (consumerDescriptor != null)
        {
            builder.Services.Remove(consumerDescriptor);
        }
        
        // Add migration consumer
        builder.Services.AddSingleton<WolverineMigrationConsumer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WolverineMigrationConsumer>());
        
        // Register migration health check
        builder.Services.AddSingleton<WolverineMigrationHealthCheck>();
        builder.Services.AddHealthChecks()
            .AddCheck<WolverineMigrationHealthCheck>("wolverine_migration");
        
        return builder;
    }
}
