using BrainService.Services;
using BrainService.Services.Session;
using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var valkeyUrl = builder.Configuration.GetValue<string>("Valkey:Url") ?? "localhost:6379";
var redisConn = ConnectionMultiplexer.Connect(valkeyUrl);
builder.Services.AddSingleton<IConnectionMultiplexer>(redisConn);
builder.Services.AddSingleton<IDistributedLockFactory>(sp => RedLockFactory.Create([redisConn]));

builder.Services.AddOpenApi();
builder.Services.AddGrpc();

builder.Services.AddSingleton<BrainGrpcService>();
builder.Services.AddSingleton<NodeRegistryService>();
builder.Services.AddSingleton<CommandPublisher>();
builder.Services.AddSingleton<VoiceSessionService>();

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
