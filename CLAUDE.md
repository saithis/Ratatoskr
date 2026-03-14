- For every code change, make sure it is covered by tests
- Prefer integration tests over unit tests
- Use AwesomeAssertions of assertions
- Always make sure the documentation in docs/ is in sync
- Always use TimeProvider to create instances of DateTime/DateTimeOffset

## Running tests

The test project uses TUnit (not xUnit/NUnit). Run tests with `dotnet run`, not `dotnet test`.

```bash
# Run all tests (use --maximum-parallel-tests 10 to avoid inotify limit issues)
dotnet run --project tests/Ratatoskr.Tests -- --maximum-parallel-tests 10

# Run a single test by name (treenode-filter format: /*/*/ClassName/TestName)
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/InboxTests/Inbox_MyTestName"

# Run all tests in a class
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/OutboxBasicTests/*" --maximum-parallel-tests 10

# List all tests
dotnet run --project tests/Ratatoskr.Tests -- --list-tests
```

### TUnit filter tips

- The `--treenode-filter` uses a path format: `/*/*/ClassName/TestName` (assembly/namespace/class/test).
- Use `*` wildcards for segments you don't want to match exactly.
- Wildcard patterns like `*OutboxTests*` do NOT work — use the full path format `/*/*/OutboxBasicTests/*`.
- To find the exact class name for a test, use `--list-tests` and look at the grouping.
- Do NOT use `--filter` (MSTest/xUnit style) — TUnit only supports `--treenode-filter`.

