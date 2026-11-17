using Relay.Application;
using Relay.Infrastructure;
using Relay.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Register application and infrastructure layers for future outbox dispatcher
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
