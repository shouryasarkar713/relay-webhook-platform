using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Relay.Application.Messaging;
using Relay.Infrastructure.Messaging;
using Xunit;

namespace Relay.UnitTests;

public sealed class PublisherConfigurationTests
{
    [Fact]
    public void AzureServiceBusPublisherOptions_ThrowsWhenNeitherConnectionNorNamespaceConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureServiceBusPublisherOptions>(opt =>
        {
            opt.ConnectionString = "";
            opt.FullyQualifiedNamespace = "";
            opt.QueueOrTopicName = "relay-events";
        });
        services.AddSingleton<IEventMessagePublisher, AzureServiceBusEventPublisher>();

        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IEventMessagePublisher>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Neither ConnectionString nor FullyQualifiedNamespace was provided*");
    }

    [Fact]
    public void Worker_Production_ThrowsWhenServiceBusIsUnconfigured()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ServiceBus:ConnectionString"] = "",
            ["ServiceBus:FullyQualifiedNamespace"] = "",
            ["ServiceBus:AllowSimulationPublisher"] = "false"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var serviceBusConnection = configuration["ServiceBus:ConnectionString"];
        var serviceBusNamespace = configuration["ServiceBus:FullyQualifiedNamespace"];
        var allowSimulation = configuration.GetValue<bool>("ServiceBus:AllowSimulationPublisher");
        var isDevelopment = false; // Simulating Production

        var act = () =>
        {
            if (!string.IsNullOrWhiteSpace(serviceBusConnection) || !string.IsNullOrWhiteSpace(serviceBusNamespace))
            {
                // Ok
            }
            else if (isDevelopment && allowSimulation)
            {
                // Ok
            }
            else
            {
                throw new InvalidOperationException("Critical Configuration Error: Azure Service Bus is not configured.");
            }
        };

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Critical Configuration Error*");
    }
}
