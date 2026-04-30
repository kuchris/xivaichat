using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace XivAiChat;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] ProviderOptions =
    {
        AiProvider.LmStudio,
        AiProvider.OpenAiCompatible,
        AiProvider.Gemini,
        AiProvider.NvidiaNim,
    };

    private static readonly string[] ReasoningEffortOptions =
    {
        "none",
        "low",
        "medium",
        "high",
    };

    private readonly Plugin plugin;
    private string endpointBuffer;
    private string modelBuffer;
    private string apiKeyBuffer;
    private string aliasBuffer;
    private string systemPromptBuffer;
    private string testMessageBuffer = string.Empty;
    private string presetNameBuffer = string.Empty;
    private bool isDetectingLmStudioModel;

    public ConfigWindow(Plugin plugin)
        : base("XIV AI Chat###XivAiChatConfig")
    {
        this.plugin = plugin;
        WindowCompat.ApplySizeConstraints(this, new Vector2(700, 560), new Vector2(1400, 1400));

        this.endpointBuffer = plugin.Configuration.Endpoint;
        this.modelBuffer = plugin.Configuration.Model;
        this.apiKeyBuffer = plugin.Configuration.ApiKey;
        this.aliasBuffer = plugin.Configuration.TriggerAlias;
        this.systemPromptBuffer = plugin.Configuration.SystemPrompt;
        this.presetNameBuffer = plugin.Configuration.ActivePromptPreset;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var configuration = this.plugin.Configuration;
        configuration.EnsureDefaults();

        ImGui.TextWrapped("Switch between local and API models, save multiple prompt presets, choose the chat channels you want to watch, and reply back in the same channel.");
        ImGui.Separator();

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            configuration.Enabled = enabled;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        var sendReplies = configuration.SendReplies;
        if (ImGui.Checkbox("Auto-send replies", ref sendReplies))
        {
            configuration.SendReplies = sendReplies;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Leave this off first so the plugin only prints drafts locally.");

        var requireApproval = configuration.RequireApprovalBeforeReply;
        if (ImGui.Checkbox("Require OK before replying", ref requireApproval))
        {
            configuration.RequireApprovalBeforeReply = requireApproval;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("When enabled, the plugin prepares a draft but does not print or send it until you approve it below.");

        var requireMention = configuration.RequireMention;
        if (ImGui.Checkbox("Require mention or alias", ref requireMention))
        {
            configuration.RequireMention = requireMention;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        var showDraftPopup = this.plugin.Configuration.ShowDraftPopup;
        if (ImGui.Checkbox("Show Reply Drafts", ref showDraftPopup))
        {
            this.plugin.Configuration.ShowDraftPopup = showDraftPopup;
            this.plugin.SaveConfiguration();

            if (showDraftPopup)
            {
                this.plugin.OpenDraftPopup();
            }
        }

        ImGui.TextDisabled(this.plugin.GetStatusSummary());
        ImGui.Spacing();

        DrawProviderSection(configuration);
        ImGui.Spacing();
        DrawExaSection(configuration);
        ImGui.Spacing();
        DrawChannelSection(configuration);
        ImGui.Spacing();
        DrawBehaviorSection(configuration);
        ImGui.Spacing();
        DrawPromptSection(configuration);
        ImGui.Spacing();
        DrawPendingRepliesSection();
        ImGui.Spacing();
        DrawQuickTestSection();
        ImGui.Spacing();
        DrawDebugSection();
    }

    private void DrawProviderSection(Configuration configuration)
    {
        ImGui.Text("Provider");

        var providerIndex = Array.IndexOf(ProviderOptions, configuration.Provider);
        providerIndex = providerIndex < 0 ? 0 : providerIndex;
        if (ImGui.Combo("AI provider", ref providerIndex, ProviderOptions, ProviderOptions.Length))
        {
            configuration.Provider = ProviderOptions[providerIndex];
            ApplyProviderDefaults(configuration);
            SyncBuffersFromConfiguration(configuration);
            this.plugin.SaveConfiguration();

            if (string.Equals(configuration.Provider, AiProvider.LmStudio, StringComparison.Ordinal))
            {
                _ = this.DetectLmStudioModelAsync();
            }
        }

        if (ImGui.InputText(GetEndpointLabel(configuration), ref this.endpointBuffer, 256))
        {
            configuration.Endpoint = this.endpointBuffer.Trim();
            this.plugin.SaveConfiguration();
        }

        if (ImGui.InputText("Model name", ref this.modelBuffer, 128))
        {
            configuration.Model = this.modelBuffer.Trim();
            this.plugin.SaveConfiguration();
        }

        if (!string.Equals(configuration.Provider, AiProvider.LmStudio, StringComparison.Ordinal))
        {
            if (ImGui.InputText("API key", ref this.apiKeyBuffer, 256, ImGuiInputTextFlags.Password))
            {
                configuration.ApiKey = this.apiKeyBuffer.Trim();
                this.plugin.SaveConfiguration();
            }

            ImGuiComponents.HelpMarker(GetApiKeyHelpText(configuration));
        }
        else
        {
            if (ImGui.Button(this.isDetectingLmStudioModel ? "Detecting..." : "Detect loaded model"))
            {
                _ = this.DetectLmStudioModelAsync();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker("Reads LM Studio's /api/v1/models list and uses the first loaded LLM so you do not have to type the model name again.");
        }

        if (string.Equals(configuration.Provider, AiProvider.Gemini, StringComparison.Ordinal))
        {
            var effortIndex = Array.IndexOf(ReasoningEffortOptions, configuration.ReasoningEffort);
            effortIndex = effortIndex < 0 ? 1 : effortIndex;
            if (ImGui.Combo("Gemini reasoning", ref effortIndex, ReasoningEffortOptions, ReasoningEffortOptions.Length))
            {
                configuration.ReasoningEffort = ReasoningEffortOptions[effortIndex];
                this.plugin.SaveConfiguration();
            }

            ImGuiComponents.HelpMarker("Gemini chat models use reasoning_effort here. Gemma on Gemini API is handled automatically when the model name starts with gemma-.");

            ImGui.Text("Quick model picks");
            if (ImGui.Button("gemini-2.5-flash-lite"))
            {
                configuration.Model = "gemini-2.5-flash-lite";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            if (ImGui.Button("gemini-3.1-flash-lite"))
            {
                configuration.Model = "gemini-3.1-flash-lite";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            if (ImGui.Button("gemma-4-31b-it"))
            {
                configuration.Model = "gemma-4-31b-it";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.TextWrapped("`gemini-*` models use Google's OpenAI-compatible chat endpoint. `gemma-*` models are auto-routed to Google's native Gemini API path. `gemini-3.1-flash-lite` is sent as `gemini-3.1-flash-lite-preview` for compatibility.");
        }
        else if (string.Equals(configuration.Provider, AiProvider.NvidiaNim, StringComparison.Ordinal))
        {
            ImGui.Text("Quick model picks");
            if (ImGui.Button("z-ai/glm4.7"))
            {
                configuration.Model = "z-ai/glm4.7";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            if (ImGui.Button("z-ai/glm5"))
            {
                configuration.Model = "z-ai/glm5";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            if (ImGui.Button("moonshotai/kimi-k2.5"))
            {
                configuration.Model = "moonshotai/kimi-k2.5";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.SameLine();
            if (ImGui.Button("minimaxai/minimax-m2.5"))
            {
                configuration.Model = "minimaxai/minimax-m2.5";
                this.modelBuffer = configuration.Model;
                this.plugin.SaveConfiguration();
            }

            ImGui.TextWrapped("NIM uses NVIDIA's hosted OpenAI-compatible chat endpoint. You can enter either https://integrate.api.nvidia.com/v1 or the full /v1/chat/completions URL. Model IDs may be entered with or without the nvidia_nim/ routing prefix. z-ai/glm-4.7 is normalized to NVIDIA's chat API id z-ai/glm4.7.");
        }
    }

    private void DrawExaSection(Configuration configuration)
    {
        ImGui.Text("Web Search (Exa)");

        var useExaSearch = configuration.UseExaSearch;
        if (ImGui.Checkbox("Enable Exa web search", ref useExaSearch))
        {
            configuration.UseExaSearch = useExaSearch;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("When enabled, the AI searches the web via Exa before each reply. Uses the free Exa MCP server — no API key needed.");
    }

    private void DrawBehaviorSection(Configuration configuration)
    {
        ImGui.Text("Behavior");

        if (ImGui.InputText("Trigger alias", ref this.aliasBuffer, 64))
        {
            configuration.TriggerAlias = this.aliasBuffer.Trim();
            this.plugin.SaveConfiguration();
        }

        var temperature = configuration.Temperature;
        if (ImGui.SliderFloat("Temperature", ref temperature, 0.0f, 2.0f, "%.2f"))
        {
            configuration.Temperature = temperature;
            this.plugin.SaveConfiguration();
        }

        var useReasoning = configuration.UseReasoning;
        if (ImGui.Checkbox("LM Studio native reasoning", ref useReasoning))
        {
            configuration.UseReasoning = useReasoning;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Used for the local LM Studio native endpoint. API providers use their own settings.");

        var maxTokens = configuration.MaxTokens;
        if (ImGui.SliderInt("Max tokens", ref maxTokens, 64, 4000))
        {
            configuration.MaxTokens = maxTokens;
            this.plugin.SaveConfiguration();
        }

        var replyDelay = configuration.ReplyDelayMilliseconds;
        if (ImGui.SliderInt("Reply delay (ms)", ref replyDelay, 0, 30000))
        {
            configuration.ReplyDelayMilliseconds = replyDelay;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Adds a small wait before automatic replies are printed or sent, so the bot feels less instant.");

        var cooldown = configuration.CooldownSeconds;
        if (ImGui.SliderInt("Cooldown seconds", ref cooldown, 0, 600))
        {
            configuration.CooldownSeconds = cooldown;
            this.plugin.SaveConfiguration();
        }

        var replyAfterCount = configuration.ReplyAfterMessageCount;
        if (ImGui.SliderInt("Reply after chats", ref replyAfterCount, 1, 20))
        {
            configuration.ReplyAfterMessageCount = replyAfterCount;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("How many accepted chat messages in the same watched channel should accumulate before the AI generates one reply.");

        var historyCount = configuration.MaxHistoryMessages;
        if (ImGui.SliderInt("History lines", ref historyCount, 1, 20))
        {
            configuration.MaxHistoryMessages = historyCount;
            this.plugin.SaveConfiguration();
        }
    }

    private void DrawChannelSection(Configuration configuration)
    {
        ImGui.Text("Channels");
        ImGui.TextWrapped("Enable the chat channels you want the AI to listen to. When auto-send is on, it replies back into the same channel that triggered it.");

        foreach (var group in ChatChannelRegistry.Groups)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(group);

            var index = 0;
            foreach (var channel in ChatChannelRegistry.GetByGroup(group))
            {
                if (index > 0)
                {
                    ImGui.SameLine();
                }

                var enabled = configuration.IsChannelEnabled(channel.Id);
                if (ImGui.Checkbox($"{channel.Label}##{channel.Id}", ref enabled))
                {
                    configuration.SetChannelEnabled(channel.Id, enabled);
                    this.plugin.SaveConfiguration();
                }

                index++;
                if (index == 3)
                {
                    index = 0;
                }
            }
        }
    }

    private void DrawPromptSection(Configuration configuration)
    {
        ImGui.Text("Prompt Presets");

        ImGui.Text("Quick language switch");
        if (ImGui.Button("English"))
        {
            configuration.SetActivePrompt(BuiltInPromptPresets.EnglishName);
            this.systemPromptBuffer = configuration.SystemPrompt;
            this.presetNameBuffer = configuration.ActivePromptPreset;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        if (ImGui.Button("繁中"))
        {
            configuration.SetActivePrompt(BuiltInPromptPresets.TraditionalChineseName);
            this.systemPromptBuffer = configuration.SystemPrompt;
            this.presetNameBuffer = configuration.ActivePromptPreset;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        if (ImGui.Button("日本語"))
        {
            configuration.SetActivePrompt(BuiltInPromptPresets.JapaneseName);
            this.systemPromptBuffer = configuration.SystemPrompt;
            this.presetNameBuffer = configuration.ActivePromptPreset;
            this.plugin.SaveConfiguration();
        }

        var activePreset = configuration.GetActivePrompt();
        var activeBuiltIn = activePreset is not null && BuiltInPromptPresets.IsBuiltInName(activePreset.Name);
        var presetNames = configuration.PromptPresets.Select(preset => preset.Name).ToArray();
        var activeIndex = Array.FindIndex(
            presetNames,
            name => string.Equals(name, configuration.ActivePromptPreset, StringComparison.Ordinal));
        activeIndex = activeIndex < 0 ? 0 : activeIndex;

        if (ImGui.Combo("Active preset", ref activeIndex, presetNames, presetNames.Length))
        {
            configuration.SetActivePrompt(presetNames[activeIndex]);
            this.systemPromptBuffer = configuration.SystemPrompt;
            this.presetNameBuffer = configuration.ActivePromptPreset;
            this.plugin.SaveConfiguration();
        }

        if (ImGui.Button("Load Preset"))
        {
            configuration.SetActivePrompt(configuration.ActivePromptPreset);
            this.systemPromptBuffer = configuration.SystemPrompt;
            this.plugin.SaveConfiguration();
        }

        ImGui.SameLine();
        if (activeBuiltIn)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Save To Preset"))
        {
            configuration.SystemPrompt = this.systemPromptBuffer.Trim();
            if (configuration.SaveCurrentPromptToActivePreset())
            {
                this.plugin.SaveConfiguration();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete Preset"))
        {
            if (configuration.DeletePromptPreset(configuration.ActivePromptPreset))
            {
                this.systemPromptBuffer = configuration.SystemPrompt;
                this.presetNameBuffer = configuration.ActivePromptPreset;
                this.plugin.SaveConfiguration();
            }
        }

        if (activeBuiltIn)
        {
            ImGui.EndDisabled();
        }

        if (activeBuiltIn && activePreset is not null)
        {
            this.systemPromptBuffer = activePreset.Prompt;
            ImGui.TextWrapped("Built-in presets are read-only. Enter a new preset name below if you want to clone and edit this prompt.");
        }

        if (ImGui.InputTextWithHint("Preset name", "New preset name", ref this.presetNameBuffer, 64))
        {
        }

        ImGui.SameLine();
        if (ImGui.Button("Create / Overwrite"))
        {
            configuration.SystemPrompt = this.systemPromptBuffer.Trim();
            if (configuration.UpsertPromptPreset(this.presetNameBuffer, configuration.SystemPrompt))
            {
                this.plugin.SaveConfiguration();
            }
        }

        ImGui.Text("System prompt");
        var promptFlags = activeBuiltIn
            ? ImGuiInputTextFlags.ReadOnly
            : ImGuiInputTextFlags.None;
        if (ImGui.InputTextMultiline("##systemPrompt", ref this.systemPromptBuffer, 12000, new Vector2(-1, 180), promptFlags))
        {
            if (!activeBuiltIn)
            {
                configuration.SystemPrompt = this.systemPromptBuffer;
                this.plugin.SaveConfiguration();
            }
        }
    }

    private void DrawQuickTestSection()
    {
        ImGui.Separator();
        ImGui.Text("Quick test");

        ImGui.InputTextWithHint("##testMessage", "Ask something to send to the selected provider", ref this.testMessageBuffer, 256);
        if (ImGui.Button("Run Test"))
        {
            this.plugin.RunManualPrompt(this.testMessageBuffer);
        }

        ImGui.SameLine();
        if (ImGui.Button("Print Status"))
        {
            Plugin.ChatGui.Print(this.plugin.GetStatusSummary(), "XIV AI Chat");
        }

        ImGui.SameLine();
        if (ImGui.Button("Read Situation"))
        {
            this.plugin.ReadSituation();
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
        {
            this.IsOpen = false;
        }
    }

    private void DrawPendingRepliesSection()
    {
        ImGui.Separator();
        ImGui.Text("Pending Replies");

        var pendingReplies = this.plugin.GetPendingReplies();
        if (pendingReplies.Count == 0)
        {
            ImGui.TextDisabled("No drafts waiting for approval.");
            return;
        }

        ImGui.TextWrapped("AI drafts stay here until you press OK. They will use the current send mode when approved.");

        foreach (var pendingReply in pendingReplies.OrderByDescending(reply => reply.CreatedAtUtc))
        {
            ImGui.PushID(pendingReply.Id.ToString());
            ImGui.Separator();
            ImGui.TextDisabled($"{pendingReply.ChannelLabel} • {pendingReply.CreatedAtUtc.ToLocalTime():HH:mm:ss}");
            ImGui.TextWrapped(pendingReply.ReplyText);

            if (ImGui.Button("OK"))
            {
                this.plugin.ApprovePendingReply(pendingReply.Id);
            }

            ImGui.SameLine();
            if (ImGui.Button("Dismiss"))
            {
                this.plugin.DismissPendingReply(pendingReply.Id);
            }

            ImGui.PopID();
        }
    }

    private void DrawDebugSection()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Debug");
        ImGui.TextWrapped($"Last decision: {this.plugin.LastDecision}");
        ImGui.TextWrapped($"Last source: {this.plugin.LastSeenSource}");
        ImGui.TextWrapped($"Last type: {this.plugin.LastSeenType}");
        ImGui.TextWrapped($"Last sender: {this.plugin.LastSeenSender}");
        ImGui.TextWrapped($"Last message: {this.plugin.LastSeenMessage}");
    }

    private static string GetEndpointLabel(Configuration configuration)
    {
        return configuration.Provider switch
        {
            AiProvider.LmStudio => "LM Studio endpoint",
            AiProvider.Gemini => "Gemini endpoint",
            AiProvider.NvidiaNim => "NVIDIA NIM endpoint",
            _ => "API endpoint",
        };
    }

    private static void ApplyProviderDefaults(Configuration configuration)
    {
        switch (configuration.Provider)
        {
            case AiProvider.LmStudio:
                configuration.Endpoint = "http://127.0.0.1:1234/api/v1/chat";
                configuration.Model = string.IsNullOrWhiteSpace(configuration.Model) ? "local-model" : configuration.Model;
                break;
            case AiProvider.Gemini:
                configuration.Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                configuration.Model = "gemini-2.5-flash-lite";
                configuration.ReasoningEffort = string.IsNullOrWhiteSpace(configuration.ReasoningEffort) ? "low" : configuration.ReasoningEffort;
                configuration.MaxTokens = Math.Max(configuration.MaxTokens, 4000);
                break;
            case AiProvider.NvidiaNim:
                configuration.Endpoint = "https://integrate.api.nvidia.com/v1/chat/completions";
                configuration.Model = string.IsNullOrWhiteSpace(configuration.Model)
                    ? "z-ai/glm4.7"
                    : configuration.Model;
                break;
            case AiProvider.OpenAiCompatible:
                configuration.Endpoint =
                    string.Equals(configuration.Endpoint, "http://127.0.0.1:1234/api/v1/chat", StringComparison.Ordinal) ||
                    string.Equals(configuration.Endpoint, "http://127.0.0.1:1234/v1/chat/completions", StringComparison.Ordinal) ||
                    string.Equals(configuration.Endpoint, "http://127.0.0.1:1234/v1/responses", StringComparison.Ordinal)
                    ? "https://api.openai.com/v1/chat/completions"
                    : configuration.Endpoint;
                break;
        }
    }

    private static string GetApiKeyHelpText(Configuration configuration)
    {
        return configuration.Provider switch
        {
            AiProvider.Gemini => "For Gemini, use the API key from Google AI Studio. Gemini models use the OpenAI-compatible endpoint, while Gemma models on Gemini API are auto-routed to Google's native generateContent endpoint.",
            AiProvider.NvidiaNim => "For NVIDIA NIM, use your NVIDIA API key. The hosted NIM chat endpoint is OpenAI-compatible; this plugin normalizes the base /v1 URL and strips the nvidia_nim/ model routing prefix.",
            _ => "Use the API key required by your selected OpenAI-compatible provider.",
        };
    }

    private async Task DetectLmStudioModelAsync()
    {
        if (this.isDetectingLmStudioModel)
        {
            return;
        }

        this.isDetectingLmStudioModel = true;
        try
        {
            var model = await this.plugin.TryDetectLmStudioModelAsync();
            if (!string.IsNullOrWhiteSpace(model))
            {
                this.modelBuffer = model;
            }
        }
        finally
        {
            this.isDetectingLmStudioModel = false;
        }
    }

    private void SyncBuffersFromConfiguration(Configuration configuration)
    {
        this.endpointBuffer = configuration.Endpoint;
        this.modelBuffer = configuration.Model;
        this.apiKeyBuffer = configuration.ApiKey;
        this.aliasBuffer = configuration.TriggerAlias;
        this.systemPromptBuffer = configuration.SystemPrompt;
        this.presetNameBuffer = configuration.ActivePromptPreset;
    }
}
