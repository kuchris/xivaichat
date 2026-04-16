# XivAiChat

Personal Dalamud dev plugin that watches the FFXIV chat channels you choose, sends per-channel context to a local or API model, and either prints a draft locally or replies back into the same in-game channel.

## Install From Custom Repo

Add this custom repo URL in Dalamud:

`https://raw.githubusercontent.com/kuchris/xivaichat/main/repo.json`

Then:

1. Open `/xlsettings`
2. Go to `Experimental`
3. Under `Custom Plugin Repositories`, add the URL above
4. Save
5. Open `/xlplugins`
6. Find `XIV AI Chat` and install it

## Auto Build

This repo includes GitHub Actions automation for the custom repo flow.

When you push to `main`, GitHub Actions will:

- restore and build the plugin
- run `tools/pack.ps1`
- rebuild `dist/XivAiChat.zip`
- sync `repo.json` to the built plugin version
- commit updated generated files back to the repo

That means your custom repo URL stays the same:

`https://raw.githubusercontent.com/kuchris/xivaichat/main/repo.json`

And users can keep updating from `/xlplugins` without you rebuilding and zipping everything by hand every time.

## What It Does

- Watches multiple channels at the same time instead of only one CWLS.
- Keeps separate recent history per watched channel.
- Ignores your own messages to reduce reply loops.
- Requires a mention or alias by default so it does not answer every line.
- Can print drafts locally first, or auto-send replies back into the source channel.
- Supports saved system prompt presets in the in-game window.
- Supports local LM Studio and API providers like Gemini.
- Trims outgoing chat to fit FFXIV chat length limits.

## Supported Channels

- General: `Say`, `Party`, `Alliance`, `Free Company`, `Novice Network`, `Yell`, `Shout`
- Linkshell: `Linkshell 1` to `Linkshell 8`
- Cross-world Linkshell: `CWLS 1` to `CWLS 8`

## Providers

### LM Studio

- Default endpoint: `http://127.0.0.1:1234/api/v1/chat`
- The plugin can auto-detect the loaded model from LM Studio so you do not have to retype it every time.
- Local reasoning can be toggled in the config window.

### Gemini / API

- Gemini uses Google AI Studio API keys.
- `gemini-*` models use Google’s OpenAI-compatible chat endpoint.
- `gemma-*` models are auto-routed to Google’s native Gemini API path.
- The config window includes quick model buttons for common Gemini / Gemma choices.

## Prompt Presets

The plugin has built-in presets and also lets you save your own.

Current built-ins:

- `English`
- `Game AI`
- `Traditional Chinese`
- `Japanese`

You can:

- switch presets in game
- save edits back to the active preset
- create your own presets
- delete custom presets

## In-Game Setup

1. Build the project.
2. Open `/xlsettings`.
3. Add the built DLL path or build output folder under `Experimental > Dev Plugin Locations`.
4. Open `/xlplugins`.
5. Go to `Dev Tools > Installed Dev Plugins`.
6. Enable `XIV AI Chat`.

After that, open `/xivaichat` to use the settings window.

## Recommended First Run

1. Start with `Auto-send replies` turned off so the plugin only prints drafts locally.
2. Pick your provider in the plugin window.
3. If you use LM Studio, start its local server and click `Detect loaded model`.
4. Choose the channels you want to watch.
5. Keep `Require mention or alias` on at first.
6. Pick a prompt preset.
7. Use `Run Test` in the plugin window.
8. When the replies look good, turn on `Auto-send replies`.

## Slash Commands

The plugin is mainly meant to be configured from the in-game window, but these commands still work:

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

`/xivaichat slot <1-8>` is a quick shortcut that replaces the watched channel list with only that CWLS slot. For multi-channel setup, use the window instead.

## Behavior Notes

- Replies are generated from the recent history of the same channel that triggered the response.
- With auto-send off, the reply is only printed locally as a draft.
- With auto-send on, the plugin tries to send the reply back into the same watched channel.
- The plugin prefers short, in-game-safe output and strips obviously bad formatting when needed.
