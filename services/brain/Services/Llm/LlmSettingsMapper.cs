using BrainService.Domain.Llm;
using BrainService.Proto.Brain;

namespace BrainService.Services.Llm;

public static class LlmSettingsMapper
{
    // Field descriptor types

    private abstract class Field
    {
        public abstract string ProtoName { get; }

        // Proto operations - no-ops by default for non-proto fields.
        public virtual void ApplyPatch(GuildLlmConfig src, GuildLlmSettings dst) { }
        public virtual void Clear(GuildLlmSettings dst) { }
        public virtual void CopyOverrideToProto(GuildLlmSettings src, GuildLlmConfig dst) { }
        public virtual void CopyResolvedToProto(ResolvedLlmSettings src, GuildLlmConfig dst) { }

        public abstract void Resolve(GuildLlmSettings? src, ResolvedLlmSettings defaults, ResolvedLlmSettings dst);
        public abstract void ReadDefault(IConfigurationSection section, string systemPrompt, string rambleHint, ResolvedLlmSettings dst);
    }

    // String fields

    private sealed class StringField(
        string                                                  protoName,
        Func<GuildLlmConfig, bool>?                             hasInProto,
        Func<GuildLlmConfig, string>?                           fromProto,
        Action<GuildLlmConfig, string>?                         toProto,
        Func<GuildLlmSettings, string?>                         fromOverride,
        Action<GuildLlmSettings, string?>                       setOverride,
        Func<ResolvedLlmSettings, string?>                      fromResolved,
        Action<ResolvedLlmSettings, string?>                    setResolved,
        Func<IConfigurationSection, string, string, string?>    readDefault
    ) : Field
    {
        public override string ProtoName => protoName;

        public override void ApplyPatch(GuildLlmConfig src, GuildLlmSettings dst)
        {
            if (hasInProto != null && fromProto != null && hasInProto(src))
                setOverride(dst, fromProto(src));
        }

        public override void Clear(GuildLlmSettings dst) => setOverride(dst, null);

        public override void CopyOverrideToProto(GuildLlmSettings src, GuildLlmConfig dst)
        {
            if (toProto == null) return;
            var v = fromOverride(src);
            if (v != null) toProto(dst, v);
        }

        public override void CopyResolvedToProto(ResolvedLlmSettings src, GuildLlmConfig dst)
        {
            if (toProto == null) return;
            var v = fromResolved(src);
            if (v != null) toProto(dst, v);
        }

        public override void Resolve(GuildLlmSettings? src, ResolvedLlmSettings defaults, ResolvedLlmSettings dst)
            => setResolved(dst, (src != null ? fromOverride(src) : null) ?? fromResolved(defaults));

        public override void ReadDefault(IConfigurationSection section, string sp, string rh, ResolvedLlmSettings dst)
            => setResolved(dst, readDefault(section, sp, rh));
    }

    // Nullable value-type fields  (int?, float?, bool?, enum?)

    private sealed class ValueField<T>(
        string                                         protoName,
        Func<GuildLlmConfig, bool>?                    hasInProto,
        Func<GuildLlmConfig, T>?                       fromProto,
        Action<GuildLlmConfig, T>?                     toProto,
        Func<GuildLlmSettings, T?>                     fromOverride,
        Action<GuildLlmSettings, T?>                   setOverride,
        Func<ResolvedLlmSettings, T>                   fromResolved,
        Action<ResolvedLlmSettings, T>                 setResolved,
        Func<IConfigurationSection, string, string, T> readDefault
    ) : Field where T : struct
    {
        public override string ProtoName => protoName;

        public override void ApplyPatch(GuildLlmConfig src, GuildLlmSettings dst)
        {
            if (hasInProto != null && fromProto != null && hasInProto(src))
                setOverride(dst, fromProto(src));
        }

        public override void Clear(GuildLlmSettings dst) => setOverride(dst, null);

        public override void CopyOverrideToProto(GuildLlmSettings src, GuildLlmConfig dst)
        {
            if (toProto == null) return;
            var v = fromOverride(src);
            if (v.HasValue) toProto(dst, v.Value);
        }

        public override void CopyResolvedToProto(ResolvedLlmSettings src, GuildLlmConfig dst)
        {
            toProto?.Invoke(dst, fromResolved(src));
        }

        public override void Resolve(GuildLlmSettings? src, ResolvedLlmSettings defaults, ResolvedLlmSettings dst)
            => setResolved(dst, (src != null ? fromOverride(src) : null) ?? fromResolved(defaults));

        public override void ReadDefault(IConfigurationSection section, string sp, string rh, ResolvedLlmSettings dst)
            => setResolved(dst, readDefault(section, sp, rh));
    }
    
    // Field registration
    private static readonly Field[] Fields =
    [
        // -- String fields with proto bindings --

        new StringField(
            protoName:    "system_prompt",
            hasInProto:   p => p.HasSystemPrompt,
            fromProto:    p => p.SystemPrompt,
            toProto:      (p, v) => p.SystemPrompt = v,
            fromOverride: s => s.SystemPrompt,
            setOverride:  (s, v) => s.SystemPrompt = v,
            fromResolved: r => r.SystemPrompt,
            setResolved:  (r, v) => r.SystemPrompt = v ?? "",
            readDefault:  (sec, sp, _) => sec["SystemPrompt"] ?? sp
        ),
        new StringField(
            protoName:    "custom_instructions",
            hasInProto:   p => p.HasCustomInstructions,
            fromProto:    p => p.CustomInstructions,
            toProto:      (p, v) => p.CustomInstructions = v,
            fromOverride: s => s.CustomInstructions,
            setOverride:  (s, v) => s.CustomInstructions = v,
            fromResolved: r => r.CustomInstructions,
            setResolved:  (r, v) => r.CustomInstructions = v,
            readDefault:  (sec, _, _) => sec["CustomInstructions"]
        ),
        new StringField(
            protoName:    "bot_name",
            hasInProto:   p => p.HasBotName,
            fromProto:    p => p.BotName,
            toProto:      (p, v) => p.BotName = v,
            fromOverride: s => s.BotName,
            setOverride:  (s, v) => s.BotName = v,
            fromResolved: r => r.BotName,
            setResolved:  (r, v) => r.BotName = v ?? "",
            readDefault:  (sec, _, _) => sec["BotName"] ?? "Narek Narencjusz"
        ),
        new StringField(
            protoName:    "model_name",
            hasInProto:   p => p.HasModelName,
            fromProto:    p => p.ModelName,
            toProto:      (p, v) => p.ModelName = v,
            fromOverride: s => s.ModelName,
            setOverride:  (s, v) => s.ModelName = v,
            fromResolved: r => r.ModelName,
            setResolved:  (r, v) => r.ModelName = v,
            readDefault:  (sec, _, _) => sec["ModelName"]
        ),
        new StringField(
            protoName:    "ramble_system_hint",
            hasInProto:   p => p.HasRambleSystemHint,
            fromProto:    p => p.RambleSystemHint,
            toProto:      (p, v) => p.RambleSystemHint = v,
            fromOverride: s => s.RambleSystemHint,
            setOverride:  (s, v) => s.RambleSystemHint = v,
            fromResolved: r => r.RambleSystemHint,
            setResolved:  (r, v) => r.RambleSystemHint = v ?? "",
            readDefault:  (sec, _, rh) => sec["RambleSystemHint"] ?? rh
        ),
        new StringField(
            protoName:    "time_zone",
            hasInProto:   p => p.HasTimeZone,
            fromProto:    p => p.TimeZone,
            toProto:      (p, v) => p.TimeZone = v,
            fromOverride: s => s.TimeZone,
            setOverride:  (s, v) => s.TimeZone = v,
            fromResolved: r => r.TimeZone,
            setResolved:  (r, v) => r.TimeZone = v ?? "UTC",
            readDefault:  (sec, _, _) => sec["TimeZone"] ?? "UTC"
        ),

        // -- String fields without proto bindings --

        new StringField(
            protoName:    "",
            hasInProto:   null,
            fromProto:    null,
            toProto:      null,
            fromOverride: s => s.ProviderUrl,
            setOverride:  (s, v) => s.ProviderUrl = v,
            fromResolved: r => r.ProviderUrl,
            setResolved:  (r, v) => r.ProviderUrl = v ?? "",
            readDefault:  (sec, _, _) => sec["ProviderUrl"] ?? "http://llm:11434"
        ),

        // -- Float fields --

        new ValueField<float>(
            protoName:    "temperature",
            hasInProto:   p => p.HasTemperature,
            fromProto:    p => p.Temperature,
            toProto:      (p, v) => p.Temperature = v,
            fromOverride: s => s.Temperature,
            setOverride:  (s, v) => s.Temperature = v,
            fromResolved: r => r.Temperature,
            setResolved:  (r, v) => r.Temperature = v,
            readDefault:  (sec, _, _) => sec.GetValue("Temperature", 0.8f)
        ),
        new ValueField<float>(
            protoName:    "repetition_penalty",
            hasInProto:   p => p.HasRepetitionPenalty,
            fromProto:    p => p.RepetitionPenalty,
            toProto:      (p, v) => p.RepetitionPenalty = v,
            fromOverride: s => s.RepetitionPenalty,
            setOverride:  (s, v) => s.RepetitionPenalty = v,
            fromResolved: r => r.RepetitionPenalty,
            setResolved:  (r, v) => r.RepetitionPenalty = v,
            readDefault:  (sec, _, _) => sec.GetValue("RepetitionPenalty", 0f)
        ),

        // -- Int fields --

        new ValueField<int>(
            protoName:    "max_tokens",
            hasInProto:   p => p.HasMaxTokens,
            fromProto:    p => p.MaxTokens,
            toProto:      (p, v) => p.MaxTokens = v,
            fromOverride: s => s.MaxTokens,
            setOverride:  (s, v) => s.MaxTokens = v,
            fromResolved: r => r.MaxTokens,
            setResolved:  (r, v) => r.MaxTokens = v,
            readDefault:  (sec, _, _) => sec.GetValue("MaxTokens", 1024)
        ),
        new ValueField<int>(
            protoName:    "silence_threshold_ms",
            hasInProto:   p => p.HasSilenceThresholdMs,
            fromProto:    p => p.SilenceThresholdMs,
            toProto:      (p, v) => p.SilenceThresholdMs = v,
            fromOverride: s => s.SilenceThresholdMs,
            setOverride:  (s, v) => s.SilenceThresholdMs = v,
            fromResolved: r => r.SilenceThresholdMs,
            setResolved:  (r, v) => r.SilenceThresholdMs = v,
            readDefault:  (sec, _, _) => sec.GetValue("SilenceThresholdMs", 1500)
        ),
        new ValueField<int>(
            protoName:    "ramble_threshold_ms",
            hasInProto:   p => p.HasRambleThresholdMs,
            fromProto:    p => p.RambleThresholdMs,
            toProto:      (p, v) => p.RambleThresholdMs = v,
            fromOverride: s => s.RambleThresholdMs,
            setOverride:  (s, v) => s.RambleThresholdMs = v,
            fromResolved: r => r.RambleThresholdMs,
            setResolved:  (r, v) => r.RambleThresholdMs = v,
            readDefault:  (sec, _, _) => sec.GetValue("RambleThresholdMs", 90_000)
        ),

        // -- Int fields without proto bindings --

        new ValueField<int>(
            protoName:    "",
            hasInProto:   null,
            fromProto:    null,
            toProto:      null,
            fromOverride: s => s.ContextWindow,
            setOverride:  (s, v) => s.ContextWindow = v,
            fromResolved: r => r.ContextWindow,
            setResolved:  (r, v) => r.ContextWindow = v,
            readDefault:  (sec, _, _) => sec.GetValue("ContextWindow", 8192)
        ),
        new ValueField<int>(
            protoName:    "",
            hasInProto:   null,
            fromProto:    null,
            toProto:      null,
            fromOverride: s => s.UserJoinGraceMs,
            setOverride:  (s, v) => s.UserJoinGraceMs = v,
            fromResolved: r => r.UserJoinGraceMs,
            setResolved:  (r, v) => r.UserJoinGraceMs = v,
            readDefault:  (sec, _, _) => sec.GetValue("UserJoinGraceMs", 3000)
        ),
        new ValueField<int>(
            protoName:    "",
            hasInProto:   null,
            fromProto:    null,
            toProto:      null,
            fromOverride: s => s.RambleMinResponseLength,
            setOverride:  (s, v) => s.RambleMinResponseLength = v,
            fromResolved: r => r.RambleMinResponseLength,
            setResolved:  (r, v) => r.RambleMinResponseLength = v,
            readDefault:  (sec, _, _) => sec.GetValue("RambleMinResponseLength", 10)
        ),

        // -- Bool fields --

        new ValueField<bool>(
            protoName:    "ramble_mode_enabled",
            hasInProto:   p => p.HasRambleModeEnabled,
            fromProto:    p => p.RambleModeEnabled,
            toProto:      (p, v) => p.RambleModeEnabled = v,
            fromOverride: s => s.RambleModeEnabled,
            setOverride:  (s, v) => s.RambleModeEnabled = v,
            fromResolved: r => r.RambleModeEnabled,
            setResolved:  (r, v) => r.RambleModeEnabled = v,
            readDefault:  (sec, _, _) => sec.GetValue("RambleModeEnabled", false)
        ),

        // -- Enum fields without proto bindings --

        new ValueField<LlmProviderType>(
            protoName:    "",
            hasInProto:   null,
            fromProto:    null,
            toProto:      null,
            fromOverride: s => s.ProviderType,
            setOverride:  (s, v) => s.ProviderType = v,
            fromResolved: r => r.ProviderType,
            setResolved:  (r, v) => r.ProviderType = v,
            readDefault:  (sec, _, _) => sec["ProviderType"] != null
                              ? Enum.Parse<LlmProviderType>(sec["ProviderType"]!, ignoreCase: true)
                              : LlmProviderType.Ollama
        ),
    ];

    // Lookup table for clear_fields by proto name - built once on first use.
    private static readonly Lazy<Dictionary<string, Field>> ProtoLookup = new(() =>
        Fields
            .Where(f => f.ProtoName.Length > 0)
            .ToDictionary(f => f.ProtoName));
    
    // Public API
    public static ResolvedLlmSettings BuildDefaults(
        IConfigurationSection section,
        string systemPrompt,
        string rambleHint)
    {
        var dst = new ResolvedLlmSettings();
        foreach (var f in Fields)
            f.ReadDefault(section, systemPrompt, rambleHint, dst);

        // EnabledTools has no config representation - always null at default level
        dst.EnabledTools = null;
        return dst;
    }
    
    public static ResolvedLlmSettings Resolve(GuildLlmSettings? g, ResolvedLlmSettings defaults)
    {
        var dst = new ResolvedLlmSettings();
        foreach (var f in Fields)
            f.Resolve(g, defaults, dst);

        // EnabledTools is handled separately (list type, no proto binding)
        dst.EnabledTools = g?.EnabledTools ?? defaults.EnabledTools;
        return dst;
    }
    public static GuildLlmConfig ToResolvedProto(ResolvedLlmSettings s)
    {
        var cfg = new GuildLlmConfig();
        foreach (var f in Fields)
            f.CopyResolvedToProto(s, cfg);
        return cfg;
    }
    
    public static GuildLlmConfig ToOverrideProto(GuildLlmSettings s)
    {
        var cfg = new GuildLlmConfig();
        foreach (var f in Fields)
            f.CopyOverrideToProto(s, cfg);
        return cfg;
    }
    
    public static void ApplyPatch(GuildLlmConfig patch, GuildLlmSettings target)
    {
        foreach (var f in Fields)
            f.ApplyPatch(patch, target);
    }
    
    public static void ApplyClearFields(
        IEnumerable<string> fieldNames,
        GuildLlmSettings target,
        ILogger? logger = null)
    {
        var lookup = ProtoLookup.Value;
        foreach (var name in fieldNames)
        {
            if (lookup.TryGetValue(name, out var f))
                f.Clear(target);
            else
                logger?.LogWarning("ApplyClearFields: unknown field name '{Field}'", name);
        }
    }
}
