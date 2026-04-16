# XivAiChat

![XivAiChat icon](images/icon.png)

`XivAiChat` is a personal Dalamud plugin for Final Fantasy XIV that watches the chat channels you choose, sends recent channel history to an AI model, and either prints a draft locally or replies back into the same in-game channel.

It is built for lightweight in-game conversations rather than long assistant-style responses. The plugin keeps per-channel context, supports local LM Studio as well as API providers, and includes saved prompt presets directly in the config window.

## Features

- Watch multiple FFXIV chat channels at the same time
- Keep separate recent history for each watched channel
- Ignore your own messages to reduce reply loops
- Require a mention or alias before replying
- Print replies locally first, or auto-send them into the source channel
- Save and switch system prompt presets in game
- Support LM Studio, OpenAI-compatible endpoints, and Gemini
- Optionally add Exa web search context before generating a reply
- Trim and sanitize replies to stay within FFXIV chat limits

## Supported Channels

- General: `Say`, `Party`, `Alliance`, `Free Company`, `Novice Network`, `Yell`, `Shout`
- Linkshell: `Linkshell 1` to `Linkshell 8`
- Cross-world Linkshell: `CWLS 1` to `CWLS 8`

## Install From Custom Repo

Add this custom repo URL in Dalamud:

`https://raw.githubusercontent.com/kuchris/xivaichat/main/repo.json`

Then:

1. Open `/xlsettings`
2. Go to `Experimental`
3. Add the repo URL under `Custom Plugin Repositories`
4. Save
5. Open `/xlplugins`
6. Find `XIV AI Chat` and install it

The packaged plugin zip is published from:

`https://raw.githubusercontent.com/kuchris/xivaichat/main/dist/XivAiChat.zip`

## Install As A Dev Plugin

1. Build the project
2. Open `/xlsettings`
3. Add the build output folder under `Experimental > Dev Plugin Locations`
4. Open `/xlplugins`
5. Go to `Dev Tools > Installed Dev Plugins`
6. Enable `XIV AI Chat`

Open `/xivaichat` to configure it in game.

## Providers

### LM Studio

- Default endpoint: `http://127.0.0.1:1234/api/v1/chat`
- The plugin will try LM Studio's local endpoints in this order:
  `v1/responses`, native `api/v1/chat`, then `v1/chat/completions`
- The config window can detect the currently loaded LM Studio model
- Local reasoning can be toggled for LM Studio requests

### OpenAI-Compatible

- Any OpenAI-style chat completions endpoint should work
- Set the endpoint, model name, and API key in the config window

### Gemini

- Uses a Google AI Studio API key
- `gemini-*` models use Google's OpenAI-compatible chat endpoint
- `gemma-*` models are automatically routed to Google's native `generateContent` API
- The UI includes quick model buttons for common Gemini and Gemma targets

## Web Search

Exa web search is optional. When enabled, the plugin queries Exa's MCP endpoint before generating a reply and includes a short search summary only when it looks relevant.

- No Exa API key is required by the current implementation
- This feature depends on the client being able to reach `https://mcp.exa.ai/mcp`

## Prompt Presets

Built-in presets:

- `English`
- `Game AI`
- `Traditional Chinese`
- `Japanese`

You can:

- switch presets in game
- load the active preset back into the editor
- save edits to the active preset
- create custom presets
- delete custom presets

## Recommended First Run

1. Leave `Auto-send replies` off so the plugin prints drafts locally
2. Pick a provider
3. If you use LM Studio, start its local server and use `Detect loaded model`
4. Choose the channels to watch
5. Keep `Require mention or alias` enabled at first
6. Pick a prompt preset
7. Run a quick manual test from the config window
8. Turn on `Auto-send replies` only after the output looks safe

## Behavior Notes

- Replies are generated from recent history in the same channel that triggered the response
- Only one AI request runs at a time
- Duplicate chat events are ignored for a short window
- Replies are rate-limited by the configured cooldown
- Outgoing text is cleaned up before sending to the game chat box
- If in-game dispatch fails, the plugin falls back to printing the reply locally

## Slash Commands

- `/xivaichat`
- `/xivaichat help`
- `/xivaichat status`
- `/xivaichat on`
- `/xivaichat off`
- `/xivaichat send on`
- `/xivaichat send off`
- `/xivaichat slot 1`
- `/xivaichat endpoint <url>`
- `/xivaichat model <name>`
- `/xivaichat prompt <text>`
- `/xivaichat alias <word>`
- `/xivaichat alias clear`
- `/xivaichat mention on`
- `/xivaichat mention off`
- `/xivaichat cooldown <0-600>`
- `/xivaichat test <message>`

`/xivaichat slot <1-8>` is a shortcut that replaces the watched channel list with a single CWLS slot. For multi-channel setups, use the config window.

## Build

From the repo root:

```powershell
dotnet build XivAiChat.sln
```

To package the plugin zip and refresh `repo.json` metadata after a successful build:

```powershell
.\tools\pack.ps1
```

## Project Layout

- `XivAiChat/`: plugin source, manifest, and config UI
- `tools/pack.ps1`: packages the build output into `dist/XivAiChat.zip`
- `repo.json`: custom repository entry for Dalamud
- `images/icon.png`: plugin icon used by the repo manifest

## Notes

- This is a personal project and a dev-focused Dalamud plugin
- Use auto-send carefully, especially in public channels
- Web search and API providers require network access from the machine running the game/plugin
