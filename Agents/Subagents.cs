using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ivo;

// Agents: specialists the chat model hands work to (like Claude Code's sub-agents). Each is a Markdown file with a short
// header (name, description, model, tools) and its instructions; the same files Claude Code reads from .claude/agents.
// Yours live in <data>/agents; a project's .claude/agents folder adds its own.
public record AgentDef(string Name, string Description, string Instructions, string? Model, bool ReadOnly, string File, bool Project);

// One hand-off to an agent, for the agent map: what it was asked, how far it's got, and what it said.
public class AgentRun
{
    public string ChatId = "", Agent = "", Task = "", Model = "";
    public DateTime Started = DateTime.Now;
    public DateTime? Ended;
    public int Tokens, Steps;
    public string Doing = "Starting";
    public string? Result;
    public string Transcript = "";   // every step with what it got back: for the user (agent cards, the map), not the chat model
    public bool Failed;
    public TimeSpan Took => (Ended ?? DateTime.Now) - Started;
}

public static class Subagents
{
    // the runs of this session, newest last; Changed fires (on any thread) when one starts, moves on or ends
    public static readonly List<AgentRun> Runs = [];
    public static event Action? Changed;
    static void Tell() => Changed?.Invoke();
    public static List<AgentRun> RunsOf(string chatId) { lock (Runs) return Runs.Where(r => r.ChatId == chatId).ToList(); }

    public static string Dir => Path.Combine(Store.Dir, "agents");

    public static List<AgentDef> All(string? workspace = null)
    {
        var list = new List<AgentDef>();
        void Load(string dir, bool project)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.md"))
                try { if (Parse(File.ReadAllText(f), f, project) is { } a && list.All(x => !x.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))) list.Add(a); } catch { }
        }
        if (workspace != null) Load(Path.Combine(workspace, ".claude", "agents"), true);   // a project's own agents win over yours with the same name
        Load(Dir, false);
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---
    // name: reviewer
    // description: when to use it
    // model: provider/model (or inherit)
    // tools: Read, Grep, Glob        (Claude Code's list; any of Write/Edit/Bash means it can make changes)
    // ---
    // instructions…
    static AgentDef? Parse(string text, string file, bool project)
    {
        var m = Regex.Match(text, @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n?(.*)$", RegexOptions.Singleline);
        if (!m.Success) return null;
        var head = m.Groups[1].Value.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim().Trim('"'), StringComparer.OrdinalIgnoreCase);
        if (!head.TryGetValue("name", out var name) || name == "") return null;
        var tools = head.GetValueOrDefault("tools");
        var readOnly = head.GetValueOrDefault("access") is { } access ? access == "read-only"
                     : tools != null && !Regex.IsMatch(tools, @"\b(Write|Edit|MultiEdit|Bash|NotebookEdit)\b");
        var model = head.GetValueOrDefault("model");
        return new(name, head.GetValueOrDefault("description") ?? "", m.Groups[2].Value.Trim(), model is null or "" or "inherit" ? null : model, readOnly, file, project);
    }

    public static string Save(AgentDef a, string? oldFile)
    {
        Directory.CreateDirectory(Dir);
        var slug = Regex.Replace(a.Name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var file = Path.Combine(Dir, (slug == "" ? "agent" : slug) + ".md");
        var md = new StringBuilder("---\n")
            .Append($"name: {a.Name}\n").Append($"description: {a.Description.Replace('\n', ' ')}\n")
            .Append($"model: {a.Model ?? "inherit"}\n").Append($"access: {(a.ReadOnly ? "read-only" : "full")}\n")
            .Append("---\n\n").Append(a.Instructions.Trim()).Append('\n');
        File.WriteAllText(file, md.ToString());
        if (oldFile != null && !oldFile.Equals(file, StringComparison.OrdinalIgnoreCase) && File.Exists(oldFile)) File.Delete(oldFile);
        return file;
    }

    // ---------- an agent's own memory: notes it keeps between runs, in <data>/agents/<name>.memory.md ----------
    static string MemoryFile(string name) => Path.Combine(Dir, Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') + ".memory.md");
    public static string Memory(string name) { try { return File.Exists(MemoryFile(name)) ? File.ReadAllText(MemoryFile(name)) : ""; } catch { return ""; } }
    static readonly ToolDef NoteTool = new("agent_note", "Save a short note to your own memory for your next runs: what you covered, decisions, where things are, what to check next time. One or two sentences per call.",
        """{"type":"object","properties":{"note":{"type":"string"}},"required":["note"]}""", true);
    static string Note(string name, string note)
    {
        if (note.Trim() == "") return "Nothing to note.";
        Directory.CreateDirectory(Dir);
        var lines = Memory(name).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        lines.Add($"- {DateTime.Now:yyyy-MM-dd}: {note.Trim().ReplaceLineEndings(" ")}");
        File.WriteAllLines(MemoryFile(name), lines.TakeLast(200));   // the newest 200 notes
        return "Noted.";
    }

    // An action an agent needs approved: asked in the chat it works for (the pane sets this), one at a time. Null: refused.
    public static Func<Chat, string, ToolCall, Task<bool>>? Approve;

    // ---------- chains: each agent's report becomes the next one's input ----------
    public static ToolDef ChainTool(List<AgentDef> agents) => new("run_chain",
        "Run agents one after another on a task: the first gets the task, each next one gets the task plus the previous agent's report (for example research → write → review). Returns every step's report. Agents: " +
        string.Join(", ", agents.Prepend(General).Select(a => a.Name)),
        """{"type":"object","properties":{"agents":{"type":"array","items":{"type":"string"},"description":"Agent names in order"},"task":{"type":"string","description":"The complete task, with the context the agents need"}},"required":["agents","task"]}""", true);
    public static async Task<string> RunChain(Chat parent, List<string> names, string task, CancellationToken ct)
    {
        if (names.Count == 0) return "Error: name at least one agent.";
        var sb = new StringBuilder(); string? previous = null;
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            var input = previous == null ? task : $"{task}\n\nThe previous step ({names[i - 1]}) reported:\n{previous}\n\nDo your part of this, building on that report.";
            previous = await Run(parent, name, input, ct);
            sb.Append($"## Step {i + 1} · {name}\n{previous}\n\n");
            if (previous.StartsWith("The agent failed") || previous.StartsWith("No agent called")) break;
        }
        return sb.ToString().TrimEnd();
    }

    // ---------- the tool the chat model uses ----------
    // Always there, like Claude Code's Agent tool: a general agent with the chat's model and tools, plus the user's own specialists.
    public static readonly AgentDef General = new("general", "A capable all-round agent with your tools: research, web searches, reading files, drafting. Use it for independent sub-tasks; several calls in one turn run in parallel.",
        "You are a sub-agent doing one task for a main assistant. Do it fully with the tools, then reply with a complete, self-contained report of what you found or did: the main assistant sees only your final message.", null, false, "", false);
    public static ToolDef Tool(List<AgentDef> agents) => new("delegate_to_agent",
        "Hand a task to an agent. It works on its own with its own tool calls and returns a report. " +
        "Give it everything it needs in task: it doesn't see this conversation. To work in parallel, make several calls in the same turn. Agents:\n" +
        string.Join("\n", agents.Prepend(General).Select(a => $"- {a.Name}: {a.Description}")),
        """{"type":"object","properties":{"agent":{"type":"string","description":"general, or one of the agents listed"},"task":{"type":"string","description":"The complete task, with the context the agent needs"}},"required":["task"]}""", true);

    // The agent's own loop: its instructions as the system prompt, the chat's folder, its own model; read-only agents only
    // get tools that look. Anything that would need the user's approval in the chat is refused (nobody is there to ask).
    public static async Task<string> Run(Chat parent, string name, string task, CancellationToken ct)
    {
        var def = All(parent.Workspace).FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (def == null && name is "" or "general") def = General;
        if (def == null) return $"No agent called {name}. Agents: {string.Join(", ", All(parent.Workspace).Select(a => a.Name).Prepend("general"))}.";
        var s = Planner.S;
        var (prov, model) = def.Model?.Split('/', 2) is [var p, var mo] && s.Providers.Any(x => x.Name == p) ? (p, mo) : (parent.Provider, parent.Model);
        var memory = Memory(def.Name);
        var c = new Chat { Provider = prov, Model = model, Workspace = parent.Workspace, Mode = "autopilot", Permission = parent.Permission, Sandbox = parent.Sandbox,
                           Effort = parent.Effort, Temp = parent.Temp, Incognito = true,
                           System = def.Instructions + (memory == "" ? "" : "\n\nYour notes from earlier runs (newest last):\n" + memory) +
                                    "\n\nUse agent_note to keep anything worth remembering for your next run." };
        c.Messages.Add(new("user", task, At: DateTime.Now));
        var cfg = s.Providers.FirstOrDefault(x => x.Name == prov);
        if (cfg == null) return "The agent's provider isn't set up.";
        var run = new AgentRun { ChatId = parent.Id, Agent = def.Name, Task = task, Model = model ?? prov ?? "" };
        lock (Runs) Runs.Add(run);
        Tell();
        try
        {
            var answer = await Work(def, c, cfg, run, parent, ct);
            run.Result = answer;
            return answer;
        }
        catch (Exception ex)
        {
            run.Failed = true; run.Result = ex is OperationCanceledException ? "Stopped." : ex.Message;
            if (ex is not OperationCanceledException) { Store.Fail($"Agent · {def.Name} · {prov} · {model}", ex.Message); return "The agent failed: " + ex.Message; }
            throw;
        }
        finally { run.Ended = DateTime.Now; run.Doing = run.Failed ? "Stopped" : "Done"; Tell(); }
    }

    static async Task<string> Work(AgentDef def, Chat c, ProviderCfg cfg, AgentRun run, Chat parent, CancellationToken ct)
    {
        if (cfg.Kind == "cli")
        {
            run.Doing = "Working";
            var reply = await Cli.Run(cfg, c, _ => { }, _ => { }, ct);
            run.Tokens = (reply.Usage?.In ?? 0) + (reply.Usage?.Out ?? 0);
            return reply.Text;
        }
        var readOnly = def.ReadOnly || parent.Mode == "plan";
        var tools = Agent.ToolsFor(c).Where(t => t.Name is not ("delegate_to_agent" or "run_chain") && (!readOnly || t.ReadOnly)).Append(NoteTool).ToList();
        var key = Store.Unprotect(cfg.Key);
        var steps = new StringBuilder(); var transcript = new StringBuilder();   // the step list for the header, and each step with what it got back
        for (int step = 1; step <= 25; step++)
        {
            run.Doing = step == 1 ? "Thinking" : "Thinking · step " + step; Tell();
            Api.Reply r;
            for (var attempt = 1; ; attempt++)   // free and shared models hit limits when several agents run at once: wait and retry a few times
            {
                try { r = await Api.Stream(cfg, key, c, tools, _ => { }, null, ct); break; }
                catch (Exception ex) when (attempt <= 3 && ex is not OperationCanceledException && Router.Exhausted(ex.Message))
                {
                    run.Doing = $"Waiting for {cfg.Name} (limit hit, try {attempt} of 3)"; Tell();
                    await Task.Delay(TimeSpan.FromSeconds(attempt * attempt * 3), ct);   // 3, 12, 27 s
                }
            }
            run.Tokens += (r.Usage?.In ?? 0) + (r.Usage?.Out ?? 0); run.Steps = step;
            c.Messages.Add(new("assistant", r.Text, Calls: r.Calls.Count > 0 ? r.Calls : null, Blocks: r.Calls.Count > 0 ? r.Blocks : null));
            if (r.Calls.Count == 0) { run.Transcript = transcript.ToString(); return (steps.Length > 0 ? $"[{def.Name} · {step - 1} steps: {steps.ToString().TrimEnd(',', ' ')}]\n\n" : "") + r.Text; }
            foreach (var call in r.Calls)
            {
                var tool = tools.FirstOrDefault(t => t.Name == call.Name);
                JsonNode args; try { args = JsonNode.Parse(call.Args) ?? new JsonObject(); } catch { args = new JsonObject(); }
                string result;
                if (tool == null) result = $"Error: {call.Name} isn't available to this agent.";
                else if (call.Name == "agent_note") result = Note(def.Name, (string?)args["note"] ?? "");
                else if (Agent.NeedsApproval(parent, tool, args))
                {
                    run.Doing = "Waiting for your OK: " + Agent.Describe(call).Label; Tell();
                    var ok = Approve != null && await Approve(parent, def.Name, call);
                    result = ok ? await Agent.Run(c, call.Name, call.Args, ct) : "The user didn't allow this. Take another approach, or report what you would have done.";
                }
                else result = await Agent.Run(c, call.Name, call.Args, ct);
                steps.Append(Agent.Describe(call).Label).Append(", ");
                transcript.Append("▸ ").Append(Agent.Describe(call).Label).Append('\n').Append(result.Length > 2000 ? result[..2000] + "\n…" : result).Append("\n\n");
                run.Transcript = transcript.ToString();   // so far, for the map while it works
                run.Doing = Agent.Describe(call).Label; Tell();
                c.Messages.Add(new("tool", result, CallId: call.Id, Name: call.Name));
            }
        }
        return $"{def.Name} stopped after 25 steps without finishing. Last steps: {steps}";
    }
}
