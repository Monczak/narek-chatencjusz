using System.Text.RegularExpressions;
using BrainService.Domain.Llm;
using BrainService.Domain.Session;
using BrainService.Services.Audio;
using BrainService.Services.Memory;
using BrainService.Services.Session;

namespace BrainService.Services.Llm;

public partial class LlmContextBuilder(
    GuildSettingsService settingsService,
    OllamaModelService ollamaModelService,
    VoiceSessionHistoryService historyService,
    ITokenCounter tokenCounter,
    GuildMemoryService memoryService,
    SoundboardService soundboardService,
    ToolRegistry toolRegistry,
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
        
        var memories = await memoryService.GetAllAsync(sessionState.GuildId, ct);
        var dynamicBlock = BuildDynamicContextBlock(settings, sessionState, memories);
        
        // Step 2: Measure fixed token costs
        var systemPromptTokens = await tokenCounter.CountTokensAsync(systemPrompt, ct);
        var customInstructionsTokens = customInstructions != null
            ? await tokenCounter.CountTokensAsync(customInstructions, ct) : 0;
        var dynamicBlockTokens = await tokenCounter.CountTokensAsync(dynamicBlock, ct);
        var toolGuidanceTokens = settings.ToolsEnabled && !string.IsNullOrWhiteSpace(settings.ToolGuidance)
            ? await tokenCounter.CountTokensAsync(settings.ToolGuidance, ct)
            : 0;
        
        var contextWindow = await ollamaModelService.GetContextWindowAsync(settings.ModelName, ct)
            ?? settings.ContextWindow;

        var availableForHistory = contextWindow
            - settings.MaxTokens
            - systemPromptTokens
            - customInstructionsTokens
            - dynamicBlockTokens
            - toolGuidanceTokens
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
        
        if (settings.ToolsEnabled && !string.IsNullOrWhiteSpace(settings.ToolGuidance))
            messages.Add(new LlmMessage("system", settings.ToolGuidance));
        
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
                RepetitionPenalty: settings.RepetitionPenalty,
                MaxTokens: settings.MaxTokens
            ),
            Tools: settings.ToolsEnabled ? [.. toolRegistry.AIFunctions] : []
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
    
    private string BuildDynamicContextBlock(
        ResolvedLlmSettings settings,
        VoiceSessionState sessionState,
        IReadOnlyDictionary<string, string> memories)
    {
        var timeZone = TimeZoneInfo.Utc;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone); }
        catch { /* fallback to Utc */ }
        
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        
        var users = sessionState.Users.Count > 0
            ? string.Join(", ", sessionState.Users.Select(u => u.DisplayName))
            : "none";

        var block = $"""
                ## Current Context
                - Time: {localTime:dddd, dd MMMM yyyy, HH:mm} {timeZone.StandardName}
                - Server: {sessionState.GuildName}{(sessionState.ChannelName != null ? $" (channel: {sessionState.ChannelName})" : "")}
                - Users currently in channel: {users}
                """;
        
        var soundNames = soundboardService.GetSoundNames();
        if (soundNames.Count > 0)
            block += $"\n- Available sounds: {string.Join(", ", soundNames)}";

        if (memories.Count > 0)
        {
            block += "\n- Memories:";
            foreach (var (key, value) in memories)
                block += $"\n  - {key}: {value}";
        }

        return block;
    }
    
    private async Task<List<LlmMessage>> TrimToTokenBudgetAsync(
        List<VoiceSessionEventDocument> events,
        VoiceSessionState sessionState,
        int budget,
        CancellationToken ct)
    {
        if (budget <= 0) return [];

        var messages = new List<LlmMessage>();
        var usedTokens = 0;

        // Walk from newest to oldest, include as many as fit in the budget
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var msg = MakeLlmMessage(events[i], sessionState);
            if (msg == null) continue;

            var tokens = await GetCachedTokenCountAsync(events[i], msg.Content, ct);
            if (usedTokens + tokens > budget) break;

            messages.Insert(0, msg);
            usedTokens += tokens;
        }

        return messages;
    }

    private async Task<int> GetCachedTokenCountAsync(
        VoiceSessionEventDocument evt,
        string text,
        CancellationToken ct)
    {
        if (evt.Data.TryGetValue("token_count", out var cached))
            return cached.AsInt32;

        var count = await tokenCounter.CountTokensAsync(text, ct);
        evt.Data["token_count"] = count;
        return count;
    }

    private static LlmMessage? MakeLlmMessage(VoiceSessionEventDocument evt, VoiceSessionState sessionState) =>
        evt.Type switch
        {
            VoiceSessionEventType.Transcript => new LlmMessage(
                "user",
                $"[{GetDisplayName(evt, sessionState)}]: {evt.Data["text"].AsString}"
            ),
            VoiceSessionEventType.BotResponse => new LlmMessage(
                "assistant",
                evt.Data["content"].AsString
                    + (evt.Data["is_partial"].AsBoolean ? " [interrupted]" : "")
            ),
            VoiceSessionEventType.UserJoined => new LlmMessage(
                "system",
                $"[EVENT] {evt.Data["display_name"].AsString} joined the voice channel."
            ),
            VoiceSessionEventType.UserLeft => new LlmMessage(
                "system",
                $"[EVENT] {evt.Data["display_name"].AsString} left the voice channel."
            ),
            VoiceSessionEventType.SystemNote => new LlmMessage(
                "system",
                evt.Data["note"].AsString
            ),
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
