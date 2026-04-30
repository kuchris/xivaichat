using Dalamud.Configuration;
using Dalamud.Plugin;

namespace XivAiChat;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 6;

    public bool Enabled { get; set; }

    public bool SendReplies { get; set; }

    public bool RequireApprovalBeforeReply { get; set; }

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

    public int MaxTokens { get; set; } = 4000;

    public int ReplyDelayMilliseconds { get; set; } = 1500;

    public bool UseExaSearch { get; set; }

    public int CooldownSeconds { get; set; } = 30;

    public int ReplyAfterMessageCount { get; set; } = 1;

    public int MaxHistoryMessages { get; set; } = 8;

    public float DraftPopupPositionX { get; set; } = -1f;

    public float DraftPopupPositionY { get; set; } = -1f;

    public bool ShowDraftPopup { get; set; }

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
        SyncBuiltInPresetContent(this.PromptPresets);
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
        }

        if (this.Version < 6)
        {
            this.MaxTokens = Math.Max(this.MaxTokens, 4000);
            if (this.ReplyDelayMilliseconds <= 0)
            {
                this.ReplyDelayMilliseconds = 1500;
            }
        }

        var activePrompt = this.GetActivePrompt();
        if (activePrompt is not null &&
            BuiltInPromptPresets.IsBuiltInName(this.ActivePromptPreset))
        {
            this.SystemPrompt = activePrompt.Prompt;
        }
        else if (string.IsNullOrWhiteSpace(this.SystemPrompt))
        {
            this.SystemPrompt = activePrompt?.Prompt ?? BuiltInPromptPresets.EnglishPrompt;
        }

        this.MaxTokens = Math.Clamp(this.MaxTokens, 64, 4000);
        this.ReplyDelayMilliseconds = Math.Clamp(this.ReplyDelayMilliseconds, 0, 30000);
        this.ReplyAfterMessageCount = Math.Clamp(this.ReplyAfterMessageCount, 1, 20);
        this.Version = 6;
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

    public bool SaveCurrentPromptToActivePreset()
    {
        var preset = this.GetActivePrompt();
        if (preset is null)
        {
            return false;
        }

        if (BuiltInPromptPresets.IsBuiltInName(preset.Name))
        {
            return false;
        }

        preset.Prompt = this.SystemPrompt;
        return true;
    }

    public bool UpsertPromptPreset(string presetName, string prompt)
    {
        var name = CanonicalizePresetName(presetName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (BuiltInPromptPresets.IsBuiltInName(name))
        {
            return false;
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
        return true;
    }

    public bool DeletePromptPreset(string presetName)
    {
        if (this.PromptPresets.Count <= 1)
        {
            return false;
        }

        var canonicalName = CanonicalizePresetName(presetName);
        if (BuiltInPromptPresets.IsBuiltInName(canonicalName))
        {
            return false;
        }

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

    private static void SyncBuiltInPresetContent(List<PromptPreset> presets)
    {
        foreach (var preset in presets)
        {
            var builtInPrompt = GetBuiltInPrompt(preset.Name);
            if (builtInPrompt is null)
            {
                continue;
            }

            preset.Prompt = builtInPrompt;
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
    public const string NvidiaNim = "NVIDIA NIM";
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
        You are a Final Fantasy XIV player chatting in game. You are not an assistant, and you never explain your reasoning.

        Rules:
        1. Output only the final line that should be sent in game.
        2. Reply in English only.
        3. Keep it natural, short, and casual, like a real player chatting with friends.
        4. Usually reply in 1 sentence, at most 2 short sentences.
        5. Do not add prefixes like AI:, Assistant:, Reply:, or speaker names.
        6. Do not explain rules, summarize context, or describe your thinking.
        7. Do not use analysis phrases like "I need to", "first", "the user said", or similar meta commentary.
        8. Do not use emoji characters. Simple text-style emoticons or kaomoji are okay.
        9. If someone is clearly just testing, answer with a short and natural test-like reply.

        Style:
        - Like an MMO friend
        - Like in-game small talk
        - Short, smooth, and natural
        - Friendly, but not overly formal
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
        - Do not use emoji characters. Simple text-style emoticons or kaomoji are okay.
        - Use Traditional Chinese only, never Simplified Chinese.

        Style:
        - friendly
        - casual
        """;

    public const string TraditionalChinesePrompt =
        """
        你是在《Final Fantasy XIV》遊戲內聊天的玩家，不是助手，也不要解釋自己的思考過程。

        規則：
        1. 只輸出最後要送出的那一句話。
        2. 一律只用繁體中文回覆。
        3. 語氣自然、輕鬆，像真人玩家在聊天。
        4. 盡量簡短，通常 1 句，最多 2 句。
        5. 不要加上 AI:、助手：、回覆：或角色名稱前綴。
        6. 不要解釋規則、摘要上下文或寫出思考過程。
        7. 不要用「我需要」、「首先」、「使用者說」這類分析語氣。
        8. 不要使用 emoji 字元，但可以使用簡單的文字表情或顏文字。
        9. 如果對方只是在測試，就自然簡短回一句。

        風格：
        - 像 MMO 朋友
        - 像遊戲內群聊
        - 短、順、自然
        - 友善但不油
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
        9. emoji の文字は使わない。シンプルな顔文字や文字の雰囲気表現はOK。
        10. 相手がテストしているだけなら、軽く自然に一言返す。

        雰囲気:
        - MMOのフレンドっぽい
        - ゲーム内チャットの雑談っぽい
        - 短くて自然
        - ちょっと親しみがある
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
