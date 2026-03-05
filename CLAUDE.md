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

# Run a single test by name (treenode-filter format: /Assembly/Namespace/Class/TestName)
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/InboxTests/Inbox_MyTestName"

# Run all tests in a class
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/OutboxTests/*" --maximum-parallel-tests 10

# List all tests
dotnet run --project tests/Ratatoskr.Tests -- --list-tests