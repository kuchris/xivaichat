using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivAiChat;

public sealed class LmStudioClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public async Task<string> CreateReplyAsync(
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        configuration.EnsureDefaults();
        var normalizedConfiguration = CloneWithNormalizedModel(configuration);

        return normalizedConfiguration.Provider switch
        {
            AiProvider.LmStudio => await this.CreateLmStudioReplyAsync(normalizedConfiguration, history, cancellationToken),
            AiProvider.Gemini => await this.CreateGeminiReplyAsync(normalizedConfiguration, history, cancellationToken),
            _ => await this.CreateOpenAiCompatibleReplyAsync(normalizedConfiguration, history, cancellationToken, isGemini: false),
        };
    }

    public async Task<string?> TryDetectLoadedLmStudioModelAsync(string configuredEndpoint, CancellationToken cancellationToken)
    {
        if (!TryBuildLmStudioModelsEndpoint(configuredEndpoint, out var modelsEndpoint))
        {
            return null;
        }

        try
        {
            using var response = await this.httpClient.GetAsync(modelsEndpoint, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return ExtractLoadedLmStudioModel(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Debug(ex, "Failed to detect loaded LM Studio model.");
            return null;
        }
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
    }

    private async Task<string> CreateLmStudioReplyAsync(
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        if (TryBuildLmStudioResponsesEndpoint(configuration.Endpoint, out var responsesEndpoint))
        {
            var responsesReply = await this.TryCreateLmStudioResponsesReplyAsync(
                responsesEndpoint,
                configuration,
                history,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(responsesReply))
            {
                return responsesReply;
            }
        }

        if (TryBuildLmStudioNativeEndpoint(configuration.Endpoint, out var nativeEndpoint))
        {
            var nativeReply = await this.TryCreateNativeLmStudioReplyAsync(
                nativeEndpoint,
                configuration,
                history,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(nativeReply))
            {
                return nativeReply;
            }
        }

        if (TryBuildLmStudioChatCompletionsEndpoint(configuration.Endpoint, out var chatCompletionsEndpoint))
        {
            var fallbackConfiguration = CloneConfiguration(configuration);
            fallbackConfiguration.Endpoint = chatCompletionsEndpoint;
            fallbackConfiguration.EnsureDefaults();
            return await this.CreateOpenAiCompatibleReplyAsync(fallbackConfiguration, history, cancellationToken, isGemini: false);
        }

        return await this.CreateOpenAiCompatibleReplyAsync(configuration, history, cancellationToken, isGemini: false);
    }

    private async Task<string> CreateGeminiReplyAsync(
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        try
        {
            if (IsGemmaOnGeminiModel(configuration.Model))
            {
                return await this.CreateGeminiNativeReplyAsync(configuration, history, cancellationToken);
            }

            return await this.CreateOpenAiCompatibleReplyAsync(configuration, history, cancellationToken, isGemini: true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable &&
                                             !string.Equals(configuration.Model, "gemini-2.5-flash", StringComparison.Ordinal))
        {
            var fallbackConfiguration = CloneWithModel(configuration, "gemini-2.5-flash");
            return await this.CreateOpenAiCompatibleReplyAsync(fallbackConfiguration, history, cancellationToken, isGemini: true);
        }
    }

    private async Task<string> CreateOpenAiCompatibleReplyAsync(
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken,
        bool isGemini)
    {
        var request = new OpenAiChatCompletionsRequest(
            configuration.Model,
            BuildOpenAiMessages(configuration, history),
            configuration.Temperature,
            configuration.MaxTokens,
            GetReasoningEffort(configuration, isGemini));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint)
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };

        if (!string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey.Trim());
        }

        using var response = await this.httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = ExtractOpenAiReplyText(body);
        if (string.IsNullOrWhiteSpace(content))
        {
            var snippet = body.Length <= 1200 ? body : $"{body[..1200]}...";
            throw new InvalidOperationException($"AI provider returned no reply text. Raw response: {snippet}");
        }

        return content;
    }

    private async Task<string> CreateGeminiNativeReplyAsync(
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new InvalidOperationException("Gemini API key is required for Gemma on Gemini API.");
        }

        var endpoint = BuildGeminiGenerateContentEndpoint(configuration.Model);
        var systemInstruction = BuildGeminiSystemInstruction(configuration.SystemPrompt);
        var request = new GeminiGenerateContentRequest(
            BuildGeminiContents(history),
            new GeminiContent(
                [new GeminiPart(systemInstruction)]),
            new GeminiGenerationConfig(
                configuration.Temperature,
                configuration.MaxTokens,
                null));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };

        httpRequest.Headers.Add("x-goog-api-key", configuration.ApiKey.Trim());

        using var response = await this.httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = ExtractGeminiGenerateContentText(body);
        if (string.IsNullOrWhiteSpace(content))
        {
            var snippet = body.Length <= 1200 ? body : $"{body[..1200]}...";
            throw new InvalidOperationException($"Gemini returned no reply text. Raw response: {snippet}");
        }

        return content;
    }

    private async Task<string?> TryCreateLmStudioResponsesReplyAsync(
        string endpoint,
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new OpenAiResponsesRequest(
                configuration.Model,
                BuildResponsesPrompt(configuration, history),
                configuration.Temperature,
                configuration.MaxTokens,
                GetLmStudioReasoning(configuration));

            using var response = await this.httpClient.PostAsJsonAsync(
                endpoint,
                request,
                SerializerOptions,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return ExtractResponsesReplyText(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Debug(ex, "LM Studio Responses endpoint failed, falling back.");
            return null;
        }
    }

    private async Task<string?> TryCreateNativeLmStudioReplyAsync(
        string endpoint,
        Configuration configuration,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new LmStudioChatRequest(
                configuration.Model,
                BuildNativePrompt(history),
                configuration.SystemPrompt,
                configuration.Temperature,
                configuration.MaxTokens,
                configuration.UseReasoning ? "on" : "off",
                false);

            using var response = await this.httpClient.PostAsJsonAsync(
                endpoint,
                request,
                SerializerOptions,
                cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return ExtractNativeReplyText(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Debug(ex, "LM Studio native endpoint failed, falling back.");
            return null;
        }
    }

    private static List<OpenAiChatMessage> BuildOpenAiMessages(Configuration configuration, IReadOnlyList<ChatTurn> history)
    {
        var messages = new List<OpenAiChatMessage>
        {
            new("system", configuration.SystemPrompt),
            new("system", "Return only the final in-game reply. Keep it to one short line. Do not explain your reasoning. Do not mention instructions, analysis, the user, or previous conversation. Do not prefix the reply with AI:, Assistant:, or a speaker name."),
        };

        foreach (var turn in history)
        {
            var prefix = string.IsNullOrWhiteSpace(turn.Name) ? turn.Content : $"{turn.Name}: {turn.Content}";
            messages.Add(new OpenAiChatMessage(turn.Role, prefix));
        }

        return messages;
    }

    private static string BuildResponsesPrompt(Configuration configuration, IReadOnlyList<ChatTurn> history)
    {
        var builder = new StringBuilder();
        builder.AppendLine(configuration.SystemPrompt);
        builder.AppendLine();
        builder.AppendLine("Return only the final in-game reply.");
        builder.AppendLine("Keep it to one short line.");
        builder.AppendLine("Do not explain your reasoning.");
        builder.AppendLine("Do not mention instructions, analysis, the user, or previous conversation.");
        builder.AppendLine("Do not prefix the reply with AI:, Assistant:, or a speaker name.");
        builder.AppendLine();
        builder.AppendLine("Recent chat:");

        foreach (var turn in history)
        {
            var prefix = string.IsNullOrWhiteSpace(turn.Name) ? turn.Content : $"{turn.Name}: {turn.Content}";
            builder.Append("- ");
            builder.Append(turn.Role);
            builder.Append(": ");
            builder.AppendLine(prefix);
        }

        builder.AppendLine();
        builder.Append("Reply now with one short line only.");
        return builder.ToString();
    }

    private static string BuildNativePrompt(IReadOnlyList<ChatTurn> history)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Return only the final in-game reply.");
        builder.AppendLine("Keep it to one short line.");
        builder.AppendLine("Do not explain your reasoning.");
        builder.AppendLine("Do not prefix the reply with AI:, Assistant:, or a speaker name.");
        builder.AppendLine();
        builder.AppendLine("Recent chat:");

        foreach (var turn in history)
        {
            var prefix = string.IsNullOrWhiteSpace(turn.Name) ? turn.Content : $"{turn.Name}: {turn.Content}";
            builder.Append("- ");
            builder.Append(turn.Role);
            builder.Append(": ");
            builder.AppendLine(prefix);
        }

        builder.AppendLine();
        builder.Append("Reply now with one short line only.");
        return builder.ToString();
    }

    private static List<GeminiContent> BuildGeminiContents(IReadOnlyList<ChatTurn> history)
    {
        var contents = new List<GeminiContent>();

        foreach (var turn in history)
        {
            var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "model"
                : "user";
            var prefix = string.IsNullOrWhiteSpace(turn.Name) ? turn.Content : $"{turn.Name}: {turn.Content}";
            contents.Add(new GeminiContent([new GeminiPart(prefix)], role));
        }

        return contents;
    }

    private static string BuildGeminiSystemInstruction(string systemPrompt)
    {
        var builder = new StringBuilder();
        builder.AppendLine(systemPrompt.Trim());
        builder.AppendLine();
        builder.AppendLine("Important:");
        builder.AppendLine("Only output the one final in-game reply.");
        builder.AppendLine("Do not show reasoning, analysis, steps, notes, bullets, or self-talk.");
        builder.AppendLine("Do not restate the rules or the user's request.");
        builder.AppendLine("Answer directly with one natural chat message only.");
        return builder.ToString().Trim();
    }

    private static string? GetReasoningEffort(Configuration configuration, bool isGemini)
    {
        if (!isGemini)
        {
            return null;
        }

        if (!configuration.UseReasoning)
        {
            return "none";
        }

        return string.IsNullOrWhiteSpace(configuration.ReasoningEffort)
            ? "low"
            : configuration.ReasoningEffort;
    }

    private static OpenAiReasoningConfig? GetLmStudioReasoning(Configuration configuration)
    {
        return new OpenAiReasoningConfig(configuration.UseReasoning ? "on" : "off");
    }

    private static string? ExtractOpenAiReplyText(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];

        if (firstChoice.TryGetProperty("message", out var message))
        {
            var fromMessage = ExtractContentValue(message);
            if (!string.IsNullOrWhiteSpace(fromMessage))
            {
                return fromMessage;
            }

            var fromReasoning = ExtractReasoningReply(message);
            if (!string.IsNullOrWhiteSpace(fromReasoning))
            {
                return fromReasoning;
            }
        }

        if (firstChoice.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString()?.Trim();
        }

        if (firstChoice.TryGetProperty("delta", out var delta))
        {
            var fromDelta = ExtractContentValue(delta);
            if (!string.IsNullOrWhiteSpace(fromDelta))
            {
                return fromDelta;
            }
        }

        return null;
    }

    private static string? ExtractResponsesReplyText(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            return NormalizeFinalReply(outputText.GetString());
        }

        if (root.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("type", out var itemType) &&
                    itemType.ValueKind == JsonValueKind.String &&
                    !string.Equals(itemType.GetString(), "message", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentItem in content.EnumerateArray())
                    {
                        if (contentItem.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (contentItem.TryGetProperty("type", out var contentType) &&
                            contentType.ValueKind == JsonValueKind.String &&
                            !string.Equals(contentType.GetString(), "output_text", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(contentType.GetString(), "text", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (contentItem.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                        {
                            var value = text.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                parts.Add(value);
                            }
                        }
                    }
                }
            }

            if (parts.Count > 0)
            {
                return NormalizeFinalReply(string.Join(" ", parts));
            }
        }

        return null;
    }

    private static string? ExtractNativeReplyText(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = contentElement.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? null : NormalizeFinalReply(string.Join(" ", parts));
    }

    private static string? ExtractGeminiGenerateContentText(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var firstCandidate = candidates[0];
        if (!firstCandidate.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var textParts = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    textParts.Add(value);
                }
            }
        }

        return textParts.Count == 0 ? null : NormalizeFinalReply(string.Join(" ", textParts));
    }

    private static string? NormalizeFinalReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var compact = text.Replace('\r', '\n').Trim();
        while (compact.Contains("\n\n", StringComparison.Ordinal))
        {
            compact = compact.Replace("\n\n", "\n", StringComparison.Ordinal);
        }

        if (!LooksLikeReasoningDump(compact))
        {
            return compact.Replace('\n', ' ').Trim();
        }

        var quoted = ExtractLastQuotedSegment(compact);
        if (!string.IsNullOrWhiteSpace(quoted))
        {
            return quoted;
        }

        var lines = compact.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim().TrimStart('*', '-', '•').Trim();
            if (string.IsNullOrWhiteSpace(line) || LooksLikeReasoningDump(line))
            {
                continue;
            }

            if (line.Length <= 120)
            {
                return line;
            }
        }

        var flattened = compact.Replace('\n', ' ').Trim();
        return flattened.Length <= 120 ? flattened : flattened[..120].Trim();
    }

    private static bool LooksLikeReasoningDump(string text)
    {
        var markers = new[]
        {
            "User input:",
            "Context:",
            "Role:",
            "Language:",
            "Rules:",
            "Style:",
            "Constraints:",
            "Goal:",
            "Vibe:",
            "I need to",
            "Let me",
            "First,",
            "A natural response",
            "most natural",
            "我需要",
            "首先",
            "角色：",
            "語言：",
            "規則：",
            "風格：",
            "限制：",
        };

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractLastQuotedSegment(string text)
    {
        var quotePairs = new (char Open, char Close)[]
        {
            ('"', '"'),
            ('\'', '\''),
            ('“', '”'),
            ('「', '」'),
            ('『', '』'),
        };

        foreach (var (open, close) in quotePairs)
        {
            var end = text.LastIndexOf(close);
            if (end <= 0)
            {
                continue;
            }

            var start = text.LastIndexOf(open, end - 1);
            if (start < 0 || end <= start + 1)
            {
                continue;
            }

            var value = text[(start + 1)..end].Trim();
            if (!string.IsNullOrWhiteSpace(value) &&
                !LooksLikeReasoningDump(value) &&
                value.Length <= 120)
            {
                return value;
            }
        }

        return null;
    }

    private static string? ExtractContentValue(JsonElement container)
    {
        if (!container.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Trim();
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString()?.Trim();
            }

            return null;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var directText = item.GetString();
                if (!string.IsNullOrWhiteSpace(directText))
                {
                    parts.Add(directText.Trim());
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var textValue = text.GetString();
                if (!string.IsNullOrWhiteSpace(textValue))
                {
                    parts.Add(textValue.Trim());
                }
            }
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string? ExtractReasoningReply(JsonElement container)
    {
        if (!container.TryGetProperty("reasoning_content", out var reasoning) ||
            reasoning.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = reasoning.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var patterns = new[]
        {
            "One short line:",
            "Final reply:",
            "Reply:",
            "Answer:",
            "一句話：",
            "一句话：",
            "簡短回覆：",
            "简短回复：",
            "回覆：",
            "回复：",
        };

        foreach (var pattern in patterns)
        {
            var index = trimmed.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var tail = trimmed[(index + pattern.Length)..].Trim();
            var quoted = ExtractQuotedText(tail);
            if (!string.IsNullOrWhiteSpace(quoted))
            {
                return quoted;
            }

            var firstSentence = ExtractFirstSentence(tail);
            if (!string.IsNullOrWhiteSpace(firstSentence))
            {
                return firstSentence;
            }
        }

        var finalQuoted = ExtractQuotedText(trimmed);
        if (!string.IsNullOrWhiteSpace(finalQuoted))
        {
            return finalQuoted;
        }

        return null;
    }

    private static string? ExtractQuotedText(string text)
    {
        var quotePairs = new (char Open, char Close)[]
        {
            ('"', '"'),
            ('\'', '\''),
            ('“', '”'),
            ('「', '」'),
            ('『', '』'),
        };

        foreach (var (open, close) in quotePairs)
        {
            var start = text.IndexOf(open);
            if (start < 0)
            {
                continue;
            }

            var end = text.IndexOf(close, start + 1);
            if (end <= start + 1)
            {
                continue;
            }

            var value = text[(start + 1)..end].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ExtractFirstSentence(string text)
    {
        var separators = new[] { '\n', '.', '!', '?', '。', '！', '？' };
        var first = text.Split(separators, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }

    private static bool TryBuildLmStudioResponsesEndpoint(string configuredEndpoint, out string responsesEndpoint)
    {
        responsesEndpoint = string.Empty;

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/v1/responses",
            Query = string.Empty,
        };

        responsesEndpoint = builder.Uri.ToString();
        return true;
    }

    private static bool TryBuildLmStudioChatCompletionsEndpoint(string configuredEndpoint, out string chatCompletionsEndpoint)
    {
        chatCompletionsEndpoint = string.Empty;

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/v1/chat/completions",
            Query = string.Empty,
        };

        chatCompletionsEndpoint = builder.Uri.ToString();
        return true;
    }

    private static bool TryBuildLmStudioModelsEndpoint(string configuredEndpoint, out string modelsEndpoint)
    {
        modelsEndpoint = string.Empty;

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/api/v1/models",
            Query = string.Empty,
        };

        modelsEndpoint = builder.Uri.ToString();
        return true;
    }

    private static bool TryBuildLmStudioNativeEndpoint(string configuredEndpoint, out string nativeEndpoint)
    {
        nativeEndpoint = string.Empty;

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/api/v1/chat",
            Query = string.Empty,
        };

        nativeEndpoint = builder.Uri.ToString();
        return true;
    }

    private static string? ExtractLoadedLmStudioModel(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!model.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "llm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!model.TryGetProperty("loaded_instances", out var loadedInstances) ||
                loadedInstances.ValueKind != JsonValueKind.Array ||
                loadedInstances.GetArrayLength() == 0)
            {
                continue;
            }

            var firstInstance = loadedInstances[0];
            if (firstInstance.ValueKind == JsonValueKind.Object &&
                firstInstance.TryGetProperty("id", out var instanceId) &&
                instanceId.ValueKind == JsonValueKind.String)
            {
                var loadedId = instanceId.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(loadedId))
                {
                    return loadedId;
                }
            }

            if (model.TryGetProperty("selected_variant", out var selectedVariant) &&
                selectedVariant.ValueKind == JsonValueKind.String)
            {
                var variant = selectedVariant.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(variant))
                {
                    return variant;
                }
            }

            if (model.TryGetProperty("key", out var keyElement) &&
                keyElement.ValueKind == JsonValueKind.String)
            {
                var key = keyElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static bool IsGemmaOnGeminiModel(string model)
    {
        return model.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase);
    }

    private static Configuration CloneWithNormalizedModel(Configuration configuration)
    {
        var clone = CloneConfiguration(configuration);
        clone.Model = NormalizeModelName(configuration.Provider, configuration.Model);
        clone.EnsureDefaults();
        return clone;
    }

    private static Configuration CloneWithModel(Configuration configuration, string model)
    {
        var clone = CloneConfiguration(configuration);
        clone.Model = model;
        clone.EnsureDefaults();
        return clone;
    }

    private static Configuration CloneConfiguration(Configuration configuration)
    {
        return new Configuration
        {
            Version = configuration.Version,
            Enabled = configuration.Enabled,
            SendReplies = configuration.SendReplies,
            CwlsSlot = configuration.CwlsSlot,
            WatchedChannelIds = configuration.WatchedChannelIds.ToList(),
            RequireMention = configuration.RequireMention,
            TriggerAlias = configuration.TriggerAlias,
            Provider = configuration.Provider,
            Endpoint = configuration.Endpoint,
            Model = NormalizeModelName(configuration.Provider, configuration.Model),
            ApiKey = configuration.ApiKey,
            SystemPrompt = configuration.SystemPrompt,
            ActivePromptPreset = configuration.ActivePromptPreset,
            PromptPresets = configuration.PromptPresets
                .Select(preset => new PromptPreset
                {
                    Name = preset.Name,
                    Prompt = preset.Prompt,
                })
                .ToList(),
            Temperature = configuration.Temperature,
            UseReasoning = configuration.UseReasoning,
            ReasoningEffort = configuration.ReasoningEffort,
            MaxTokens = configuration.MaxTokens,
            CooldownSeconds = configuration.CooldownSeconds,
            MaxHistoryMessages = configuration.MaxHistoryMessages,
        };
    }

    private static string NormalizeModelName(string provider, string model)
    {
        if (!string.Equals(provider, AiProvider.Gemini, StringComparison.Ordinal))
        {
            return model;
        }

        return model.Trim() switch
        {
            "gemini-3.1-flash-lite" => "gemini-3.1-flash-lite-preview",
            _ => model,
        };
    }

    private static string BuildGeminiGenerateContentEndpoint(string model)
    {
        return $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
    }

    public sealed record ChatTurn(string Role, string Name, string Content);

    private sealed record OpenAiChatCompletionsRequest(
        string Model,
        IReadOnlyList<OpenAiChatMessage> Messages,
        float Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort);

    private sealed record OpenAiChatMessage(string Role, string Content);

    private sealed record OpenAiResponsesRequest(
        string Model,
        string Input,
        float Temperature,
        [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
        OpenAiReasoningConfig? Reasoning);

    private sealed record OpenAiReasoningConfig(string Effort);

    private sealed record LmStudioChatRequest(
        string Model,
        string Input,
        [property: JsonPropertyName("system_prompt")] string SystemPrompt,
        float Temperature,
        [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
        string Reasoning,
        bool Store);

    private sealed record GeminiGenerateContentRequest(
        [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents,
        [property: JsonPropertyName("system_instruction")] GeminiContent? SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig? GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts,
        [property: JsonPropertyName("role")] string? Role = null);

    private sealed record GeminiPart([property: JsonPropertyName("text")] string Text);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
        [property: JsonPropertyName("thinkingConfig")] GeminiThinkingConfig? ThinkingConfig);

    private sealed record GeminiThinkingConfig(
        [property: JsonPropertyName("thinkingLevel")] string ThinkingLevel);
}
