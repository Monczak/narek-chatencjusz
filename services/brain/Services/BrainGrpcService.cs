using BrainService.Domain.Llm;
using BrainService.Proto.Brain;
using BrainService.Services.Audio.Graph;
using BrainService.Services.Llm;
using BrainService.Services.Session;
using Grpc.Core;

namespace BrainService.Services;

public class BrainGrpcService(
    ILogger<BrainGrpcService> logger,
    NodeRegistryService nodeRegistry,
    CommandPublisher publisher,
    VoiceSessionService voiceSessionService,
    AudioGraphFactory audioGraphFactory,
    GuildSettingsService guildSettingsService,
    OllamaModelService ollamaModelService,
    IHostApplicationLifetime applicationLifetime
) : Brain.BrainBase
{
    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
        => Task.FromResult(new PingResponse { Message = $"Pong: {request.Message}" });

    public override async Task<JoinChannelResponse> JoinChannel(JoinChannelRequest request, ServerCallContext context)
    {
        logger.LogInformation("Node {NodeId} requesting join Guild {GuildId} Channel {ChannelId}",
            request.NodeId, request.Guild.Id, request.Channel.Id);

        var currentOwner     = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var currentChannelId = await nodeRegistry.GetChannelForGuildAsync(request.Guild.Id);
        var currentSessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);

        var requestedSession = !string.IsNullOrEmpty(request.SessionId)
            ? request.SessionId
            : await nodeRegistry.GetLatestSessionForGuildAsync(request.Guild.Id);

        var hasOwner    = !string.IsNullOrEmpty(currentOwner);
        var sameGuild   = currentOwner == request.NodeId;
        var sameChannel = currentChannelId == request.Channel.Id;

        switch (hasOwner, sameGuild, sameChannel)
        {
            case (true, true, true):
                return new JoinChannelResponse
                {
                    Success   = false,
                    Message   = "Already connected",
                    SessionId = currentSessionId ?? ""
                };

            case (true, false, _):
                return new JoinChannelResponse
                {
                    Success = false,
                    Message = $"Guild handled by node {currentOwner}"
                };

            default:
                var sessionId = currentSessionId ?? requestedSession ?? Guid.NewGuid().ToString();
                await publisher.PublishCommandAsync(request.NodeId, new BrainCommand
                {
                    Connect = new ConnectVoice
                    {
                        Guild          = request.Guild,
                        Channel        = request.Channel,
                        SessionId      = sessionId,
                        CorrelationId  = request.CorrelationId
                    }
                });
                return new JoinChannelResponse
                {
                    Success   = true,
                    Message   = "Connection approved",
                    SessionId = sessionId
                };
        }
    }

    public override async Task<LeaveChannelResponse> LeaveChannel(LeaveChannelRequest request, ServerCallContext context)
    {
        var currentOwner     = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
        var currentSessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);

        if (currentOwner == request.NodeId || !string.IsNullOrEmpty(request.CorrelationId))
        {
            await publisher.PublishCommandAsync(request.NodeId, new BrainCommand
            {
                Disconnect = new DisconnectVoice
                {
                    Guild          = request.Guild,
                    SessionId      = currentSessionId ?? "",
                    CorrelationId  = request.CorrelationId,
                }
            });
        }
        return new LeaveChannelResponse { Success = true };
    }

    public override async Task<VoiceStateAck> NotifyVoiceState(VoiceStateNotification request, ServerCallContext context)
    {
        var isConnected = request.Reason is
            VoiceStateReason.Connect or VoiceStateReason.ManualMove or
            VoiceStateReason.ReconcileMissing or VoiceStateReason.ReconcileDrift;

        var isDisconnected = request.Reason is
            VoiceStateReason.Disconnect or VoiceStateReason.ManualDisconnect;

        if (isConnected && request.Channel != null)
        {
            var sessionId = request.SessionId;
            if (string.IsNullOrEmpty(sessionId))
                sessionId = await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id)
                            ?? Guid.NewGuid().ToString();

            await nodeRegistry.RegisterGuildConnectionAsync(
                request.Guild, request.NodeId, request.Channel, sessionId);
            await voiceSessionService.UpdateSessionChannelAsync(
                request.Guild, request.Channel, sessionId);
            
            audioGraphFactory.StartSession(sessionId, request.Guild.Id);

            logger.LogInformation("Graph started for session {SessionId} guild {GuildId}",
                sessionId, request.Guild.Id);
        }
        else if (isDisconnected)
        {
            var currentOwner = await nodeRegistry.GetNodeForGuildAsync(request.Guild.Id);
            if (currentOwner == request.NodeId)
            {
                var sessionId = request.SessionId
                                ?? await nodeRegistry.GetSessionForGuildAsync(request.Guild.Id);
                if (!string.IsNullOrEmpty(sessionId))
                    await audioGraphFactory.StopSessionAsync(sessionId);

                await nodeRegistry.UnregisterGuildConnectionAsync(request.Guild);
            }
        }

        return new VoiceStateAck { Success = true };
    }

    public override async Task<VoiceSessionEventAck> StreamVoiceSessionEvents(
        IAsyncStreamReader<VoiceSessionEvent> requestStream, ServerCallContext context)
    {
        var nodeId       = context.RequestHeaders.GetValue("node_id");
        var activeGuilds = new HashSet<GuildContext>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, applicationLifetime.ApplicationStopping);

        try
        {
            await foreach (var evt in requestStream.ReadAllAsync(cts.Token))
            {
                activeGuilds.Add(evt.Guild);
                if (string.IsNullOrEmpty(nodeId)) nodeId = evt.NodeId;
                await voiceSessionService.HandleEventAsync(evt);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogWarning("Event stream for Node {NodeId} disconnected", nodeId);

            if (!string.IsNullOrEmpty(nodeId))
            {
                foreach (var guild in activeGuilds)
                {
                    await voiceSessionService.HandleNodeDisconnectAsync(guild);
                    
                    var sessionId = await nodeRegistry.GetSessionForGuildAsync(guild.Id);
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        logger.LogInformation(
                            "Stopping audio graph for session {SessionId} after node {NodeId} disconnect",
                            sessionId, nodeId);
                        await audioGraphFactory.StopSessionAsync(sessionId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading event stream");
        }

        return new VoiceSessionEventAck { Success = true };
    }
    
    public override async Task<GetGuildSettingsResponse> GetGuildSettings(
        GetGuildSettingsRequest request,
        ServerCallContext context)
    {
        var guildId = request.GuildId;

        var raw = await guildSettingsService.GetRawSettingsAsync(guildId);
        var resolved = await guildSettingsService.GetSettingsAsync(guildId);

        return new GetGuildSettingsResponse
        {
            Resolved = ToProto(resolved),
            Overrides = raw is null ? new GuildLlmConfig() : ToRawProto(raw),
        };
    }

    public override async Task<UpdateGuildSettingsResponse> UpdateGuildSettings(
        UpdateGuildSettingsRequest request,
        ServerCallContext context)
    {
        var guildId = request.GuildId;

        // Load existing raw overrides (or start from scratch)
        var raw = await guildSettingsService.GetRawSettingsAsync(guildId)
                  ?? new GuildLlmSettings { GuildId = guildId };

        // Apply the patch - only write fields that are present in the proto message
        var p = request.Patch;
        if (p.HasSystemPrompt)       raw.SystemPrompt       = p.SystemPrompt;
        if (p.HasCustomInstructions) raw.CustomInstructions = p.CustomInstructions;
        if (p.HasBotName)            raw.BotName            = p.BotName;
        if (p.HasTemperature)        raw.Temperature        = p.Temperature;
        if (p.HasMaxTokens)          raw.MaxTokens          = p.MaxTokens;
        if (p.HasSilenceThresholdMs) raw.SilenceThresholdMs = p.SilenceThresholdMs;
        if (p.HasRambleModeEnabled)  raw.RambleModeEnabled  = p.RambleModeEnabled;
        if (p.HasRambleThresholdMs)  raw.RambleThresholdMs  = p.RambleThresholdMs;
        if (p.HasRambleSystemHint)   raw.RambleSystemHint   = p.RambleSystemHint;
        if (p.HasTimeZone)           raw.TimeZone           = p.TimeZone;

        // Honor explicit clears - set fields back to null so defaults take over
        foreach (var field in request.ClearFields)
        {
            switch (field)
            {
                case "system_prompt":        raw.SystemPrompt       = null; break;
                case "custom_instructions":  raw.CustomInstructions = null; break;
                case "bot_name":             raw.BotName            = null; break;
                case "temperature":          raw.Temperature        = null; break;
                case "max_tokens":           raw.MaxTokens          = null; break;
                case "silence_threshold_ms": raw.SilenceThresholdMs = null; break;
                case "ramble_mode_enabled":  raw.RambleModeEnabled  = null; break;
                case "ramble_threshold_ms":  raw.RambleThresholdMs  = null; break;
                case "ramble_system_hint":   raw.RambleSystemHint   = null; break;
                case "time_zone":            raw.TimeZone           = null; break;
                default:
                    logger.LogWarning("UpdateGuildSettings: unknown clear_field name '{Field}'", field);
                    break;
            }
        }

        await guildSettingsService.SaveSettingsAsync(raw);
        logger.LogInformation("UpdateGuildSettings applied for guild {GuildId}", guildId);

        return new UpdateGuildSettingsResponse { Success = true, Message = "Settings updated." };
    }
    
    public override async Task<GetAvailableModelsResponse> GetAvailableModels(
        GetAvailableModelsRequest request, ServerCallContext context)
    {
        var models = await ollamaModelService.GetAvailableModelsAsync(context.CancellationToken);
        var response = new GetAvailableModelsResponse();
        response.ModelNames.AddRange(models);
        return response;
    }
    
    private static GuildLlmConfig ToProto(ResolvedLlmSettings s) => new()
    {
        SystemPrompt       = s.SystemPrompt,
        BotName            = s.BotName,
        Temperature        = s.Temperature,
        MaxTokens          = s.MaxTokens,
        SilenceThresholdMs = s.SilenceThresholdMs,
        RambleModeEnabled  = s.RambleModeEnabled,
        RambleThresholdMs  = s.RambleThresholdMs,
        TimeZone           = s.TimeZone,
        CustomInstructions = s.CustomInstructions ?? "",
        RambleSystemHint   = s.RambleSystemHint,
        ModelName          = s.ModelName ?? "",
    };
    
    private static GuildLlmConfig ToRawProto(GuildLlmSettings s)
    {
        var cfg = new GuildLlmConfig();
        if (s.SystemPrompt       != null) cfg.SystemPrompt       = s.SystemPrompt;
        if (s.CustomInstructions != null) cfg.CustomInstructions = s.CustomInstructions;
        if (s.BotName            != null) cfg.BotName            = s.BotName;
        if (s.Temperature        != null) cfg.Temperature        = s.Temperature.Value;
        if (s.MaxTokens          != null) cfg.MaxTokens          = s.MaxTokens.Value;
        if (s.SilenceThresholdMs != null) cfg.SilenceThresholdMs = s.SilenceThresholdMs.Value;
        if (s.RambleModeEnabled  != null) cfg.RambleModeEnabled  = s.RambleModeEnabled.Value;
        if (s.RambleThresholdMs  != null) cfg.RambleThresholdMs  = s.RambleThresholdMs.Value;
        if (s.RambleSystemHint   != null) cfg.RambleSystemHint   = s.RambleSystemHint;
        if (s.TimeZone           != null) cfg.TimeZone           = s.TimeZone;
        if (s.ModelName          != null) cfg.ModelName          = s.ModelName;
        return cfg;
    }
}
