using BrainService.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var valkeyUrl = builder.Configuration.GetValue<string>("Valkey:Url") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(valkeyUrl));

builder.Services.AddOpenApi();
builder.Services.AddGrpc();

builder.Services.AddSingleton<BrainGrpcService>();
builder.Services.AddSingleton<NodeRegistryService>();

builder.Services.AddHostedService<StaleConnectionClearer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGrpcService<BrainGrpcService>();

app.Run();
