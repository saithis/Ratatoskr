namespace Ratatoskr.RabbitMq.WolverineMigration;

/// <summary>
/// Configuration options for Wolverine to Ratatoskr migration.
/// </summary>
public class WolverineMigrationOptions
{
    /// <summary>
    /// Enable migration mode (creates .v2 topology alongside existing Wolverine topology).
    /// Default: true
    /// </summary>
    public bool EnableMigration { get; set; } = true;
    
    /// <summary>
    /// Queue suffix for new Ratatoskr topology.
    /// Default: ".v2"
    /// </summary>
    /// <remarks>
    /// The suffix is appended to all queue and exchange names to avoid conflicts with existing Wolverine topology.
    /// Example: "apikey.subscriptions" becomes "apikey.subscriptions.v2"
    /// </remarks>
    public string QueueSuffix { get; set; } = ".v2";
    
    /// <summary>
    /// Original Wolverine service name (used for validating Wolverine DLQ topology).
    /// Example: "apikey" for service with DLQ named "apikey.wolverine-dead-letter-queue"
    /// </summary>
    public string WolverineServiceName { get; set; } = "";
    
    /// <summary>
    /// Whether to automatically create duplicate bindings from external exchanges to new .v2 queues.
    /// Set to true during initial migration, false after external bindings are updated manually.
    /// Default: true
    /// </summary>
    /// <remarks>
    /// When true, the migration manager will discover existing bindings on old Wolverine queues
    /// and create duplicate bindings to new Ratatoskr queues. This ensures both topologies
    /// receive messages during the canary deployment phase.
    /// </remarks>
    public bool CreateDuplicateBindings { get; set; } = true;
    
    /// <summary>
    /// Whether to validate that the old Wolverine topology still exists.
    /// Default: true
    /// </summary>
    /// <remarks>
    /// When true, the migration manager will perform passive declares on old Wolverine queues
    /// to ensure they still exist. This prevents accidental deletion of the old topology
    /// before migration is complete.
    /// </remarks>
    public bool ValidateWolverineTopologyExists { get; set; } = true;
    
    /// <summary>
    /// Mapping of old Wolverine queue names to new Ratatoskr queue base names.
    /// Key: Wolverine queue name (e.g., "apikey.subscriptions")
    /// Value: Ratatoskr queue name base (e.g., "apikey.subscriptions")
    /// </summary>
    /// <remarks>
    /// The value will have the QueueSuffix appended to it.
    /// This mapping is used for:
    /// - Discovering bindings on old queues to replicate
    /// - Validating old topology during migration
    /// - Health checks comparing old vs new queue states
    /// </remarks>
    public Dictionary<string, string> QueueMappings { get; set; } = new();

    /// <summary>
    /// Helper method to add a queue mapping in fluent style.
    /// </summary>
    public WolverineMigrationOptions AddQueueMapping(string wolverineQueueName, string ratatoskrQueueBaseName)
    {
        QueueMappings[wolverineQueueName] = ratatoskrQueueBaseName;
        return this;
    }
}
