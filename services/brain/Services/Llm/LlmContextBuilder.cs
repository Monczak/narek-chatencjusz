using System.Text.RegularExpressions;
using BrainService.Domain.Llm;
using BrainService.Domain.Session;
using BrainService.Services.Session;

namespace BrainService.Services.Llm;

public partial class LlmContextBuilder(
    GuildSettingsService settingsService,
    OllamaModelService ollamaModelService,
    VoiceSessionHistoryService historyService,
    ITokenCounter tokenCounter,
    ILogger<LlmContextBuilder> logger)
{
    public async Task<LlmRequest> BuildAsync(
        VoiceSessionState sessionState,
        LlmContextReason reason,
        IReadOnlyList<VoiceSessionEventDocument> pendingEvents,
        CancellationToken ct = default)
    {
        var settings = await settingsService.GetSettingsAsync(sessionState.GuildId);
        
        // Step 1: System prompt
        var systemPrompt = ApplyTemplates(settings, sessionState);
        var customInstructions = settings.CustomInstructions;
        var dynamicBlock = BuildDynamicContextBlock(settings, sessionState);
        
        // Step 2: Measure fixed token costs
        var systemPromptTokens = await tokenCounter.CountTokensAsync(systemPrompt, ct);
        var customInstructionsTokens = customInstructions != null
            ? await tokenCounter.CountTokensAsync(customInstructions, ct) : 0;
        var dynamicBlockTokens = await tokenCounter.CountTokensAsync(dynamicBlock, ct);
        
        var contextWindow = await ollamaModelService.GetContextWindowAsync(settings.ModelName, ct)
            ?? settings.ContextWindow;

        var availableForHistory = contextWindow
            - settings.MaxTokens
            - systemPromptTokens
            - customInstructionsTokens
            - dynamicBlockTokens
            - ContextWindowSafetyMargin;
        
        // Step 3: Load LLM-visible session history
        var rawHistory = await historyService.GetLlmContextEventsAsync(sessionState.SessionId, LlmContextEventLimit, ct);
        
        // Step 4: Trim from the oldest end to fit token budget
        var historyMessages = await TrimToTokenBudgetAsync(rawHistory, sessionState, availableForHistory, ct);
        
        // Step 5: Get pending events
        var pendingMessages = pendingEvents
            .Select(e => MakeLlmMessage(e, sessionState))
            .Where(m => m != null)
            .Cast<LlmMessage>()
            .ToList();
        
        // Step 6: Build trigger hint
        var triggerHint = BuildTriggerHint(reason, sessionState, settings);
        
        // Step 7: Assemble final message list
        var messages = new List<LlmMessage>
        {
            new("system", systemPrompt)
        };

        if (!string.IsNullOrWhiteSpace(customInstructions))
            messages.Add(new LlmMessage("system", customInstructions));
        
        messages.Add(new LlmMessage("system", dynamicBlock));
        messages.AddRange(historyMessages);
        messages.AddRange(pendingMessages);

        if (triggerHint != null)
            messages.Add(new LlmMessage("system", triggerHint));
        
        var request = new LlmRequest(
            Messages: messages,
            Settings: new LlmGenerationSettings(
                ModelName: settings.ModelName,
                Temperature: settings.Temperature,
                MaxTokens: settings.MaxTokens
            )
        );

        logger.LogDebug("[LLM] Context built: {MsgCount} messages, {Available} tokens available for history",
            messages.Count, availableForHistory);

        return request;
    }
    
    private static string ApplyTemplates(ResolvedLlmSettings settings, VoiceSessionState sessionState)
    {
        var timeZone = TimeZoneInfo.Utc;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone); }
        catch { /* fallback to Utc if string is invalid */ }
    
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        
        return SystemPromptTemplateRegex().Replace(settings.SystemPrompt, m => m.Groups[1].Value switch
        {
            "bot_name" => settings.BotName,
            "guild_name" => sessionState.GuildName,
            "datetime" => localTime.ToString("dddd, dd MMMM yyyy, HH:mm UTC"),
            _ => m.Value
        });
    }
    
    private static string BuildDynamicContextBlock(ResolvedLlmSettings settings, VoiceSessionState sessionState)
    {
        var timeZone = TimeZoneInfo.Utc;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone); }
        catch { /* fallback to Utc */ }
        
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        
        var users = sessionState.Users.Count > 0
            ? string.Join(", ", sessionState.Users.Select(u => u.DisplayName))
            : "none";

        return $"""
                ## Current Context
                - Time: {localTime:dddd, dd MMMM yyyy, HH:mm} {timeZone.StandardName}
                - Server: {sessionState.GuildName}{(sessionState.ChannelName != null ? $" (channel: {sessionState.ChannelName})" : "")}
                - Users currently in channel: {users}
                """;
    }
    
    private async Task<List<LlmMessage>> TrimToTokenBudgetAsync(
        List<VoiceSessionEventDocument> events,
        VoiceSessionState sessionState,
        int budget,
        CancellationToken ct)
    {
        var result = new LinkedList<LlmMessage>();
        var used = 0;

        for (var i = events.Count - 1; i >= 0; i--)
        {
            var msg = MakeLlmMessage(events[i], sessionState);
            if (msg == null) continue;

            var tokens = await tokenCounter.CountTokensAsync(msg.Content, ct);
            if (used + tokens > budget) break;

            result.AddFirst(msg);
            used += tokens;
        }

        return result.ToList();
    }
    
    private static LlmMessage? MakeLlmMessage(VoiceSessionEventDocument evt, VoiceSessionState sessionState) =>
        evt.Type switch
        {
            VoiceSessionEventType.Transcript when evt.Data.Contains("text") =>
                new LlmMessage("user",
                    $"[{GetDisplayName(evt, sessionState)}]: {evt.Data["text"].AsString}"),

            VoiceSessionEventType.BotResponse when evt.Data.Contains("content") =>
                new LlmMessage("assistant",
                    evt.Data["content"].AsString
                    + (evt.Data.Contains("is_partial") && evt.Data["is_partial"].AsBoolean
                        ? " [interrupted]"
                        : "")),

            VoiceSessionEventType.UserJoined when evt.Data.Contains("display_name") =>
                new LlmMessage("system",
                    $"[EVENT] {evt.Data["display_name"].AsString} joined the voice channel."),

            VoiceSessionEventType.UserLeft when evt.Data.Contains("display_name") =>
                new LlmMessage("system",
                    $"[EVENT] {evt.Data["display_name"].AsString} left the voice channel."),

            VoiceSessionEventType.SystemNote when evt.Data.Contains("note") =>
                new LlmMessage("system", evt.Data["note"].AsString),

            _ => null
        };

    private static string GetDisplayName(VoiceSessionEventDocument evt, VoiceSessionState sessionState)
    {
        if (evt.UserId.HasValue)
        {
            var match = sessionState.Users.FirstOrDefault(u => (long)u.UserId == evt.UserId.Value);
            if (match != null) return match.DisplayName;
        }

        return evt.UserId.HasValue ? $"User {evt.UserId}" : "Unknown User";
    }

    private static string? BuildTriggerHint(LlmContextReason reason, VoiceSessionState sessionState, ResolvedLlmSettings settings) =>
        reason switch
        {
            LlmContextReason.UserSilence => null, // No hint needed - normal flow
            LlmContextReason.Ramble => $"[HINT] {settings.RambleSystemHint}",
            LlmContextReason.UserJoined => "[HINT] A user just joined the channel. You may greet them if appropriate.",
            LlmContextReason.UserLeft => "[HINT] A user just left the channel. You may acknowledge this if appropriate.",
            _ => null
        };

    private const int ContextWindowSafetyMargin = 64;
    private const int LlmContextEventLimit = 500;
    
    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex SystemPromptTemplateRegex();
}
