using BrainService;
using BrainService.Domain.Guild;
using BrainService.Hubs;
using BrainService.Services;
using BrainService.Services.Asr;
using BrainService.Services.Audio;
using BrainService.Services.Audio.Graph;
using BrainService.Services.Audio.Transport;
using BrainService.Services.Audio.Vad;
using BrainService.Services.Configuration;
using BrainService.Services.Llm;
using BrainService.Services.Guild;
using BrainService.Services.Llm.Tools;
using BrainService.Services.Memory;
using BrainService.Services.Session;
using BrainService.Services.Tts;
using Microsoft.Extensions.Logging.Console;
using MongoDB.Driver;
using MudBlazor.Services;
using RedLockNet;
using RedLockNet.SERedis;
using StackExchange.Redis;

ThreadPool.SetMinThreads(250, 250);

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();
builder.Services.Configure<ConsoleLoggerOptions>(o =>
    o.QueueFullMode = ConsoleLoggerQueueFullMode.DropWrite);

// Valkey
var valkeyUrl = builder.Configuration.GetValue<string>("Valkey:Url") ?? "localhost:6379";
var redisConn = ConnectionMultiplexer.Connect(valkeyUrl);
builder.Services.AddSingleton<IConnectionMultiplexer>(redisConn);
builder.Services.AddSingleton<IDistributedLockFactory>(sp => RedLockFactory.Create([redisConn]));

// Mongo
var mongoUrl = builder.Configuration.GetValue<string>("Mongo:Url") ?? "mongodb://localhost:27017";
var mongoDbName = builder.Configuration.GetValue<string>("Mongo:Database") ?? "narek";
var mongoClient = new MongoClient(mongoUrl);
builder.Services.AddSingleton<IMongoClient>(mongoClient);
builder.Services.AddSingleton<IMongoDatabase>(_ => mongoClient.GetDatabase(mongoDbName));

// ASR
var asrUrl = builder.Configuration.GetValue<string>("Asr:Url") ?? "localhost:6060";
builder.Services.AddSingleton<AsrGrpcClient>(sp =>
    new AsrGrpcClient(asrUrl, sp.GetRequiredService<ILogger<AsrGrpcClient>>()));

// Comms / API
builder.Services.AddOpenApi();
builder.Services.AddGrpc();
builder.Services.AddHttpClient();

// Audio
builder.Services.AddSingleton<SileroVadModelService>();
builder.Services.AddSingleton<UdpAudioServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpAudioServer>());
builder.Services.AddSingleton<AudioGraphFactory>();
builder.Services.AddSingleton<SoundboardService>();

// Session / state
builder.Services.AddSingleton<BrainConfigService>();
builder.Services.AddSingleton<BotConfigService>();
builder.Services.AddSingleton<BrainGrpcService>();
builder.Services.AddSingleton<NodeRegistryService>();
builder.Services.AddSingleton<CommandPublisher>();
builder.Services.AddSingleton<VoiceSessionHistoryService>();
builder.Services.AddSingleton<VoiceSessionService>();
builder.Services.AddHostedService<StaleConnectionClearer>();

// LLM
builder.Services.AddSingleton<GuildSettingsService>();

var ollamaUrl = builder.Configuration.GetValue<string>("Guild:Defaults:ProviderUrl") ?? "http://llm:11434";
builder.Services.AddSingleton(sp => new OllamaModelService(
    ollamaUrl,
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<OllamaModelService>>()));

builder.Services.AddSingleton<LlmProviderFactory>();
builder.Services.AddSingleton<ITokenCounter, FallbackTokenCounter>();

builder.Services.AddSingleton<GuildMemoryService>();
builder.Services.AddSingleton<ToolContextAccessor>();
builder.Services.AddSingleton<IToolExecutor, RememberTool>();
builder.Services.AddSingleton<IToolExecutor, RecallTool>();
builder.Services.AddSingleton<IToolExecutor, PlaySoundboardTool>();
builder.Services.AddSingleton<IToolExecutor, EndSessionTool>();
builder.Services.AddSingleton<ToolRegistry>(sp =>
    new ToolRegistry(sp.GetServices<IToolExecutor>()));

builder.Services.AddSingleton<LlmContextBuilder>();
builder.Services.AddSingleton<LlmOrchestrator>();

// TTS
builder.Services.AddSingleton<TtsVoiceRegistry>();
builder.Services.AddSingleton<SapiTtsProvider>();
builder.Services.AddSingleton<TtsProviderFactory>();

// Register the real observer only when voices are configured;
// otherwise use the no-op so the orchestrator needs no null checks.
builder.Services.AddSingleton<TtsResponseObserver>();
builder.Services.AddSingleton<ILlmResponseObserver>(sp =>
{
   var registry = sp.GetRequiredService<TtsVoiceRegistry>();
   return registry.Voices.Count > 0
       ? sp.GetRequiredService<TtsResponseObserver>()
       : NullLlmResponseObserver.Instance;
});

// Dashboard
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

var signalR = builder.Services.AddSignalR();
signalR.AddStackExchangeRedis(valkeyUrl, options =>
{
    options.Configuration.ChannelPrefix = new RedisChannel("dashboard:updates", RedisChannel.PatternMode.Literal);
});

var app = builder.Build();

var vadModelService = app.Services.GetRequiredService<SileroVadModelService>();
await vadModelService.EnsureModelAsync();

{
    var orchestrator = app.Services.GetRequiredService<LlmOrchestrator>();
    var sessionSvc = app.Services.GetRequiredService<VoiceSessionService>();
    orchestrator.SetSessionService(sessionSvc);
    sessionSvc.SetOrchestrator(orchestrator);
}

{
    var soundboardSvc = app.Services.GetRequiredService<SoundboardService>();
    await soundboardSvc.LoadAllAsync();
}

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

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<AudioGraphFactory>().DisposeAsync().AsTask().GetAwaiter().GetResult());

app.Run();
