using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Internal;
using TUnit.Core;

namespace Ratatoskr.Tests.EfCore;

public class TypedOptionsRegistryTests
{
    [Test]
    public void Get_UnregisteredType_Throws()
    {
        var registry = new TypedOptionsRegistry<InboxOptions>("inbox options");

        var act = () => registry.Get(typeof(DbContext));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No inbox options registered*DbContext*");
    }

    [Test]
    public void Get_RegisteredType_ReturnsOptions()
    {
        var registry = new TypedOptionsRegistry<OutboxOptions>("outbox options");
        var options = new OutboxOptions();
        registry.Register(typeof(DbContext), options);

        var result = registry.Get(typeof(DbContext));

        result.Should().BeSameAs(options);
    }

    [Test]
    public void Contains_UnregisteredType_ReturnsFalse()
    {
        var registry = new TypedOptionsRegistry<InboxOptions>("inbox options");

        registry.Contains(typeof(DbContext)).Should().BeFalse();
    }

    [Test]
    public void Contains_RegisteredType_ReturnsTrue()
    {
        var registry = new TypedOptionsRegistry<InboxOptions>("inbox options");
        registry.Register(typeof(DbContext), new InboxOptions());

        registry.Contains(typeof(DbContext)).Should().BeTrue();
    }
}
