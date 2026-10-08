# DungeonGenerator

DungeonGenerator can use either an OpenAI-compatible model hosted locally or an eligible ChatGPT plan.

## Run an adventure

Every run requires the name of one direct subdirectory beneath `Workspace`:

```bash
dotnet run -- "Dwarven Tomb"
```

The equivalent named form is:

```bash
dotnet run -- --adventure "Dwarven Tomb"
```

The directory is created when it does not exist. Absolute paths, nested paths, `.` and `..` are rejected. Quote names that contain spaces.

Use this layout:

```text
Workspace/
├── ADVENTURE_GENERATION_TEMPLATE.md   # shared by every adventure
├── SRD_CC_v5.2.1.pdf                  # another shared input, if supplied
├── Dwarven Tomb/                      # selected on the command line
│   ├── Dwarven Tomb.jpg               # specific to this adventure
│   ├── ADVENTURE.md                   # generated output
│   └── .dungeon-generator-state.json  # recovery state for this adventure
└── Another Adventure/
    └── map.png
```

The file tools expose root-level shared files and only the selected adventure directory. Other adventure directories are hidden and rejected. Reads can use shared root files or selected-adventure files; every write is forced into the selected adventure directory. Passing a bare output name such as `ADVENTURE.md` is safe because it is automatically resolved beneath the selected adventure.

Files from versions of DungeonGenerator that predate adventure directories are not moved automatically. Move any existing root-level map or `ADVENTURE.md` into the appropriate adventure directory before the first new-style run. A legacy root `ADVENTURE.md` is hidden from file discovery so it cannot be mistaken for the current artifact.

## Choose the generator

Edit `appsettings.json` and set `Generator.Provider` to one of:

- `Local` — uses `Generator.Local.Endpoint` and `Generator.Local.Model`.
- `ChatGPT` — uses `Generator.ChatGPT.Model`, `Generator.ChatGPT.ThinkingLevel`, and your ChatGPT plan.

For example:

```json
{
  "Generator": {
    "Provider": "ChatGPT",
    "Local": {
      "Endpoint": "http://127.0.0.1:1234/v1/",
      "Model": "unsloth/muse-glimmer-30b"
    },
    "ChatGPT": {
      "Model": "gpt-6.1-sol",
      "ThinkingLevel": "medium"
    }
  }
}
```

On the first ChatGPT run, the program opens **Continue with ChatGPT** in the system browser. Sign in, select the appropriate account and workspace, and approve ChatGPT plan usage. No API key is required.

The OAuth connection is saved outside the repository in the operating system's application-data directory. On Unix systems, the directory and credential file are restricted to the current user. Access tokens are refreshed automatically; the browser opens again if reauthorization is required.

The comments in `appsettings.json` list the supported flagship models, including `gpt-5.6-sol`, and the thinking levels each supports. Higher thinking levels generally trade additional latency and token usage for more reasoning. The configured ChatGPT model must be available to the signed-in account. At startup, the program checks the account-specific model catalog and reports the available model slugs if the configured model cannot be used.

## Live status output

Agent text is streamed to the console as it is generated. The prompt requires concise `[Status]` messages before tool calls and `[Result]` messages after them. Tool calls request a `reason` argument, which is recorded when supplied; omission does not prevent the operation from running. Agent runs log a unique ID, label, prompt size, session type, elapsed time, response size, cancellation, and failures.

## Incremental Markdown output

The agent reads the shared `Workspace/ADVENTURE_GENERATION_TEMPLATE.md`, creates `Workspace/<adventure-directory>/ADVENTURE.md` after its initial map analysis, and appends completed Markdown sections as it works. `WriteFileAsync` creates or deliberately replaces the document; `AppendFileAsync` adds durable section checkpoints while enforcing the selected-adventure boundary, Markdown-extension, per-append, and total-file size limits.

Startup source acquisition is owned by the primary session. Duplicate full-map and template deliveries are suppressed for the lifetime of the process. Delegation is blocked until the first non-empty selected-adventure `ADVENTURE.md` checkpoint exists, recursive and duplicate delegations are rejected, and each run is limited to four delegated sessions. Child prompts must receive a source brief so they can work without repeating template or map discovery.

Writes and appends are staged in a temporary file and atomically moved into place, so cancellation or a process failure cannot leave the last valid artifact half-overwritten. Each successful artifact write updates `Workspace/<adventure-directory>/.dungeon-generator-state.json` with run status and checkpoint metadata isolated from every other adventure. The first Ctrl+C requests graceful cancellation, waits for active operations to unwind, records the run as cancelled, disposes generator resources, and exits without an exception stack trace using exit code 130. Pressing Ctrl+C again terminates immediately and leaves the run detectable as interrupted. A later launch for the same adventure directory detects completed, failed, cancelled, abruptly interrupted, missing-artifact, and untracked-artifact states. It then instructs the agent to inspect existing Markdown, preserve complete sections, resume at the next missing template section, or rebuild only when the partial document is unsafe to append to.
