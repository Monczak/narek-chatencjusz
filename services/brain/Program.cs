using BrainService;
using BrainService.Hubs;
using BrainService.Services;
using BrainService.Services.Audio;
using BrainService.Services.Configuration;
using BrainService.Services.Session;
using MudBlazor.Services;
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

builder.Services.AddHttpClient();

builder.Services.AddSingleton<SileroVadModelService>();
builder.Services.AddSingleton<AudioGraphFactory>();

builder.Services.AddSingleton<BrainConfigService>();
builder.Services.AddSingleton<BrainGrpcService>();
builder.Services.AddSingleton<NodeRegistryService>();
builder.Services.AddSingleton<CommandPublisher>();
builder.Services.AddSingleton<VoiceSessionService>();

builder.Services.AddHostedService<StaleConnectionClearer>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var signalR = builder.Services.AddSignalR();
signalR.AddStackExchangeRedis(valkeyUrl, options =>
{
    options.Configuration.ChannelPrefix = new RedisChannel("dashboard:updates", RedisChannel.PatternMode.Literal);
});

var app = builder.Build();

var vadModelService = app.Services.GetRequiredService<SileroVadModelService>();
await vadModelService.EnsureModelAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error?code=500");
}

app.UseStatusCodePagesWithReExecute("/error", "?code={0}");
app.UseRouting();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<DashboardHub>("/hub/dashboard");

app.UseHttpsRedirection();

app.MapGrpcService<BrainGrpcService>();

app.Run();
