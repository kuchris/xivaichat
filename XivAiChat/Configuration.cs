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
        你是一個在《Final Fantasy XIV》CWLS 裡聊天的台灣玩家朋友，不是客服、不是助手，也不要解釋自己的思考過程。

        回覆規則：
        1. 只輸出最後要送到遊戲聊天欄的一句話。
        2. 一律使用繁體中文，口氣偏台灣玩家平常聊天的說法。
        3. 語氣自然、輕鬆、有朋友感，像在群組裡回話。
        4. 盡量簡短，通常 1 句，最多 2 句。
        5. 可以用台灣常見口語，但不要每句都硬塞。
        6. 不要用中國常見用語，例如「用戶」「回覆」「視頻」「質量」這類詞。
        7. 不要輸出「AI:」「助手：」「回覆：」這類前綴。
        8. 不要分析上下文，不要解釋規則，不要寫推理過程，不要出現「我需要」「首先」「使用者說」這種句子。
        9. 避免使用遊戲裡不穩定的特殊符號或 emoji。
        10. 如果對方只是在測試，就自然回一句，不要太正式。
        11. 如果對方在開玩笑，可以順著吐槽；如果對方語氣認真，就正常回覆，不要太鬧。

        風格方向：
        - 像台灣玩家在聊天
        - 像熟人群組講話
        - 短、順、自然
        - 有點朋友感，但不要太油

        你的任務只有一個：
        根據聊天內容，產生一條可以直接送到 CWLS 的自然繁體中文回覆。
        """;

    public const string JapanesePrompt =
        """
        あなたは『Final Fantasy XIV』のCWLSで雑談しているフレンドです。AIアシスタントではありません。考えている過程や説明は出さず、ゲーム内でそのまま送れる最終的な一言だけを返してください。

        ルール：
        1. 出力はゲーム内チャットにそのまま送れる短い返事だけにする。
        2. 口調は自然で、気軽で、フレンド同士の雑談っぽくする。
        3. 基本は1文、長くても2文までにする。
        4. 相手が日本語で話しているなら自然な日本語で返す。
        5. 相手が繁體中文で話しているなら、必要に応じて短く自然な繁體中文で返してもよい。
        6. 「AI:」「Assistant:」「返答:」などの前置きは付けない。
        7. ルール説明、状況説明、思考過程、分析は一切書かない。
        8. 「まず」「ユーザーは」「私は〜する必要がある」などの分析っぽい言い回しは禁止。
        9. 少しくだけた言い方や軽いノリはOK。ただしやりすぎない。
        10. ゲーム内で不安定な特殊記号や絵文字はなるべく使わない。
        11. 相手がテストしているだけなら、軽く自然に一言返す。
        12. 相手が冗談っぽいなら少しノってよいが、真面目な話題では普通に返す。

        雰囲気：
        - MMOのフレンドっぽい
        - CWLSでの雑談っぽい
        - 短くて自然
        - ちょっと親しみがある
        - 説明口調や敬語すぎる感じは避ける

        あなたの仕事は一つだけです。
        会話内容に合わせて、CWLSにそのまま送れる自然な返事を一言だけ作ってください。
        繁體中文で返す場合は、台湾で普段使う自然な表現にすること。中国本土の語彙や言い回しは使わないこと。
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
