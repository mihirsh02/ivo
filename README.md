# Ivo

**Ivo is an AI desk for Windows.** One app for chatting with any AI model, letting an agent work in your code, running scheduled tasks that report back, and keeping your day organised. It lives in the tray, opens with a shortcut, and stays out of the way until you need it.

It talks to cloud models through your own API keys, to models running on your PC, and to the AI command-line tools you already use (Claude CLI, Copilot CLI, Gemini CLI, Docker's Gordon and more), with their own sign-in.

---

## Contents

- [Features](#features)
  - [Chat](#chat)
  - [Models and providers](#models-and-providers)
  - [Agents and workspaces](#agents-and-workspaces)
  - [Code mode](#code-mode)
  - [Inbox, planner and morning briefing](#inbox-planner-and-morning-briefing)
  - [Read aloud and voices](#read-aloud-and-voices)
  - [Connections](#connections)
  - [Spotify](#spotify)
  - [Companion, tray and shortcuts](#companion-tray-and-shortcuts)
  - [Look and feel](#look-and-feel)
  - [Calendar and starred replies](#calendar-and-starred-replies)
  - [Your data](#your-data)
- [Install](#install)
- [Build from source](#build-from-source)
- [Where things are stored](#where-things-are-stored)
- [Project layout](#project-layout)
- [How some things work](#how-some-things-work)
- [The native build (IvoNative)](#the-native-build-ivonative)
- [Known limits](#known-limits)

---

## Features

### Chat

- **Ivo, the mascot:** the indigo-violet droplet creature with a cyan visor, pinned at the top of the chat list; she floats, sways and waves hello. Her own chat (not a model chat: no pickers or options) needs no model: she knows every part of Ivo, follows up when you ask "how?", copes with typos, stretched words ("heyyy") and different wording, speaks Gen Z (wyd, wya, nvm, ngl, rizz, no cap… and explains them), remembers your name, talks, does sums and unit conversions, and tells the time. A word she's never met is learnt quietly (Urban Dictionary or a dictionary, free, no account) and kept on this PC, so it's known offline from then on. "Show me around" starts the tour, which she leads. Her chat and the words she's learnt are kept on this PC, sealed with the app lock.
  - **She asks back:** answers to what she just asked ("wya?" → "with me") are read as answers.
  - **What you mean, not just the words:** every message is matched to one of her intents (`Core/IvoIntents.json`: greeting, what she's doing, confused, upset with her, laughing, sad, happy…) by its closest example or its weighted words, so new wording and typos still land ("whatchu doin", "heylol" → "hey lol"). Generic "go on" replies are the last resort.
  - **Moods:** she reads yours from words, emoji, ALL CAPS and "!!!" and answers in one of her own: sorry, sulky (be mean twice), caring, hyped, cheeky or mad.
  - **She learns:** a word she doesn't know is looked up (dictionary, Urban Dictionary, DuckDuckGo); what it means is read as an intent, answered, and kept, so it works offline next time. **Double-click any message** (yours, or her reply to it, old ones too) or type `/learn …` and she looks it up there and then: the meanings of its words, then the message itself (DuckDuckGo, Wikipedia), answers it and keeps it. Teach her too: "when I say gm, say good morning sunshine", "rizzler means someone with rizz". Settings → Assistant → **What Ivo has learnt** lists it all, each with a remove button, and Forget all. Kept in `ivo-memory.json`, sealed with the app lock.
  - **Maths and logic:** sums with brackets and powers, "15% of 240", "which is bigger, 2^10 or 1000", "is 17 prime", "days until christmas", "what day is 3 March 2027", unit conversion.
  - **Does it for you:** in plain words or with `/` (type `/` for the list): reminders ("remind me at 5 to call mom"; a bare hour is the next one ahead), alerts, daily tasks, to-dos, habits, Spotify (play a song, artist or playlist by name; pause, next, previous; needs Premium), Portal calls and messages to your contacts, any setting ("switch theme to amoled", "turn off code mode"; she works the Settings page's own switch, so it's exactly like clicking it), and opening any section or panel. Missing a detail, she asks for just that ("what time?"). Calls, messages and weighty settings (restart, reset, removal, the lock) show **Do it / Cancel** first; the rest happen at once with **Undo**. Text and password boxes are never touched: keys, PINs and sign-ins stay yours to type.
  - **Music:** "what am I playing?", "play", "pause", "stop the music", "skip this song", "queue blinding lights" / "play X next" / `/queue`, "lyrics", and the rest of Spotify (`/spotify …` or plain words): shuffle, repeat (song or playlist), volume / mute, "skip to 1:30" / "back 10 seconds", like / unlike the song, "add this song to <playlist>", "play my <name> playlist", "play my liked songs", "show my playlists", "what's up next" (your queue), recently played, your top songs and artists, "play the album …", "play songs by …", podcasts, "follow this artist", "my devices", "play on my phone" (from LRCLIB), "tell me about this song" / "who sings this?". With her chat open, about one new song in ten gets an "I love this song" and a fact about it. Playing by name picks a device that's really there (the active one, else this PC's Spotify app) and checks the song actually started.
  - **YouTube and Reddit:** "follow mkbhd on youtube", "any new videos from mkbhd?", "what's new on youtube", `/youtube` (channels' public feeds, no account); "top posts in r/india", "r/technology this week", "what's hot on reddit", `/reddit` (Reddit's official read-only API with a free "installed app" id in Settings → Assistant). Videos and posts come with buttons that open them.
  - **Habits from chat:** "tick stretch", "I did my gym", `/habit done stretch`; streaks of 3, 7, 14, 30, 50 and 100 days get a cheer.
  - **Context:** she follows the conversation ("who is Elon Musk?" → "how old is he?"; "I love pizza" → "and you?"; "what were we talking about?") and keeps what you tell her about yourself ("my favourite colour is blue", "I'm learning guitar", "I have an exam tomorrow"): "what's my favourite colour?", "what do you know about me?", her hello asks how the exam went, and she uses it ("I'm hungry" with a favourite food she knows). Settings → Assistant → What Ivo has learnt → About you.
  - **Behaviour packs:** how the model talks lives in a JSON pack, not in the code: her persona, clean mode, sample lines, per-message notes, phrases to avoid, and her own lines for messages or topics. The app ships a clean, kind and caring default (`Assets/ivo/behaviour.json`); your own packs go on top in Settings → Assistant → Behaviour packs (a note with the same `id` replaces the default's). Format: `Core/IvoBehaviour.cs`.
  - **Smarter chat (optional):** Settings → Assistant → Smarter chat downloads a small language model (Qwen2.5 0.5B Instruct, 4-bit, ~400 MB) that writes every reply, so she's one voice that follows the conversation. Her brain still reads each message first: it does every action (reminders, music, calls, settings), keeps learning, and hands the model the facts (your plans, the time, sums, contacts, what she knows about you, what she looked up), your mood, her personality and the last few messages. It answers every message itself, questions about the app too: her brain hands it what it knows (how a feature works, your plans, the time) and the model decides whether that's what you're asking. Unsafe replies are swapped for her own line. Loaded on use, let go after 30 s idle, 2 threads; ~2–3 s a reply. **Add your own model…** copies any GGUF (e.g. your trained Ivo) into Ivo's models folder and replaces the standard download; with several, pick which one she uses.
  - **Her personality and age:** Settings → Assistant → Her personality (Sweet, Cheeky, Sassy, Chill, Nerdy, Shy, or your own words). Her age is about yours, a year or two either side, from your date of birth.
  - **Your Portal contacts:** "who are my contacts?", "is Riya in my contacts?", "how many contacts do I have?"
  - **Where things are:** "where do I change notifications?" names the place, with a button that opens it. "What's on today?" includes your calendar's events.
  - **Emoji:** drawn the way Windows 11 draws them, gradients and all, from Windows' own emoji font (`Chat/ColorEmoji.cs`).
  - **The wider world:** "who is…", "what is…" and "tell me about…" are found out quietly (DuckDuckGo, Wikipedia) and kept on this PC, so they're known offline afterwards.
  - **Your own datasets (Settings → Assistant → Datasets):** JSON files of things people might say and how she replies, checked before what she knows. Each entry has `match` (example phrases, understood with typos and other wording) or `words` (keywords with weights, a reply at 3), `reply` (one picked at random; `{name}` is the user's name), and optional `more` (for "how?"). Files are copied into Ivo's data and sealed with the app lock.
  - **Your plans:** ask "what are my tasks?", "any reminders?", "what do I have today?" or "my habits" and she reads them from the Planner: open to-dos (soonest first, overdue marked), reminders, daily tasks and today's habits.
  - **Desktop widgets:** Settings → **Assistant** → **Desktop widgets**: Ivo herself (she waves at reminders due in 5 minutes and at new songs), clock & weather (Open-Meteo, by your rough location, no key), now playing on Spotify, today's habits (click to tick) and the next reminders. They sit on the desktop under every window, stay through Win+D, and stay where you drag them; **Lock them in place** to stop dragging. Right-click one to hide it.
  - **Weather:** "will it rain?", "weather in Paris", "is it cold tomorrow": she answers from Open-Meteo (free, no key), with a remark of her own, and the morning briefing starts with it. Your city goes in Settings → **Assistant** → **Weather**; with none set, it's where your IP says you are. Kept 15 minutes; the desktop clock widget reads the same.
  - **Quiet hours:** Settings → **Assistant** → **Quiet hours**: no pop-ups or chimes during the hours you set, or while a full-screen app, game or presentation is up. What came in waits, and one card tells you after.
  - **Your mood:** she keeps a daily record of the mood she reads in what you tell her (the last 60 days, on this PC), shows the week as faces in Settings → **Assistant**, and checks in gently when two of the last three days were low.
  - **Journal:** each day gets a short entry of what you talked about, readable and editable in Settings → **Assistant** → **Journal**; when a topic comes back she now and then remembers when you last talked about it.
  - **Incognito:** the 🕶️ button in her chat header opens a private chat. Nothing in it is saved, and she won't remember it, journal it or log your mood. Turning it off brings back your saved chat.
  - **Her looks:** the same indigo-violet mascot is used for her desktop presence, settings, introduction, widgets, and chat. It floats subtly and has a click reaction.
  - **Clear chat:** the bin in her chat starts afresh; what she has learnt stays.

- **Markdown replies** with headings, lists, tables, code blocks with syntax colouring, images and maths.
- **Streaming** with a live "Thinking" chip you can open to read the model's reasoning, where the model shares it.
- **Reply to a message, or to part of one:** select any text in a prompt or reply and choose Reply, and it's quoted into your next message.
- **Retry, edit and resend** any of your messages; copy any reply.
- **Multi-chat:** up to three chats side by side. Drag a pane by its header to rearrange. Clicking a chat in the sidebar fills an empty "New chat" pane before it opens another.
- **Incognito chat:** not saved, and nothing is remembered from it.
- **Memory:** the model can save lasting facts about you ("remember that…"), which carry into future chats. View or clear them in Settings → Profile.
- **Profile:** your name, date of birth (your age is worked out from it), gender (male or female, so Ivo knows), work and what you use Ivo for, shared with models only if you allow it.
- **Saved prompts:** type `/` in the message box to drop one in.
- **Attachments:** files, images, and photos from the Windows Camera app.
- **Dictation:** speak your message with the mic button.
- **Context ring:** shows how full the model's memory is. When it fills, older messages are summarised automatically, or with `/compact`.
- **Search** across the titles and messages of every chat; opening a result jumps to the matching message and outlines it.
- **Organise chats** with pins, groups and archive.
- **Projects:** right-click a group's name to give it instructions (sent with every chat in it), a default model and a folder for new chats.
- **Compare models:** `/compare your question` asks up to three models side by side; picking the best answer teaches Auto your taste.
- **Export** chats to Markdown.
- **Reading width:** replies stop at a comfortable width however wide the window is.

### Models and providers

Add a provider in **Settings → API keys** or **Gateways**:

| Kind | Examples |
|---|---|
| API keys | OpenAI, Anthropic, Gemini, Groq, Mistral, DeepSeek, xAI (Grok), Together AI, Fireworks, Perplexity, Cerebras, or any OpenAI-compatible server |
| Gateways (one key, many models) | OpenRouter, Vercel AI Gateway, Requesty, AI/ML API, or your own gateway |
| Models on this PC | Ollama, LM Studio (found automatically) |
| Command-line tools | Claude CLI, Copilot CLI, Gemini CLI, Gordon (Docker AI), Codex, Qwen Code, Cursor Agent, opencode, Aider, Goose, Amp, Crush, llm (on Windows or in WSL) |

- **Model checks:** Ivo sends each model a tiny test message and remembers which answer.
  - **Only show models that work** is a separate switch for each provider, on by default. It hides models that failed their check.
  - Pinned models, the provider's default, and the model a chat already uses always stay visible.
- **Command-line tools:**
  - **Claude CLI:** each exact version is checked through the CLI itself, weekly and when you press ✓. The checks leave nothing in Claude's history, and a tool that's out of quota keeps its earlier results.
  - **Copilot CLI, Gemini CLI, Gordon** and other tools that pick their own model get a single "does it answer" check.
- **Usage and cost:** token counts and an estimated cost per message and per chat. Each provider card shows what Ivo has sent through it, and the remaining balance where the provider reports it.
- **Spending** (Settings → Spending): a daily limit across all providers, a monthly budget per provider, and a question before using a model above a price you set. Past a limit, Ivo asks before sending.
- **Search by meaning in your notes** works with no cloud key when Ollama has an embedding model on this PC.
- **Scan this PC** (Settings) finds AI tools, keys in environment variables, MCP servers and skills already on the PC or in WSL, and adds them in a click. It then checks every provider's models, showing progress and the total time on a summary card. Models aren't checked on launch; Scan, or ✓ on a provider card, updates them.

### Agents and workspaces

Open a folder in a chat and the model becomes an agent working in it.

- **Tools:** list, read, search, edit and write files; fetch web pages; run PowerShell commands; to-dos and reminders; memory; skills; and handing tasks to your own agents.
- **Modes:**
  - **Plan** only reads, then proposes a plan.
  - **Normal** checks in before big changes.
  - **Autopilot** works through the task on its own.
- **Permissions:**
  - **Manual** asks before every change.
  - **Assisted** lets low-risk changes run.
  - **All** never asks.
  - File tools can never leave the workspace.
- **Undo** for file changes the agent made.
- **Project instructions:** `AGENTS.md`, `CLAUDE.md` or `.github/copilot-instructions.md` in the folder are passed to the model.
- **MCP servers** (Settings → Tools): the same `mcpServers` JSON as Claude Desktop. Local (`command`) and remote (`url`) servers are supported.
- **Skills:** reusable know-how (`SKILL.md` folders) the model loads when a task needs it, plus a skills marketplace to browse.
- **Agents** (Settings → Agents): specialists with their own instructions, model and access, which the chat model can delegate to.

### Code mode

Switch with **Chat | Code** in the title bar.

- **Explorer:** file tree with create, rename and delete (to the Recycle Bin), and **Git**: each repository's branch, its changes in **Staged** and **Changes** lists (+ / − on a file, or on a list to move everything), a diff for any file, and **Commit** or **Commit & push** (Ctrl+Enter commits). With nothing staged, they take every change (**Commit all**, **Push all**). Right-click a repository to **switch branch** or start a new one, **pull**, **push**, **fetch**, **stash** and bring changes back, **undo the last commit** (keeping its changes), **discard all changes**, copy the branch name, or open the folder.
- **Editor:**
  - Tabs with syntax colouring for C#, JavaScript/TypeScript, Python, HTML/XML, CSS, SQL, PowerShell, Markdown and more.
  - **Find and replace** (Ctrl+H), **Go to file** (Ctrl+P), **Save** (Ctrl+S).
  - **Markdown preview** beside the text (Ctrl+Shift+V).
  - Images are previewed, and other binary files open in a hex view.
- **Terminal:** several terminals (PowerShell, cmd, Git Bash, WSL) as tabs.
- **Chat:** a chat docked on the right that works in the open folder.
- **Focus outlines:** the panel you're working in is outlined in purple, the others in quiet grey.

### Inbox, planner and morning briefing

- **Inbox:** reminders, task results and briefings land here as cards on a 3D wheel you can flick through.
  - Filter (Everything, Unread, Starred, or by kind: reminders, daily tasks, dev tasks, briefings), sort and search.
  - The reader shows one item in full.
- **Planner:**
  - **To-do list:** one line carries the whole to-do, e.g. "Call mom tomorrow 6pm !high #family ~Home every week" (a day or time, `!high`/`!med`/`!low`, `#tags`, a `~list`, a repeat).
    - Views: Today, Next 7 days, All and each list; sections for Overdue, Today, Tomorrow, Next 7 days, Later and No date.
    - The tick's colour shows priority. Click a to-do for quick dates, priority, repeat and its steps; ticking a repeating one moves it to its next time.
  - **Reminders:** one-off or repeating, or for when an app opens ("when I open Teams"). Snooze for 10 minutes, an hour, until after lunch, this evening or tomorrow.
  - **Daily tasks:** a prompt run on a schedule. Quotes, alerts and follow-ups each have a category and colour.
  - **Dev tasks:** the coding agent working in a folder on a schedule.

- **Morning briefing:** a daily briefing when you wake up: the weather, your day, your to-dos, the news, and any reminders or tasks due.
- **Read aloud and voices:** replies can be read aloud; the voices are from Microsoft or offline natural voices, and a pop-up asks before a task reads its result.
- **Connections:** Connects to YouTube, Reddit, GitHub, GitLab, Notion, Google tasks, the PC (Spotify, file system), websites, Slack, Discord and more. Some are local-only, some use OAuth or API keys.
- **Spotify:** official Web API only, signed in with OAuth PKCE; Ivo is a remote controller, not an audio player. Play, queue, browse, like, follow and see what's now playing.
- **Companion, tray and shortcuts:** Ivo has a mascot and a tray icon; choose a shortcut to open the app, and the tray keeps it out of the way until you need it.
- **Look and feel:** a light/dark theme, an ambient glow, the app can float over the desktop, and the UI is tuned to be calm and focused.
- **Calendar and starred replies:** month view with your events and schedules; starred replies are stored even when a chat is deleted.
- **Your data:** backups, encrypted key storage, crash logs, app lock, and privacy controls.

---

## Install

Three installers in `dist\` put the same Ivo in `Program Files` (one administrator prompt), and each replaces the others' install, so there's only ever one Ivo. None needs .NET installed: Ivo carries its own runtime. Your chats and settings are never touched by installing or updating.

| Installer | What it is |
|---|---|
| `IvoInstall-<version>.exe` | Ivo's own installer, in Ivo's look: folder, desktop shortcut and start-with-Windows choices, then "Open Ivo". Its uninstaller asks before deleting your data. |
| `IvoSetup-<version>.exe` | A classic setup wizard (Inno Setup) with Ivo's artwork. |
| `IvoSetup-<version>.msi` | For IT deployment (Intune, Group Policy): `msiexec /i IvoSetup-<version>.msi /qn`. |

When switching installer types, the previous install's uninstaller must still be present. If endpoint protection removed it, ask IT to restore or remove that install before running setup again.

Voice packs and other large pieces download only when you choose them.

**Requirements:**
- Windows 10 version 1809 or later, or Windows 11, 64-bit.
- Microsoft Edge WebView2 (preinstalled on current Windows) for the terminal and in-app web pages.

---

## Build from source

You need the **.NET 10 SDK**.

```powershell
# run from source
dotnet run --project Ivo.csproj

# the checks: encryption, secret masking, schedules, time parsing (the installer build runs them first)
dotnet run --project tests\Ivo.Checks

# the installers (needs Inno Setup 6 and WiX 5: winget install JRSoftware.InnoSetup; dotnet tool install -g wix --version 5.0.2)
.\installer\build.ps1        # runs the checks, then → dist\IvoInstall-<version>.exe, IvoSetup-<version>.exe and .msi, signed when the Ivo certificate is present

# the installer artwork, redrawn from Ivo's colours and crystal
python tools\installer-art\make_art.py
```

**Startup options:**
- `--tray`: start in the tray, no window.
- `--quiet`: skip the launch animation.

Only one Ivo runs at a time. Starting it again brings the running one to the front.

---

## Where things are stored

Everything lives in **`%AppData%\Ivo`**:

| Path | What |
|---|---|
| `settings.json` | Providers (keys encrypted), preferences, schedules, to-dos, connections, Spotify folders, model check results. Written safely, so a crash mid-save can't damage it. With the app lock on, the personal parts are kept in an encrypted `Vault` inside it. |
| `chats\*.json` | One file per chat (encrypted while the app lock is on) |
| `inbox.json` | Inbox items (encrypted while the app lock is on) |
| `failures.log` | Every failure, one line each (Settings → Data & storage → Developer) |
| `activity.log` | Every change an agent made (encrypted while the app lock is on) |
| `knowledge-index.json` | The search index of your knowledge folder (encrypted while the app lock is on) |
| `agents\`, `skills\` | Your agents and skills |
| `voices\` | Downloaded natural voice packs |
| `web\` | The embedded browser's profile (terminal, sign-ins) |
| `cli\` | Working folder for command-line tools when a chat has no workspace |
| `crash.log` | Every crash, if any |

---

## Project layout

```
Ivo.csproj          WPF app, .NET 10, Windows
App.xaml(.cs)       Startup, single instance, themes and shared styles, the global line and corner rules
Core/               Settings and chat storage, model APIs and pricing, Inbox, time helpers, Markdown export
Agents/             Tool-using agent, CLI adapters and model checks, MCP client, skills, subagents,
                    context compaction, planner tools, Connections (GitHub, GitLab, Notion, Spotify)
Platform/           Voices (Windows + offline natural voices), pseudo-terminal, elevation helper,
                    discovery of tools on the PC and in WSL, sounds
Chat/               The chat pane: messages, Markdown rendering, composer, orb, agent runs, selectable text
Windows/            MainWindow split into partial files by area: Shell (tray, hotkeys, dialogs), Settings,
                    Code, Explorer, Git, Find, Inbox, InboxWheel, Schedule, Tasks, Todo, Voice, Profile,
                    Extensions, Connections, Spotify, Popout, Calendar, TitleBar, Lock, Security, Stars,
                    Capture, Projects, Tour, Intro; plus SpotifyView, Companion, TrayMenu, NotifyCard, Splash,
                    TimePicker, TabStrip, Highlight, Camera
Assets/             Ivo's icon fonts, chime, terminal and camera pages
installer/          Ivo's own installer (IvoInstall), the Inno Setup and WiX scripts, artwork and the build script
tests/Ivo.Checks/   The checks, as a small console program
```

The code follows one style: plain-English comments explaining *why*, short methods, and UI mostly built in code next to the behaviour it drives.

---

## Developer quickstart

If you're working on Ivo itself, use this loop after every meaningful change.

### 1. Restore and build locally

```powershell
# from the repo root
dotnet restore

dotnet build Ivo.csproj
```

This catches compile errors quickly before you run the app.

### 2. Run the app during development

```powershell
dotnet run --project Ivo.csproj
```

For a tray-started session while developing:

```powershell
dotnet run --project Ivo.csproj -- --tray
```

If you need the app without the splash animation:

```powershell
dotnet run --project Ivo.csproj -- --quiet
```

### 3. Run the validation checks after code changes

The project has a small validation app for encryption, masking, scheduling and time-parsing behaviour.

```powershell
dotnet run --project tests\Ivo.Checks
```

Run this whenever you touch:
- app-lock or encryption logic
- provider/model routing
- scheduling, reminders, or planner logic
- secret redaction and privacy behaviour
- time/date parsing or calendar tasks

### 4. Build installers when preparing a release or packaging change

```powershell
.\installer\build.ps1
```

This script runs the checks first and then produces the packaged installers under `dist\`.

You will need:
- .NET 10 SDK
- Inno Setup 6
- WiX 5

Example setup:

```powershell
winget install JRSoftware.InnoSetup
dotnet tool install -g wix --version 5.0.2
```

### 5. Typical per-change workflow

For UI changes:
```powershell
dotnet build Ivo.csproj
dotnet run --project Ivo.csproj
```

For logic/security changes:
```powershell
dotnet build Ivo.csproj
dotnet run --project tests\Ivo.Checks
dotnet run --project Ivo.csproj
```

For packaging or installer changes:
```powershell
dotnet build Ivo.csproj
dotnet run --project tests\Ivo.Checks
.\installer\build.ps1
```

### 6. Useful project notes

- `App.xaml.cs` is the top-level app bootstrap and shared visual rules.
- `MainWindow.*` is the main shell and feature wiring.
- `Core/` contains the app state, settings, provider data, timing helpers, and assistant memory.
- `Agents/` contains the tool-using agent, CLI wrappers, MCP, skills and planner logic.
- `Platform/` covers OS integrations like dictation, voice, terminal, sound and watchdog features.
- `Portal/` is the standalone communicator app.

---

## How some things work

- **Model checks:** each model gets one short "Reply with just the word OK" message. A result is only recorded when a check finishes. If a provider reports that it's out of quota or rate-limited, the earlier results are kept and the next automatic check waits 12 hours.
- **Claude CLI versions:** the candidate versions come from Anthropic's model list (or OpenRouter's public catalogue). Each is kept only if Claude CLI actually answers with it. Its shortcuts (opus, sonnet, haiku) are used only until the first check.
- **Agents and approvals:** tool calls that change things show a card with the change before it runs; reads run freely. File tools resolve every path inside the workspace and refuse anything outside it.
- **Spotify:** official Web API only, signed in with OAuth PKCE, so there's no client secret in the app. The refresh token is stored encrypted. Ivo is a remote: it never streams audio itself.
- **Line weight and corners:** a single rule in `App.xaml.cs` sets every divider to the same weight for the current display scaling, snapped to the pixel grid, and gives every card one corner rounding.
- **Encryption at rest:** a random 256-bit data key seals files with AES-GCM. It's stored only wrapped: by a PBKDF2 key from your PIN or password, and by each recovery code. Until Ivo is unlocked once after it starts, sealed files read as empty and are never overwritten, and scheduled tasks wait.
- **Hiding secrets:** before a request goes to a cloud model, recognised secrets are swapped for `[secret-n]` placeholders; the reply and any tool calls get the real values back. Local models get the real text.
- **Sandboxed MCP servers:** started with a duplicated Windows token lowered to Low integrity, with their input and output piped back to Ivo.

---

## The native build (IvoNative)

`..\IvoNative` is a port of Ivo to **Avalonia with .NET Native AOT**. It compiles to machine code, with no IL to decompile, which makes the app much harder to reverse-engineer than the WPF build.

- **Status:** it builds and starts in the tray. Its screens still need testing and fixing.
- **Differences from the WPF build:**
  - a native terminal instead of the embedded web view;
  - provider pages open in your browser;
  - maths shows as formatted text;
  - the crystal orb is drawn in 2D.
- **Following the WPF build:** features land in the WPF build first and are ported afterwards.
- **Porting guide:** `IvoNative\PORTING.md`.

---

## Known limits

- **Copilot CLI** on the Free plan only runs its own pick (auto). Named models can't be chosen from outside its app, so Ivo uses auto.
- **Gemini CLI** and **Gordon** pick their own model. Ivo checks only that they answer.
- **Spotify:**
  - friend activity, recommendations, related artists and Spotify's own playlist folders aren't available through Spotify's official API, so Ivo doesn't show them;
  - controlling playback needs Premium.
- **The Windows Narrator "natural" voices** can't be used by other apps. Ivo offers its own offline natural voices instead.
- **The terminal** in the WPF build is a web view: it hides while overlays are open, and can't be clipped to rounded corners.
