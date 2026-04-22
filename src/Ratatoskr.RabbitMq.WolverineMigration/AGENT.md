# AI Agent Instructions - Wolverine Migration Addon

## Purpose

This is a **TEMPORARY** migration addon package. It should be removed after successful migration.

## Key Points for AI Agents

1. **Temporary Nature**: This package is designed for one-time use and should be archived after Q2 2026
2. **Do Not Extend**: Do not add features beyond migration support
3. **Do Not Refactor**: Keep code simple and focused on migration
4. **Documentation First**: Any changes must update README.md
5. **Testing Required**: All changes must include integration tests

## Architecture

- **WolverineMigrationOptions**: Configuration for migration (queue suffix, mappings, etc.)
- **WolverineMigrationTopologyManager**: Creates .v2 topology alongside Wolverine topology
- **WolverineMigrationConsumer**: Consumes from .v2 queues instead of original queues
- **WolverineMigrationExtensions**: DI registration that replaces standard RabbitMq components
- **WolverineMigrationHealthCheck**: Verifies both topologies are healthy

## Migration Strategy

1. **Phase 1**: Deploy with `USE_RATATOSKR_TOPOLOGY=true` on one replica
2. **Phase 2**: Gradually increase replicas with new topology
3. **Phase 3**: Full migration (all replicas on new topology)
4. **Phase 4**: Manual cleanup (remove old topology, remove this package)

## Testing

All tests are in `tests/Ratatoskr.Tests/Migration/WolverineMigrationTests.cs`

- Test dual topology creation
- Test consumer uses .v2 queues
- Test retry topology works
- Test DLQ topology works
- Test health checks
- Test validation

## Common Issues

### "Wolverine queue does not exist"
- Set `ValidateWolverineTopologyExists = false` if old topology is already deleted
- Or restore old topology from backups

### Duplicate messages
- Expected during migration
- Applications must handle idempotency

### Messages not duplicated to new queue
- Verify `CreateDuplicateBindings = true`
- Check RabbitMQ Management UI for bindings
- May need manual binding creation

## Do Not

- ❌ Add features beyond migration support
- ❌ Refactor for "cleanliness" (keep it simple)
- ❌ Remove deprecation warnings
- ❌ Make this package permanent
- ❌ Add dependencies beyond what's already there

## Do

- ✅ Keep documentation up-to-date
- ✅ Add integration tests for any changes
- ✅ Preserve backward compatibility during migration period
- ✅ Log extensively (use `[Migration]` prefix)
- ✅ Validate user configuration

## Package Lifecycle

**Expected Lifecycle**: 
1. Publish: Q1 2026
2. Active Use: Q1-Q2 2026 (3-6 months)
3. Deprecation Notice: Q2 2026
4. Archive: Q3 2026 (after all users migrated)

This package will be **ARCHIVED** after Q2 2026. Do not plan long-term maintenance.
