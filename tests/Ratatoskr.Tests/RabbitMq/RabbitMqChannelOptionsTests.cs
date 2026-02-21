using AwesomeAssertions;
using Ratatoskr.RabbitMq.Config;
using TUnit.Core;

namespace Ratatoskr.Tests.RabbitMq;

public class RabbitMqChannelOptionsTests
{
    [Test]
    public void WithExchangeDurable_SetsValue()
    {
        var options = new RabbitMqChannelOptions();

        var result = options.WithExchangeDurable(false);

        result.ExchangeDurable.Should().BeFalse();
        result.Should().BeSameAs(options);
    }

    [Test]
    public void WithExchangeDurable_DefaultParameter_SetsTrue()
    {
        var options = new RabbitMqChannelOptions().WithExchangeDurable(false);

        options.WithExchangeDurable();

        options.ExchangeDurable.Should().BeTrue();
    }

    [Test]
    public void WithExchangeAutoDelete_SetsValue()
    {
        var options = new RabbitMqChannelOptions();

        var result = options.WithExchangeAutoDelete(true);

        result.ExchangeAutoDelete.Should().BeTrue();
        result.Should().BeSameAs(options);
    }

    [Test]
    public void WithExchangeAutoDelete_DefaultParameter_SetsTrue()
    {
        var options = new RabbitMqChannelOptions();

        options.WithExchangeAutoDelete();

        options.ExchangeAutoDelete.Should().BeTrue();
    }

    [Test]
    public void FluentChaining_WorksAcrossAllExchangeMethods()
    {
        var options = new RabbitMqChannelOptions()
            .WithDirectExchange()
            .WithExchangeDurable(false)
            .WithExchangeAutoDelete();

        options.ExchangeType.Should().Be(RabbitMqExchangeType.Direct);
        options.ExchangeDurable.Should().BeFalse();
        options.ExchangeAutoDelete.Should().BeTrue();
    }
}
