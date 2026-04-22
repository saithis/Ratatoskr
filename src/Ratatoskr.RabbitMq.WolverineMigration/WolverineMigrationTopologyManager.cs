using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.RabbitMq;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;

namespace Ratatoskr.RabbitMq.WolverineMigration;

/// <summary>
/// Topology manager that orchestrates migration from Wolverine to Ratatoskr topology.
/// Creates new .v2 topology alongside existing Wolverine topology for zero-downtime migration.
/// </summary>
public class WolverineMigrationTopologyManager(
    ChannelRegistry registry,
    RabbitMqConnectionManager connectionManager,
    WolverineMigrationOptions migrationOptions,
    ILogger<WolverineMigrationTopologyManager> logger)
{
    private readonly TaskCompletionSource _provisioningTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Waits for the migration topology provisioning to complete.
    /// </summary>
    public Task WaitForProvisioningAsync(CancellationToken cancellationToken = default)
    {
        return _provisioningTcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Provisions the Ratatoskr topology with migration suffix alongside existing Wolverine topology.
    /// </summary>
    public async Task ProvisionTopologyAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!migrationOptions.EnableMigration)
            {
                logger.LogWarning("Migration mode is disabled. WolverineMigrationTopologyManager should not be used. Use standard RabbitMqTopologyManager instead.");
                _provisioningTcs.TrySetResult();
                return;
            }

            logger.LogInformation("Starting Wolverine to Ratatoskr topology migration (suffix: {Suffix})", migrationOptions.QueueSuffix);

            await using var channel = await connectionManager.CreateChannelAsync(true, cancellationToken);

            // Step 1: Validate Wolverine topology still exists (if enabled)
            if (migrationOptions.ValidateWolverineTopologyExists)
            {
                await ValidateWolverineTopologyAsync(channel, cancellationToken);
            }

            // Step 2: Provision Ratatoskr topology with queue suffix
            // Publish channels first, to make sure they are declared if consume channels want to bind to them
            foreach (var reg in registry.GetPublishChannels())
            {
                await ProvisionChannelAsync(channel, reg, cancellationToken);
            }
            
            foreach (var reg in registry.GetConsumeChannels())
            {
                await ProvisionChannelAsync(channel, reg, cancellationToken);
            }

            // Step 3: Create duplicate bindings from external exchanges (if enabled)
            if (migrationOptions.CreateDuplicateBindings)
            {
                await CreateDuplicateBindingsAsync(channel, cancellationToken);
            }

            logger.LogInformation("Wolverine to Ratatoskr topology migration provisioning completed successfully");
            _provisioningTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision migration topology");
            _provisioningTcs.TrySetException(ex);
            throw;
        }
    }

    private async Task ValidateWolverineTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        logger.LogInformation("Validating that Wolverine topology still exists...");

        foreach (var mapping in migrationOptions.QueueMappings)
        {
            var wolverineQueueName = mapping.Key;

            try
            {
                // Passive declare to check if queue exists
                await channel.QueueDeclarePassiveAsync(wolverineQueueName, cancellationToken);
                logger.LogInformation("Validated: Wolverine queue '{Queue}' exists", wolverineQueueName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wolverine queue '{Queue}' does not exist or is not accessible. " +
                    "This may indicate the old topology was deleted prematurely.", wolverineQueueName);
                throw new InvalidOperationException(
                    $"Wolverine topology validation failed: Queue '{wolverineQueueName}' does not exist. " +
                    "Ensure the old Wolverine topology is still intact before migrating.", ex);
            }
        }

        // Also validate Wolverine service DLQ if service name is provided
        if (!string.IsNullOrEmpty(migrationOptions.WolverineServiceName))
        {
            var wolverineDlqName = $"{migrationOptions.WolverineServiceName}.wolverine-dead-letter-queue";
            try
            {
                await channel.QueueDeclarePassiveAsync(wolverineDlqName, cancellationToken);
                logger.LogInformation("Validated: Wolverine service DLQ '{Queue}' exists", wolverineDlqName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Wolverine service DLQ '{Queue}' does not exist. This may be expected if not using Wolverine's global DLQ.", wolverineDlqName);
            }
        }
    }

    private async Task ProvisionChannelAsync(IChannel channel, ChannelRegistration reg, CancellationToken token)
    {
        var channelOpts = reg.GetRabbitMqChannelOptions() ?? new RabbitMqChannelOptions();

        // 1. Exchange Logic (same as standard topology manager)
        if (reg.Intent == ChannelType.EventPublish || reg.Intent == ChannelType.CommandConsume)
        {
            // We OWN the exchange -> Declare it
            logger.LogInformation("[Migration] Declaring exchange '{Exchange}' Type: {Type}", reg.ChannelName, channelOpts.ExchangeType);
            await channel.ExchangeDeclareAsync(
                exchange: reg.ChannelName, 
                type: channelOpts.ExchangeType, 
                durable: channelOpts.Durable, 
                autoDelete: channelOpts.AutoDelete, 
                arguments: null, 
                cancellationToken: token);
        }
        else
        {
            // We EXPECT the exchange -> Validate it (Passive Declare)
            logger.LogInformation("[Migration] Validating exchange '{Exchange}' exists", reg.ChannelName);
            try 
            {
                await channel.ExchangeDeclarePassiveAsync(reg.ChannelName, token);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "[Migration] Exchange '{Exchange}' validation failed. It must exist for intent {Intent}.", reg.ChannelName, reg.Intent);
                throw; 
            }
        }

        // 2. Queue Logic (with migration suffix applied)
        if (reg.Intent == ChannelType.CommandConsume || reg.Intent == ChannelType.EventConsume)
        {
            var consumerOpts = reg.GetRabbitMqConsumerOptions();
            
            string baseQueueName = consumerOpts?.QueueName ?? throw new InvalidOperationException($"Queue name must be specified for consumer channel '{reg.ChannelName}'");
            
            // Apply migration suffix to queue name
            string queueName = $"{baseQueueName}{migrationOptions.QueueSuffix}";
            
            logger.LogInformation("[Migration] Using queue name '{QueueName}' (base: '{BaseQueueName}', suffix: '{Suffix}')", 
                queueName, baseQueueName, migrationOptions.QueueSuffix);

            IDictionary<string, object?> queueArgs = new Dictionary<string, object?>(consumerOpts.QueueArguments);
            if (consumerOpts.QueueType == QueueType.Quorum)
            {
                queueArgs["x-queue-type"] = "quorum";
            }

            if (consumerOpts.UseManagedRetryTopology)
            {
                var dlqName = $"{queueName}{consumerOpts.DeadLetterQueueSuffix}";
                var retryQueueName = $"{queueName}{consumerOpts.RetryQueueSuffix}";

                logger.LogInformation("[Migration] Provisioning retry topology for queue '{Queue}' (DLQ: {Dlq}, Retry: {Retry})", queueName, dlqName, retryQueueName);

                // 1. Declare DLQ Exchange (Fanout)
                await channel.ExchangeDeclareAsync(
                    exchange: dlqName, 
                    type: ExchangeType.Fanout, 
                    durable: true, 
                    autoDelete: false, 
                    arguments: null, 
                    cancellationToken: token);

                // 2. Declare DLQ Queue
                var dlqArgs = new Dictionary<string, object?>(queueArgs);
                dlqArgs.Remove("x-dead-letter-exchange");
                dlqArgs.Remove("x-dead-letter-routing-key");
                await channel.QueueDeclareAsync(
                    queue: dlqName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: dlqArgs,
                    cancellationToken: token);
                
                // 3. Bind DLQ Queue to DLQ Exchange
                await channel.QueueBindAsync(
                    queue: dlqName,
                    exchange: dlqName,
                    routingKey: "",
                    cancellationToken: token);

                // 4. Declare Retry Queue (TTL -> Main Queue)
                var retryArgs = new Dictionary<string, object?>(queueArgs)
                {
                    ["x-dead-letter-exchange"] = "",
                    ["x-dead-letter-routing-key"] = queueName,
                    ["x-message-ttl"] = (long)consumerOpts.RetryDelay.TotalMilliseconds
                };
                
                await channel.QueueDeclareAsync(
                    queue: retryQueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: retryArgs,
                    cancellationToken: token);

                // 5. Configure Main Queue to Dead-Letter to Retry Queue
                queueArgs["x-dead-letter-exchange"] = "";
                queueArgs["x-dead-letter-routing-key"] = retryQueueName;
            }

            logger.LogInformation("[Migration] Declaring queue '{Queue}' for channel '{Channel}'", queueName, reg.ChannelName);
            
            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: consumerOpts.Durable,
                exclusive: consumerOpts.Exclusive,
                autoDelete: consumerOpts.AutoDelete,
                arguments: queueArgs,
                cancellationToken: token);

            // 3. Bindings
            foreach (var msg in reg.Messages)
            {
                var msgOpts = msg.GetRabbitMqOptions();
                
                string routingKey = msgOpts?.RoutingKey ?? msg.MessageTypeName;
                
                logger.LogInformation("[Migration] Binding queue '{Queue}' to exchange '{Exchange}' with key '{Key}'", queueName, reg.ChannelName, routingKey);
                
                await channel.QueueBindAsync(
                    queue: queueName, 
                    exchange: reg.ChannelName, 
                    routingKey: routingKey, 
                    arguments: null, 
                    cancellationToken: token);
            }
        }
    }

    private async Task CreateDuplicateBindingsAsync(IChannel channel, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Migration] Creating duplicate bindings from external exchanges to new .v2 queues...");

        foreach (var mapping in migrationOptions.QueueMappings)
        {
            var oldQueueName = mapping.Key;
            var newQueueBaseName = mapping.Value;
            var newQueueName = $"{newQueueBaseName}{migrationOptions.QueueSuffix}";

            logger.LogInformation("[Migration] Discovering bindings for old queue '{OldQueue}' to replicate to new queue '{NewQueue}'", 
                oldQueueName, newQueueName);

            try
            {
                // Get bindings for the old queue using RabbitMQ Management API model
                // Note: RabbitMQ.Client doesn't provide a direct API to query bindings,
                // so we need to use the HTTP Management API or assume bindings from configuration
                
                // For now, we'll rely on the configured bindings in ChannelRegistry
                // and log a warning that manual binding verification may be needed
                
                var consumeChannels = registry.GetConsumeChannels();
                
                foreach (var reg in consumeChannels)
                {
                    var consumerOpts = reg.GetRabbitMqConsumerOptions();
                    if (consumerOpts?.QueueName == newQueueBaseName)
                    {
                        // This is a channel we're migrating
                        // The bindings are already created in ProvisionChannelAsync
                        logger.LogInformation("[Migration] Bindings for '{Queue}' to exchange '{Exchange}' were created during provisioning", 
                            newQueueName, reg.ChannelName);
                    }
                }

                logger.LogWarning("[Migration] Automatic binding discovery is limited. " +
                    "If your old queue '{OldQueue}' has bindings not defined in Ratatoskr configuration, " +
                    "you may need to manually create duplicate bindings to '{NewQueue}' via RabbitMQ Management UI or CLI.",
                    oldQueueName, newQueueName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Migration] Failed to create duplicate bindings for queue '{OldQueue}' -> '{NewQueue}'", 
                    oldQueueName, newQueueName);
                throw;
            }
        }

        logger.LogInformation("[Migration] Duplicate binding creation completed. " +
            "Verify bindings manually using RabbitMQ Management UI if needed.");
    }

    /// <summary>
    /// Gets the migrated queue name for a given base queue name.
    /// </summary>
    public string GetMigratedQueueName(string baseQueueName)
    {
        return $"{baseQueueName}{migrationOptions.QueueSuffix}";
    }
}
