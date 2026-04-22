# Ratatoskr.RabbitMq.WolverineMigration

⚠️ **TEMPORARY MIGRATION PACKAGE** - This package is intended for one-time migration from Wolverine to Ratatoskr and will be archived after Q2 2026.

A temporary addon library that enables safe, zero-downtime migration from Wolverine RabbitMQ topology to Ratatoskr RabbitMQ topology with full support for canary deployments and multiple replicas.

## Table of Contents

- [Overview](#overview)
- [Topology Differences](#topology-differences)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Migration Process](#migration-process)
- [Configuration](#configuration)
- [Monitoring](#monitoring)
- [Cleanup](#cleanup)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)

## Overview

This migration addon creates a parallel topology alongside your existing Wolverine queues, allowing you to:

- ✅ **Zero downtime** - Both old and new replicas can run simultaneously
- ✅ **Canary deployment** - Gradually roll out new topology with feature flags
- ✅ **No message loss** - Messages are duplicated to both topologies during migration
- ✅ **Safe rollback** - Revert to Wolverine at any point during migration
- ✅ **No impact to other services** - External bindings are duplicated, not replaced

## Topology Differences

### Wolverine Topology

```
External Exchange → Main Queue (quorum)
                    ↓ DLX
                    DLQ Exchange (topic)
                    ↓
                    DLQ Queue (classic)
                    ↓ DLX
                    Service-wide DLQ Exchange (fanout)
                    ↓
                    Service-wide DLQ Queue (classic)
```

**Example:**
- `apikey.subscriptions` (quorum queue)
- `apikey.subscriptions.dlq` (topic exchange + classic queue)
- `apikey.wolverine-dead-letter-queue` (fanout exchange + classic queue)

### Ratatoskr Topology

```
External Exchange → Main Queue (quorum)
                    ↓ DLX on nack
                    Retry Queue (quorum) ⟲ TTL back to Main Queue
                    
                    Main Queue ─ publish on error → DLQ Exchange (fanout)
                                                    ↓
                                                    DLQ Queue (quorum)
```

**Example:**
- `apikey.subscriptions.v2` (quorum queue)
- `apikey.subscriptions.v2.retry` (quorum queue with TTL)
- `apikey.subscriptions.v2.dlq` (fanout exchange + quorum queue)

### Key Differences

| Aspect | Wolverine | Ratatoskr |
|--------|-----------|-----------|
| **Retry Strategy** | Multi-level DLX chain | TTL-based retry queue loop |
| **Queue Type** | Mixed (quorum + classic) | All quorum |
| **DLQ Approach** | Service-wide final DLQ | Per-queue DLQ |
| **Retry Control** | Automatic via DLX | Application-controlled with explicit publish |

## Installation

Add the package to your service:

```bash
dotnet add package Ratatoskr.RabbitMq.WolverineMigration
```

## Quick Start

### 1. Configure Migration in Your Service

```csharp
using Ratatoskr.RabbitMq.WolverineMigration;

var useMigration = builder.Configuration.GetValue<bool>("USE_RATATOSKR_TOPOLOGY", false);

if (useMigration)
{
    services.AddRatatoskr(ratatoskr => 
    {
        ratatoskr.WithServiceName("ApiKeyService");
        
        ratatoskr.UseRabbitMq(mq => 
        {
            mq.ConnectionString = "amqp://localhost:5672";
        });
        
        // Enable migration addon
        ratatoskr.UseWolverineMigration(migration =>
        {
            migration.EnableMigration = true;
            migration.QueueSuffix = ".v2";
            migration.WolverineServiceName = "apikey";
            migration.CreateDuplicateBindings = true;
            migration.ValidateWolverineTopologyExists = true;
            
            // Map old Wolverine queue names to new Ratatoskr queue names
            migration.AddQueueMapping(
                wolverineQueueName: "apikey.subscriptions",
                ratatoskrQueueBaseName: "apikey.subscriptions"
            );
        });

        // Configure channels as normal (queue names will be suffixed automatically)
        ratatoskr.AddEventConsumeChannel("sso.authz.events")
                 .WithRabbitMq(cfg => cfg
                     .QueueName("apikey.subscriptions") // Will become apikey.subscriptions.v2
                     .ExchangeType(ExchangeType.Topic))
                 .Consumes<UserRolesChanged>(cfg => cfg
                     .WithRoutingKey("user-roles-changed"));
    });
}
else
{
    // Keep existing Wolverine configuration
    services.AddWolverine(/* existing configuration */);
}
```

### 2. Set Environment Variable

For new replicas using Ratatoskr:
```bash
USE_RATATOSKR_TOPOLOGY=true
```

For old replicas still using Wolverine:
```bash
USE_RATATOSKR_TOPOLOGY=false
```

### 3. Deploy First Canary

Deploy **one replica** with `USE_RATATOSKR_TOPOLOGY=true`. This will:
- Create new `.v2` queues
- Validate old Wolverine topology exists
- Create duplicate bindings (messages flow to both topologies)

## Migration Process

### Phase 1: Initial Setup (Day 1)

1. **Add package** to your service
2. **Configure migration** as shown above
3. **Deploy one canary replica** with `USE_RATATOSKR_TOPOLOGY=true`
4. **Verify** new topology via RabbitMQ Management UI

**Expected State:**
```
Old Topology:                      New Topology:
apikey.subscriptions               apikey.subscriptions.v2
apikey.subscriptions.dlq          apikey.subscriptions.v2.retry
apikey.wolverine-dead-letter-queue apikey.subscriptions.v2.dlq

External Exchange (sso.authz.events)
  ├─→ apikey.subscriptions (old)
  └─→ apikey.subscriptions.v2 (new)
```

### Phase 2: Gradual Rollout (Days 2-7)

Gradually increase the number of replicas with `USE_RATATOSKR_TOPOLOGY=true`:

```bash
# Day 2: 25% of replicas
# Deploy 1 out of 4 replicas with USE_RATATOSKR_TOPOLOGY=true

# Day 3-4: 50% of replicas
# Deploy 2 out of 4 replicas with USE_RATATOSKR_TOPOLOGY=true

# Day 5-7: 75% of replicas
# Deploy 3 out of 4 replicas with USE_RATATOSKR_TOPOLOGY=true
```

**Monitoring During Rollout:**
- Message processing rates (compare old vs new queues)
- Error rates and retry counts
- Dead letter queue depths
- Consumer lag metrics
- Application logs for migration warnings

### Phase 3: Full Migration (Day 8-14)

1. Deploy **all replicas** with `USE_RATATOSKR_TOPOLOGY=true`
2. Monitor old Wolverine queues - they should drain naturally
3. Wait 2+ weeks for confidence before cleanup

### Phase 4: Cleanup (After 2+ weeks)

⚠️ **CRITICAL**: Only proceed after confirming **no messages** in old queues for several days.

#### Step 1: Remove Duplicate Bindings

Using `rabbitmqadmin`:
```bash
# List bindings to verify
rabbitmqadmin list bindings source destination

# Remove binding from old queue
rabbitmqadmin delete binding \
  source=sso.authz.events \
  destination=apikey.subscriptions \
  destination_type=queue \
  properties_key=user-roles-changed
```

Or via RabbitMQ Management UI:
1. Navigate to **Exchanges** → `sso.authz.events`
2. Scroll to **Bindings** section
3. Find binding to old queue `apikey.subscriptions`
4. Click **Unbind**

#### Step 2: Verify Old Queues Are Empty

```bash
# Check queue message counts
rabbitmqadmin list queues name messages

# Should show 0 messages for:
# - apikey.subscriptions
# - apikey.subscriptions.dlq
# - apikey.wolverine-dead-letter-queue
```

#### Step 3: Delete Old Wolverine Topology

```bash
# Delete queues
rabbitmqadmin delete queue name=apikey.subscriptions
rabbitmqadmin delete queue name=apikey.subscriptions.dlq
rabbitmqadmin delete queue name=apikey.wolverine-dead-letter-queue

# Delete exchanges
rabbitmqadmin delete exchange name=apikey.subscriptions.dlq
rabbitmqadmin delete exchange name=apikey.wolverine-dead-letter-queue
```

Or via RabbitMQ Management UI:
1. Navigate to **Queues** tab
2. Select old queue → **Delete / Purge** → **Delete**
3. Navigate to **Exchanges** tab
4. Select old exchange → **Delete**

#### Step 4: Remove Migration Code

1. Remove `UseWolverineMigration()` configuration
2. Remove `Ratatoskr.RabbitMq.WolverineMigration` package reference
3. Remove `USE_RATATOSKR_TOPOLOGY` environment variable checks
4. Deploy updated code (standard Ratatoskr configuration continues working with `.v2` queues)

**Optional**: If you want to remove `.v2` suffix, this requires a second migration (or downtime). **Recommendation**: Keep `.v2` suffix permanently.

## Configuration

### WolverineMigrationOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableMigration` | `bool` | `true` | Enable migration mode (must be true when using addon) |
| `QueueSuffix` | `string` | `".v2"` | Suffix appended to all queue/exchange names |
| `WolverineServiceName` | `string` | `""` | Original Wolverine service name (for DLQ validation) |
| `CreateDuplicateBindings` | `bool` | `true` | Automatically create duplicate bindings to external exchanges |
| `ValidateWolverineTopologyExists` | `bool` | `true` | Validate old topology exists during startup |
| `QueueMappings` | `Dictionary` | `{}` | Map old queue names to new queue base names |

### Example Configuration

```csharp
migration =>
{
    migration.EnableMigration = true;
    migration.QueueSuffix = ".v2";
    migration.WolverineServiceName = "myservice";
    
    // Map multiple queues
    migration.AddQueueMapping("myservice.events", "myservice.events");
    migration.AddQueueMapping("myservice.commands", "myservice.commands");
    
    // Or using dictionary syntax
    migration.QueueMappings["myservice.notifications"] = "myservice.notifications";
}
```

## Monitoring

### Health Checks

The migration addon registers a health check endpoint:

```bash
curl http://localhost:5000/health/wolverine_migration
```

Response:
```json
{
  "status": "Healthy",
  "results": {
    "wolverine_migration": {
      "status": "Healthy",
      "description": "Wolverine migration is healthy. Both old and new topologies are operational.",
      "data": {
        "migration_enabled": true,
        "queue_suffix": ".v2",
        "queue_mappings": 1,
        "consumer_healthy": true
      }
    }
  }
}
```

### Metrics

Monitor these metrics during migration:

1. **Queue Depths** (via RabbitMQ Management API)
   - Old queue: `apikey.subscriptions`
   - New queue: `apikey.subscriptions.v2`
   - Should be similar during parallel operation

2. **Message Rates**
   - Incoming rate (external exchange)
   - Processing rate (both old and new consumers)
   - Dead letter rates

3. **Consumer Lag**
   - Ratatoskr exposes OpenTelemetry metrics for lag

4. **Application Logs**
   - Search for `[Migration]` prefix in logs
   - Monitor for warnings about missing bindings

### RabbitMQ CLI Commands

```bash
# List all queues with message counts
rabbitmqadmin list queues name messages consumers

# Monitor queue in real-time
watch -n 1 'rabbitmqadmin list queues name messages'

# Check bindings for an exchange
rabbitmqadmin list bindings source=sso.authz.events

# View queue details
rabbitmqadmin show queue name=apikey.subscriptions.v2
```

## Cleanup

### Manual Cleanup Checklist

Before deleting old topology, verify:

- [ ] All replicas are running `USE_RATATOSKR_TOPOLOGY=true`
- [ ] Old queues have been empty for 2+ weeks
- [ ] No errors in application logs related to message processing
- [ ] Monitoring shows consistent behavior between old and new queues
- [ ] Stakeholders are informed of upcoming cleanup

### Cleanup Commands

See [Phase 4: Cleanup](#phase-4-cleanup-after-2-weeks) above for detailed commands.

### Rollback After Cleanup

⚠️ If you need to rollback **after** deleting old topology:

1. You **cannot** use Wolverine anymore (topology is deleted)
2. Keep using Ratatoskr with `.v2` queues
3. Alternative: Restore old topology from backups (if available)

**This is why we recommend waiting 2+ weeks before cleanup!**

## Troubleshooting

### Issue: "Wolverine queue does not exist" error on startup

**Cause**: Old Wolverine topology was deleted before migration completed.

**Solution**:
1. If you have backups, restore old topology
2. Otherwise, disable validation: `migration.ValidateWolverineTopologyExists = false;`
3. Remove queue mappings that reference deleted queues

### Issue: Duplicate messages being processed

**Cause**: Both old and new replicas are consuming from external exchange.

**Expected**: This is normal during canary deployment. Your application should handle idempotency.

**Solution**: Ensure message handlers are idempotent (use database constraints, deduplication, etc.)

### Issue: Messages only going to old queue, not new queue

**Cause**: Duplicate bindings were not created.

**Solution**:
1. Verify `CreateDuplicateBindings = true` in configuration
2. Check RabbitMQ Management UI for bindings: `Exchanges` → `sso.authz.events` → `Bindings`
3. Manually create binding if needed:
   ```bash
   rabbitmqadmin declare binding \
     source=sso.authz.events \
     destination=apikey.subscriptions.v2 \
     destination_type=queue \
     routing_key=user-roles-changed
   ```

### Issue: Consumer health check fails

**Cause**: RabbitMQ connection issues or queue doesn't exist.

**Check**:
1. RabbitMQ is accessible: `rabbitmqadmin list connections`
2. Queue exists: `rabbitmqadmin list queues | grep .v2`
3. Application logs for detailed error messages

### Issue: Old queue not draining after full migration

**Cause**: Some replicas may still be using old topology, or bindings weren't removed.

**Solution**:
1. Verify all replicas have `USE_RATATOSKR_TOPOLOGY=true`: `kubectl get pods -o yaml | grep USE_RATATOSKR_TOPOLOGY`
2. Check bindings: `rabbitmqadmin list bindings destination=apikey.subscriptions`
3. If binding still exists to old queue, remove it (see cleanup steps)

### Issue: Cannot find `rabbitmqadmin` command

**Solution**:
```bash
# Download rabbitmqadmin from your RabbitMQ server
wget http://localhost:15672/cli/rabbitmqadmin
chmod +x rabbitmqadmin
sudo mv rabbitmqadmin /usr/local/bin/

# Or use Docker
docker exec -it <rabbitmq-container> rabbitmqadmin list queues
```

## FAQ

### Q: Can I change the queue suffix after initial deployment?

**A**: No, changing the suffix would require re-provisioning topology. Stick with `.v2` once chosen.

### Q: What happens if a replica crashes during message processing?

**A**: RabbitMQ will redeliver the message (standard behavior). No data loss occurs.

### Q: Can I skip the `.v2` suffix and use original queue names?

**A**: Not during migration. The suffix is required to avoid conflicts. After cleanup, you can optionally do a second migration to remove the suffix (requires downtime) or keep it permanently.

### Q: Do I need to update external services that publish to our exchanges?

**A**: No! Your exchanges are owned by you. External services continue publishing to the same exchanges. The migration only changes your internal queues and bindings.

### Q: How long does the migration take?

**A**: Technical implementation: 1-2 days. Canary rollout: 1-2 weeks. Stabilization before cleanup: 2+ weeks. Total: 4-6 weeks for complete migration with confidence.

### Q: Can I run this migration in production?

**A**: Yes! This migration strategy is specifically designed for zero-downtime production migrations. However, always test in staging first.

### Q: What if I want to rollback after full migration?

**A**: Before cleanup, rollback is trivial: set `USE_RATATOSKR_TOPOLOGY=false` on all replicas. After cleanup (old topology deleted), you must continue with Ratatoskr.

### Q: Does this work with RabbitMQ clusters?

**A**: Yes! Quorum queues are designed for clusters. Both Wolverine and Ratatoskr topologies work in clustered environments.

### Q: Can I migrate multiple services simultaneously?

**A**: Yes, but we recommend migrating one service at a time to minimize risk and simplify troubleshooting.

### Q: What about messages in-flight during deployment?

**A**: Messages are safe! RabbitMQ holds unacknowledged messages until acknowledged. If a replica goes down, messages are redelivered to remaining replicas.

## Support

This is a temporary migration package. For issues:

1. Check this README thoroughly
2. Review application logs (search for `[Migration]` prefix)
3. Consult RabbitMQ Management UI
4. Open an issue in the Ratatoskr repository

## License

MIT License - Same as Ratatoskr core library

---

**Remember**: This package is temporary. Plan to remove it after successful migration! 🎉
