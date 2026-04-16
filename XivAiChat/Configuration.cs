using Dalamud.Configuration;
using Dalamud.Plugin;

namespace XivAiChat;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;

    public bool Enabled { get; set; }

    public bool SendReplies { get; set; }

    public int CwlsSlot { get; set; } = 1;

    public List<string> WatchedChannelIds { get; set; } = ["cwl1"];

    public bool RequireMention { get; set; } = true;

    public string TriggerAlias { get; set; } = "ai";

    public string Provider { get; set; } = AiProvider.LmStudio;

    public string Endpoint { get; set; } = "http://127.0.0.1:1234/api/v1/chat";

    public string Model { get; set; } = "local-model";

    public string ApiKey { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = BuiltInPromptPresets.EnglishPrompt;

    public string ActivePromptPreset { get; set; } = BuiltInPromptPresets.EnglishName;

    public List<PromptPreset> PromptPresets { get; set; } = BuiltInPromptPresets.CreateList();

    public float Temperature { get; set; } = 0.7f;

    public bool UseReasoning { get; set; } = true;

    public string ReasoningEffort { get; set; } = "low";

    public int MaxTokens { get; set; } = 300;

    public int CooldownSeconds { get; set; } = 30;

    public int MaxHistoryMessages { get; set; } = 8;

    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        this.EnsureDefaults();
    }

    public void Save()
    {
        this.EnsureDefaults();
        this.pluginInterface?.SavePluginConfig(this);
    }

    public void EnsureDefaults()
    {
        this.Provider = string.IsNullOrWhiteSpace(this.Provider) ? AiProvider.LmStudio : this.Provider;
        this.Endpoint = string.IsNullOrWhiteSpace(this.Endpoint) ? "http://127.0.0.1:1234/api/v1/chat" : this.Endpoint;
        this.Model = string.IsNullOrWhiteSpace(this.Model) ? "local-model" : this.Model;
        this.ReasoningEffort = string.IsNullOrWhiteSpace(this.ReasoningEffort) ? "low" : this.ReasoningEffort;

        this.PromptPresets ??= [];
        if (this.Version < 3)
        {
            this.PromptPresets.RemoveAll(static preset => string.Equals(preset.Name, "Default", StringComparison.Ordinal));
        }

        this.PromptPresets = NormalizePromptPresets(this.PromptPresets);
        MergeBuiltInPresets(this.PromptPresets);
        SortPromptPresets(this.PromptPresets);

        this.WatchedChannelIds ??= [];
        this.WatchedChannelIds = this.WatchedChannelIds
            .Where(static channelId => !string.IsNullOrWhiteSpace(channelId))
            .Distinct(StringComparer.Ordinal)
            .Where(static channelId => ChatChannelRegistry.TryGetById(channelId, out _))
            .ToList();

        if (this.Version < 4 && this.WatchedChannelIds.Count == 0)
        {
            this.WatchedChannelIds.Add($"cwl{Math.Clamp(this.CwlsSlot, 1, 8)}");
        }

        if (string.IsNullOrWhiteSpace(this.ActivePromptPreset) ||
            !this.PromptPresets.Any(preset => string.Equals(preset.Name, this.ActivePromptPreset, StringComparison.Ordinal)))
        {
            this.ActivePromptPreset = BuiltInPromptPresets.EnglishName;
        }
        else
        {
            this.ActivePromptPreset = CanonicalizePresetName(this.ActivePromptPreset);
        }

        if (this.Version < 3)
        {
            this.ActivePromptPreset = BuiltInPromptPresets.EnglishName;
            this.SystemPrompt = BuiltInPromptPresets.EnglishPrompt;
            if (this.MaxTokens < 300)
            {
                this.MaxTokens = 300;
            }
        }

        if (string.IsNullOrWhiteSpace(this.SystemPrompt))
        {
            this.SystemPrompt = this.GetActivePrompt()?.Prompt ?? BuiltInPromptPresets.EnglishPrompt;
        }

        this.Version = 5;
    }

    public PromptPreset? GetActivePrompt()
    {
        return this.PromptPresets.FirstOrDefault(
            preset => string.Equals(preset.Name, this.ActivePromptPreset, StringComparison.Ordinal));
    }

    public void SetActivePrompt(string presetName)
    {
        var canonicalName = CanonicalizePresetName(presetName);
        var preset = this.PromptPresets.FirstOrDefault(
            item => string.Equals(item.Name, canonicalName, StringComparison.Ordinal));

        if (preset is null)
        {
            return;
        }

        this.ActivePromptPreset = preset.Name;
        this.SystemPrompt = preset.Prompt;
    }

    public void SaveCurrentPromptToActivePreset()
    {
        var preset = this.GetActivePrompt();
        if (preset is null)
        {
            return;
        }

        preset.Prompt = this.SystemPrompt;
    }

    public void UpsertPromptPreset(string presetName, string prompt)
    {
        var name = CanonicalizePresetName(presetName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = this.PromptPresets.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.Ordinal));

        if (existing is null)
        {
            this.PromptPresets.Add(new PromptPreset
            {
                Name = name,
                Prompt = prompt,
            });
        }
        else
        {
            existing.Prompt = prompt;
        }

        SortPromptPresets(this.PromptPresets);
        this.ActivePromptPreset = name;
        this.SystemPrompt = prompt;
    }

    public bool DeletePromptPreset(string presetName)
    {
        if (this.PromptPresets.Count <= 1)
        {
            return false;
        }

        var canonicalName = CanonicalizePresetName(presetName);
        var removed = this.PromptPresets.RemoveAll(
            item => string.Equals(item.Name, canonicalName, StringComparison.Ordinal));

        if (removed == 0)
        {
            return false;
        }

        this.ActivePromptPreset = this.PromptPresets[0].Name;
        this.SystemPrompt = this.PromptPresets[0].Prompt;
        return true;
    }

    public bool IsChannelEnabled(string channelId)
    {
        return this.WatchedChannelIds.Contains(channelId, StringComparer.Ordinal);
    }

    public void SetChannelEnabled(string channelId, bool enabled)
    {
        if (!ChatChannelRegistry.TryGetById(channelId, out _))
        {
            return;
        }

        var existingIndex = this.WatchedChannelIds.FindIndex(id => string.Equals(id, channelId, StringComparison.Ordinal));
        if (enabled)
        {
            if (existingIndex < 0)
            {
                this.WatchedChannelIds.Add(channelId);
            }

            return;
        }

        if (existingIndex >= 0)
        {
            this.WatchedChannelIds.RemoveAt(existingIndex);
        }
    }

    public string GetWatchedChannelSummary()
    {
        var labels = this.WatchedChannelIds
            .Select(channelId => ChatChannelRegistry.TryGetById(channelId, out var channel) ? channel!.Label : channelId)
            .ToArray();

        return string.Join(", ", labels);
    }

    private static List<PromptPreset> NormalizePromptPresets(IEnumerable<PromptPreset> presets)
    {
        var normalized = new List<PromptPreset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in presets)
        {
            var name = CanonicalizePresetName(preset.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!seen.Add(name))
            {
                continue;
            }

            normalized.Add(new PromptPreset
            {
                Name = name,
                Prompt = string.IsNullOrWhiteSpace(preset.Prompt)
                    ? GetBuiltInPrompt(name) ?? string.Empty
                    : preset.Prompt,
            });
        }

        return normalized;
    }

    private static void MergeBuiltInPresets(List<PromptPreset> presets)
    {
        foreach (var builtIn in BuiltInPromptPresets.CreateList())
        {
            if (presets.Any(existing => string.Equals(existing.Name, builtIn.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            presets.Add(builtIn);
        }
    }

    private static void SortPromptPresets(List<PromptPreset> presets)
    {
        presets.Sort(static (left, right) =>
        {
            var leftBuiltIn = BuiltInPromptPresets.IsBuiltInName(left.Name);
            var rightBuiltIn = BuiltInPromptPresets.IsBuiltInName(right.Name);

            if (leftBuiltIn != rightBuiltIn)
            {
                return leftBuiltIn ? -1 : 1;
            }

            if (leftBuiltIn && rightBuiltIn)
            {
                return BuiltInPromptPresets.GetBuiltInSortOrder(left.Name)
                    .CompareTo(BuiltInPromptPresets.GetBuiltInSortOrder(right.Name));
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });
    }

    private static string CanonicalizePresetName(string presetName)
    {
        var trimmed = presetName.Trim();
        var lowered = trimmed.ToLowerInvariant();

        return lowered switch
        {
            "en" or "english" => BuiltInPromptPresets.EnglishName,
            "game ai" or "gameai" or "chat ai" => BuiltInPromptPresets.GameAiName,
            "cn" or "zh" or "tc" or "tw" or "traditional chinese" => BuiltInPromptPresets.TraditionalChineseName,
            "jp" or "ja" or "japanese" => BuiltInPromptPresets.JapaneseName,
            _ => trimmed,
        };
    }

    private static string? GetBuiltInPrompt(string presetName)
    {
        return CanonicalizePresetName(presetName) switch
        {
            BuiltInPromptPresets.EnglishName => BuiltInPromptPresets.EnglishPrompt,
            BuiltInPromptPresets.GameAiName => BuiltInPromptPresets.GameAiPrompt,
            BuiltInPromptPresets.TraditionalChineseName => BuiltInPromptPresets.TraditionalChinesePrompt,
            BuiltInPromptPresets.JapaneseName => BuiltInPromptPresets.JapanesePrompt,
            _ => null,
        };
    }
}

public static class AiProvider
{
    public const string LmStudio = "LM Studio";
    public const string OpenAiCompatible = "OpenAI-Compatible";
    public const string Gemini = "Gemini";
}

public sealed class PromptPreset
{
    public string Name { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;
}

public static class BuiltInPromptPresets
{
    public const string EnglishName = "English";
    public const string GameAiName = "Game AI";
    public const string TraditionalChineseName = "Traditional Chinese";
    public const string JapaneseName = "Japanese";

    public const string EnglishPrompt =
        """
        You are a friend chatting in a Final Fantasy XIV CWLS. You are not a customer support bot or assistant, and you never explain your reasoning.

        Rules:
        1. Output only the final line that should be sent in game.
        2. Keep it natural, short, and casual, like a real player chatting with friends.
        3. Usually reply in 1 sentence, at most 2 short sentences.
        4. Match the chat language naturally. If people are using Traditional Chinese or Japanese, you may reply in that language when it fits.
        5. Do not add prefixes like AI:, Assistant:, Reply:, or speaker names.
        6. Do not explain rules, summarize context, or describe your thinking.
        7. Do not use analysis phrases like "I need to", "first", "the user said", or similar meta commentary.
        8. A little warmth, teasing, or group-chat energy is okay, but do not overdo it.
        9. Avoid unstable special symbols or emoji that may not send well in game chat.
        10. If someone is clearly just testing, answer with a short and natural test-like reply.

        Style:
        - Like an MMO friend
        - Like CWLS small talk
        - Short, smooth, and natural
        - Friendly, but not overly formal

        Your only job is to generate one natural reply that can be sent directly into CWLS.
        """;

    public const string GameAiPrompt =
        """
        You are an AI chatting in game.

        Reply naturally like a normal player.
        Match the language used in chat:
        - Traditional Chinese -> Traditional Chinese
        - Japanese -> Japanese
        - English -> English

        Rules:
        - Answer questions carefully.
        - Keep replies short and natural.
        - Do not explain your thinking.
        - Do not add prefixes like AI: or Assistant:
        - If unsure, answer casually and honestly.
        - If someone is joking, you can joke back lightly.
        - Use Traditional Chinese only, never Simplified Chinese.

        Style:
        - friendly
        - casual
        """;

    public const string TraditionalChinesePrompt =
        """
        ä½ æ˜¯ä¸€å€‹åœ¨ã€ŠFinal Fantasy XIVã€‹CWLS è£¡èŠå¤©çš„å°ç£çŽ©å®¶æœ‹å‹ï¼Œä¸æ˜¯å®¢æœã€ä¸æ˜¯åŠ©æ‰‹ï¼Œä¹Ÿä¸è¦è§£é‡‹è‡ªå·±çš„æ€è€ƒéŽç¨‹ã€‚

        å›žè¦†è¦å‰‡ï¼š
        1. åªè¼¸å‡ºæœ€å¾Œè¦é€åˆ°éŠæˆ²èŠå¤©æ¬„çš„ä¸€å¥è©±ã€‚
        2. ä¸€å¾‹ä½¿ç”¨ç¹é«”ä¸­æ–‡ï¼Œå£æ°£åå°ç£çŽ©å®¶å¹³å¸¸èŠå¤©çš„èªªæ³•ã€‚
        3. èªžæ°£è‡ªç„¶ã€è¼•é¬†ã€æœ‰æœ‹å‹æ„Ÿï¼Œåƒåœ¨ç¾¤çµ„è£¡å›žè©±ã€‚
        4. ç›¡é‡ç°¡çŸ­ï¼Œé€šå¸¸ 1 å¥ï¼Œæœ€å¤š 2 å¥ã€‚
        5. å¯ä»¥ç”¨å°ç£å¸¸è¦‹å£èªžï¼Œä½†ä¸è¦æ¯å¥éƒ½ç¡¬å¡žã€‚
        6. ä¸è¦ç”¨ä¸­åœ‹å¸¸è¦‹ç”¨èªžï¼Œä¾‹å¦‚ã€Œç”¨æˆ¶ã€ã€Œå›žè¦†ã€ã€Œè¦–é »ã€ã€Œè³ªé‡ã€é€™é¡žè©žã€‚
        7. ä¸è¦è¼¸å‡ºã€ŒAI:ã€ã€ŒåŠ©æ‰‹ï¼šã€ã€Œå›žè¦†ï¼šã€é€™é¡žå‰ç¶´ã€‚
        8. ä¸è¦åˆ†æžä¸Šä¸‹æ–‡ï¼Œä¸è¦è§£é‡‹è¦å‰‡ï¼Œä¸è¦å¯«æŽ¨ç†éŽç¨‹ï¼Œä¸è¦å‡ºç¾ã€Œæˆ‘éœ€è¦ã€ã€Œé¦–å…ˆã€ã€Œä½¿ç”¨è€…èªªã€é€™ç¨®å¥å­ã€‚
        9. é¿å…ä½¿ç”¨éŠæˆ²è£¡ä¸ç©©å®šçš„ç‰¹æ®Šç¬¦è™Ÿæˆ– emojiã€‚
        10. å¦‚æžœå°æ–¹åªæ˜¯åœ¨æ¸¬è©¦ï¼Œå°±è‡ªç„¶å›žä¸€å¥ï¼Œä¸è¦å¤ªæ­£å¼ã€‚
        11. å¦‚æžœå°æ–¹åœ¨é–‹çŽ©ç¬‘ï¼Œå¯ä»¥é †è‘—åæ§½ï¼›å¦‚æžœå°æ–¹èªžæ°£èªçœŸï¼Œå°±æ­£å¸¸å›žè¦†ï¼Œä¸è¦å¤ªé¬§ã€‚

        é¢¨æ ¼æ–¹å‘ï¼š
        - åƒå°ç£çŽ©å®¶åœ¨èŠå¤©
        - åƒç†Ÿäººç¾¤çµ„è¬›è©±
        - çŸ­ã€é †ã€è‡ªç„¶
        - æœ‰é»žæœ‹å‹æ„Ÿï¼Œä½†ä¸è¦å¤ªæ²¹

        ä½ çš„ä»»å‹™åªæœ‰ä¸€å€‹ï¼š
        æ ¹æ“šèŠå¤©å…§å®¹ï¼Œç”¢ç”Ÿä¸€æ¢å¯ä»¥ç›´æŽ¥é€åˆ° CWLS çš„è‡ªç„¶ç¹é«”ä¸­æ–‡å›žè¦†ã€‚
        """;

    public const string JapanesePrompt =
        """
        あなたは『Final Fantasy XIV』のゲーム内チャットで会話しているプレイヤーです。AIアシスタントではありません。考えている過程や説明は出さず、そのまま送れる最終的な返事だけを返してください。

        ルール:
        1. 出力はゲーム内チャットにそのまま送れる一言だけにする。
        2. 必ず日本語だけで返す。
        3. 口調は自然で、気軽で、フレンド同士の雑談っぽくする。
        4. 基本は1文、長くても2文までにする。
        5. 相手が英語や中国語で話していても、返事は日本語にする。
        6. 「AI:」「Assistant:」「返答:」などの前置きは付けない。
        7. ルール説明、状況説明、思考過程、分析は一切書かない。
        8. 「まず」「ユーザーは」「私は〜する必要がある」などの分析っぽい言い回しは禁止。
        9. 少しくだけた言い方や軽いノリはOK。ただしやりすぎない。
        10. ゲーム内で不安定な特殊記号や絵文字はなるべく使わない。
        11. 相手がテストしているだけなら、軽く自然に一言返す。
        12. 相手が冗談っぽいなら少しノってよいが、真面目な話題では普通に返す。

        雰囲気:
        - MMOのフレンドっぽい
        - ゲーム内チャットの雑談っぽい
        - 短くて自然
        - ちょっと親しみがある

        あなたの仕事は一つだけです。
        会話内容に合わせて、ゲーム内にそのまま送れる自然な日本語の返事を一言だけ作ってください。
        """;

    public static List<PromptPreset> CreateList()
    {
        return
        [
            new()
            {
                Name = EnglishName,
                Prompt = EnglishPrompt,
            },
            new()
            {
                Name = GameAiName,
                Prompt = GameAiPrompt,
            },
            new()
            {
                Name = TraditionalChineseName,
                Prompt = TraditionalChinesePrompt,
            },
            new()
            {
                Name = JapaneseName,
                Prompt = JapanesePrompt,
            },
        ];
    }

    public static bool IsBuiltInName(string presetName)
    {
        return string.Equals(presetName, EnglishName, StringComparison.Ordinal) ||
               string.Equals(presetName, GameAiName, StringComparison.Ordinal) ||
               string.Equals(presetName, TraditionalChineseName, StringComparison.Ordinal) ||
               string.Equals(presetName, JapaneseName, StringComparison.Ordinal);
    }

    public static int GetBuiltInSortOrder(string presetName)
    {
        return presetName switch
        {
            EnglishName => 0,
            GameAiName => 1,
            TraditionalChineseName => 2,
            JapaneseName => 3,
            _ => int.MaxValue,
        };
    }
}
