using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivAiChat;

// Calls the Exa MCP server (https://mcp.exa.ai/mcp) using JSON-RPC over HTTP.
// No API key required — the free MCP endpoint handles auth internally.
public sealed class ExaSearchClient : IDisposable
{
    private const string McpEndpoint = "https://mcp.exa.ai/mcp";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task<string?> SearchAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = await this.InitializeSessionAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Plugin.Log.Debug("Exa MCP: failed to get session ID.");
                return null;
            }

            return await this.CallSearchToolAsync(sessionId, query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Debug(ex, "Exa MCP search failed.");
            return null;
        }
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
    }

    private async Task<string?> InitializeSessionAsync(CancellationToken cancellationToken)
    {
        var initRequest = new McpRequest(
            "2.0",
            1,
            "initialize",
            new McpInitializeParams(
                "2024-11-05",
                new McpClientInfo("XivAiChat", "1.0"),
                new McpCapabilities()));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = JsonContent.Create(initRequest, options: SerializerOptions),
        };
        httpRequest.Headers.Add("Accept", "application/json, text/event-stream");

        using var response = await this.httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log.Debug("Exa MCP initialize returned HTTP {StatusCode}.", response.StatusCode);
            return null;
        }

        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
        {
            return values.FirstOrDefault();
        }

        // Some servers embed session in response body
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("sessionId", out var sid) &&
            sid.ValueKind == JsonValueKind.String)
        {
            return sid.GetString();
        }

        // Server may be stateless — return a placeholder so the call proceeds
        return "stateless";
    }

    private async Task<string?> CallSearchToolAsync(string sessionId, string query, CancellationToken cancellationToken)
    {
        var callRequest = new McpRequest(
            "2.0",
            2,
            "tools/call",
            new McpToolCallParams(
                "web_search_exa",
                new ExaSearchArguments(query, 3)));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, McpEndpoint)
        {
            Content = JsonContent.Create(callRequest, options: SerializerOptions),
        };
        httpRequest.Headers.Add("Accept", "application/json, text/event-stream");

        if (!string.Equals(sessionId, "stateless", StringComparison.Ordinal))
        {
            httpRequest.Headers.Add("Mcp-Session-Id", sessionId);
        }

        using var response = await this.httpClient.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Plugin.Log.Debug("Exa MCP tools/call returned HTTP {StatusCode}.", response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return ExtractSearchText(body);
    }

    private static string? ExtractSearchText(string body)
    {
        // Handle SSE stream: extract data: lines
        if (body.Contains("data:", StringComparison.Ordinal))
        {
            body = ExtractSseData(body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result))
        {
            return null;
        }

        if (!result.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[Web search results]");

        var hasContent = false;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    builder.AppendLine(value);
                    hasContent = true;
                }
            }
        }

        return hasContent ? builder.ToString().Trim() : null;
    }

    private static string ExtractSseData(string sseBody)
    {
        // Pick the last non-empty data: line that looks like JSON
        var lines = sseBody.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data.StartsWith("{", StringComparison.Ordinal))
            {
                return data;
            }
        }

        return string.Empty;
    }

    private sealed record McpRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        int Id,
        string Method,
        object Params);

    private sealed record McpInitializeParams(
        [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
        [property: JsonPropertyName("clientInfo")] McpClientInfo ClientInfo,
        McpCapabilities Capabilities);

    private sealed record McpClientInfo(string Name, string Version);

    private sealed record McpCapabilities();

    private sealed record McpToolCallParams(string Name, object Arguments);

    private sealed record ExaSearchArguments(
        string Query,
        [property: JsonPropertyName("num_results")] int NumResults);
}
