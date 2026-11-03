# ZID — Zyzoom Download Manager
## AI Development Instructions / Master Project Plan (v2)

> Master instruction set for any AI coding tool (or human) working on ZID.
> Read sections 0–2 fully before touching code. Use the rest as reference.
> **Do not rewrite the project from scratch unless explicitly instructed.**

---

# 0. How to Use This Document

**Reading order for an AI tool starting a task**

1. Section 1 — Non-negotiable rules (always apply).
2. Section 2 — Project identity and priorities.
3. The section(s) relevant to your task (use the index below).
4. Section 22 — Refactoring workflow (before you change anything).
5. Section 25 — Definition of Done (before you say you are finished).

**Precedence when instructions conflict**

```text
1. Explicit instruction from the project owner in the current task
2. Section 1 (Non-negotiable rules)
3. The rest of this document
4. General best practices
```

**Index**

| Area | Sections |
|---|---|
| Rules, identity, context | 1, 2, 3 |
| Architecture | 4, 5, 6, 7, 8 |
| Reliability (state, recovery, aria2, retry, network) | 9, 10, 11, 12, 13 |
| Features (queue, add, clipboard, browser, history, UI) | 14, 15, 16 |
| Cross-cutting (threading, security, logging, resources) | 17, 18, 19, 20 |
| Quality (testing, refactoring workflow) | 21, 22 |
| Planning (roadmap, priorities, release) | 23, 24, 25 |
| Appendices | A, B, C |

---

# 1. Non-Negotiable Rules

These apply to every change, however small.

1. **Inspect before changing.** Read the relevant files and their callers first.
2. **Preserve existing behavior** unless the task explicitly changes it. Never silently change behavior.
3. **Work incrementally.** One focused change at a time; the project must compile after each step.
4. **The UI owns no business logic.** Forms request operations, display state, and react to events.
5. **Application code depends on `IDownloadEngine`, never directly on aria2.**
6. **The ZID task ID is authoritative.** aria2's GID is an engine detail, never the identity.
7. **Never corrupt or lose user data.** State writes are atomic; partial downloads are never discarded without intent.
8. **Treat all external input as untrusted:** URLs, filenames, headers, cookies, clipboard, browser messages, aria2 responses.
9. **Never swallow important exceptions** (`catch { }` is forbidden except where explicitly justified in a comment).
10. **Never claim something is tested, compiled, or working unless it actually was.** State exactly what you ran and what you did not.
11. **Do not add dependencies, files, or features without justification** (Section 24).
12. **When unsure, ask or choose the option that** reduces complexity, preserves compatibility, improves reliability, and keeps resource usage low.

---

# 2. Project Identity

**Project:** ZID
**Full name:** Zyzoom Download Manager
**Goal:** A lightweight, reliable, professional Windows download manager with an IDM-class user experience, whose architecture is independent of the current download engine.

**Current stack:** C# / .NET Framework, WinForms, **aria2c** as the download engine (via JSON-RPC).

**Priorities, in order**

```text
1. Reliability
2. Correctness
3. Maintainability
4. Performance
5. Lightweight resource usage
6. Professional UX
7. Windows compatibility
8. Extensibility
```

Never sacrifice reliability to add a feature.

**Product philosophy**

> A small, fast, dependable Windows application that does one job extremely well.

```text
RELIABLE   FAST   SIMPLE
```

Success is a **10/10 ZID measured by reliability, performance, usability, maintainability and robustness — not by feature count.**

---

# 3. Current State and the Main Problem

## 3.1 Existing codebase

The project is already substantial. Treat it as an existing production-oriented codebase.

| File | Role (verify by reading, do not assume) |
|---|---|
| `Form1.cs` | Main window — currently ~3,500 lines (as of writing), owns far too much |
| `AriaRpc.cs` | aria2 JSON-RPC client |
| `DownloadItem.cs` | Download model |
| `StateStore.cs` | Persistence |
| `ClipWatcher.cs` | Clipboard monitoring |
| `BrowserIntegrationForm.cs`, `BrowserListForm.cs`, `browsers.cs` | Browser integration UI and detection |
| `Playlist.cs` | Playlist handling |
| `DownloadProgressForm.cs` | Per-download progress window |
| `TaskbarProgress.cs` | Windows taskbar progress |
| `Theme.cs` | Theming |
| `ScriptBridge.cs` | Script/browser bridge |
| `mp4mux.cs` | MP4 muxing |
| `SlimBar.cs`, `neatlink.cs`, `parser.cs` | Compact UI, link handling, parsing |
| `tests.cs` | Existing tests |

## 3.2 The main architectural problem

`Form1.cs` concentrates responsibilities that do not belong in a UI class:

- download lifecycle, queue management, scheduling
- persistence and recovery
- aria2 RPC and retry logic
- formatting and business rules
- browser integration and clipboard logic

**Goal:** `Form1` becomes a thin presentation layer. This is the first major task (Section 22.3).

---

# 4. Target Architecture

```text
UI (MainForm, dialogs)
 |   requests operations / subscribes to events
 v
Application services
 +-- DownloadManager   (lifecycle, state machine, events)
 +-- QueueManager      (concurrency, order, priority)
 +-- RecoveryManager   (startup reconciliation)
 +-- Scheduler         (time rules, later)
 |
 v
Core models (DownloadItem, DownloadState, events)
 |
 v
IDownloadEngine
 +-- Aria2Engine  -->  AriaRpc  -->  aria2c.exe
 +-- (future NativeHttpEngine — NOT to be built unless requested)

Side services (isolated from core):
 StateStore / HistoryStore / SettingsStore
 ClipWatcher, Browser integration, Windows integration
 MediaProcessor (mp4mux)
```

## 4.1 Dependency rules

```text
UI            -> Application services, Core models
Application  -> Core models, IDownloadEngine, Storage interfaces
Engines      -> Core models (engine-specific code stays here)
Storage      -> Core models
Integration  -> Application services (sends requests in; never owns logic)
Core models  -> nothing
```

- Nothing outside `Engines/` may reference aria2 types, GIDs, aria2 option names, or RPC.
- Nothing in `Core/` may reference `System.Windows.Forms`.
- Dependencies point inward/downward only; no cycles.

## 4.2 Good vs. bad

```text
Bad:   Form1 -> aria2 + queue + persistence + retry + recovery

Good:  MainForm -> DownloadManager -> QueueManager
                                   -> IDownloadEngine
                                   -> StateStore
```

---

# 5. Target Project Structure

Move toward this gradually. It is a **direction, not a requirement to create every file immediately.** Do not create empty placeholder files.

```text
ZID/
+-- Program.cs
+-- Core/
|   +-- Download/    DownloadItem, DownloadManager, DownloadState, DownloadEvents
|   +-- Queue/       QueueManager, QueueItem
|   +-- Scheduling/  DownloadScheduler
|   +-- Recovery/    RecoveryManager
+-- Engines/         IDownloadEngine, Aria2Engine, AriaRpc, Aria2ProcessHost
+-- Storage/         StateStore, HistoryStore, SettingsStore
+-- Integration/
|   +-- Clipboard/   ClipWatcher
|   +-- Browser/     BrowserIntegration, BrowserDetector
|   +-- Windows/     WindowsIntegration, TaskbarProgress
+-- Networking/      UrlParser, HeaderManager, NetworkInfo
+-- Media/           Mp4Mux, MediaProcessor
+-- Utilities/       Logger, FileHelper, FormatHelper, ErrorHelper
+-- UI/              MainForm, AddDownloadForm, DownloadProgressForm,
                     PlaylistForm, BrowserListForm, SettingsForm, Theme
```

Moving a file to a new folder/namespace is its own commit: **move first, change behavior later.**

---

# 6. Core Domain

## 6.1 Download identity and model

A download has a stable **ZID `Id`** (e.g. GUID) independent of aria2's GID. The GID is stored as an engine reference and may change (e.g. after recovery or re-adding to aria2).

Candidate fields (inspect the existing `DownloadItem.cs` first; do not duplicate fields):

```text
Identity:    Id, Gid (engine ref)
Source:      Url, Referer, UserAgent, Headers, Cookies
Target:      FileName, SaveDirectory
Progress:    Status, TotalSize, DownloadedSize, Speed, Eta
Lifecycle:   CreatedAt, StartedAt, CompletedAt
Policy:      Priority, Connections, RetryCount, SpeedLimit, Checksum
Error:       ErrorCode, ErrorMessage (technical), UserMessage
```

**Sensitive fields** (Cookies, auth headers) must not be written to logs and should be stored with care (Section 18).

## 6.2 States

Use one centralized `enum DownloadState`. No scattered string comparisons like `"downloading"` or `"complete"`.

```text
Queued       waiting for a free slot
Starting     handed to the engine, not yet transferring
Downloading  actively transferring
Paused       user-paused (or queue-paused)
WaitingNetwork  connectivity lost; will auto-resume
Retrying     waiting for backoff delay before another attempt
Verifying    checksum / integrity check
Completed
Failed       terminal until user retries
Canceled     terminal until user restarts
Removed      deleted from the list
```

Map engine-specific status strings to `DownloadState` in **one place** (inside the engine adapter).

## 6.3 Valid transitions

All state changes go through `DownloadManager`, which rejects invalid transitions and logs them.

| From | Allowed to |
|---|---|
| Queued | Starting, Paused, Canceled, Removed |
| Starting | Downloading, Failed, Retrying, Paused, Canceled |
| Downloading | Paused, WaitingNetwork, Retrying, Verifying, Completed, Failed, Canceled |
| Paused | Queued, Starting, Canceled, Removed |
| WaitingNetwork | Starting, Paused, Canceled, Failed |
| Retrying | Starting, Paused, Canceled, Failed |
| Verifying | Completed, Failed |
| Completed | Removed (and "redownload" creates a **new** item) |
| Failed | Queued, Starting (manual retry), Removed |
| Canceled | Queued, Starting, Removed |

Treat this table as the starting point; adjust to existing behavior where needed, but keep it explicit and enforced in one place.

## 6.4 Events

Prefer events over polling.

```text
DownloadAdded            DownloadStarted        DownloadProgressChanged
DownloadPaused           DownloadResumed        DownloadCompleted
DownloadFailed           DownloadRemoved        DownloadStateChanged
QueueChanged             EngineConnected        EngineDisconnected
```

- Events carry the download `Id` and a snapshot, not live mutable objects shared with the UI.
- Progress events are throttled/coalesced (e.g. ≤ 4–10 UI updates per second), never one per RPC reply.

---

# 7. Download Engine Abstraction

```csharp
public interface IDownloadEngine : IDisposable
{
    // Lifecycle
    Task StartAsync(CancellationToken ct);       // start/attach to the engine
    Task StopAsync(bool graceful);

    // Task operations — all keyed by engine handle returned from AddAsync
    Task<string> AddAsync(EngineRequest request, CancellationToken ct);
    Task PauseAsync(string handle);
    Task ResumeAsync(string handle);
    Task RemoveAsync(string handle, bool deleteFiles);

    // Observation
    Task<EngineTaskStatus> GetStatusAsync(string handle);
    Task<IReadOnlyList<EngineTaskStatus>> GetAllAsync();
    Task SetSpeedLimitAsync(long? bytesPerSecond);

    // Events (engine -> manager)
    event Action<EngineTaskStatus> TaskUpdated;
    event Action<string /*handle*/, EngineError> TaskFailed;
    event Action<string /*handle*/> TaskCompleted;
    event Action Connected;
    event Action Disconnected;
}
```

This is a **sketch**. Adapt the shape to what the existing `AriaRpc`/`Form1` code actually needs, and to the project's C# language version (if `async/await` or `Task` are unavailable for the target framework, use the equivalent callback/thread pattern already used in the project).

Rules:

- `EngineRequest`, `EngineTaskStatus`, and `EngineError` are engine-neutral types in `Core/`.
- `Aria2Engine` translates between these and aria2 RPC; aria2 option names (`max-connection-per-server`, etc.) live only there.
- Do **not** implement a native engine unless explicitly requested.

---

# 8. DownloadManager

The central service controlling downloads.

**Responsibilities**

```text
Add / Start / Pause / Resume / Cancel / Remove / Retry
Own the authoritative list of DownloadItems and enforce the state machine
Coordinate with QueueManager (who runs now) and StateStore (what to persist)
Translate engine events into state changes and ZID events
Apply retry policy (Section 12)
```

**Not its responsibility:** UI, clipboard, browser specifics, media processing, aria2 details.

**Conceptual API** (keep compatible with existing code where practical):

```csharp
Add(request)   Start(id)   Pause(id)   Resume(id)
Cancel(id)     Remove(id, deleteFile)   Retry(id)
```

**Concurrency contract:** all mutations are serialized (single lock or single-threaded dispatcher). Document which thread each event is raised on (Section 17).

---

# 9. Persistence

ZID must survive: application crash, Windows restart, aria2 crash, network interruption, power loss.

**Principles**

- The ZID task ID is authoritative; never depend solely on aria2's GID.
- Persist enough to rebuild any unfinished download: URL, headers/referer/cookies (see Section 18), destination, filename, size, state, retry count, settings, and a **schema version**.
- Persist on meaningful state changes and on a modest timer for progress — not on every progress tick.

## 9.1 Safe state saving

```text
serialize -> state.json.tmp
write + flush to disk (FileStream.Flush(true))
atomic replace  (File.Replace(tmp, state.json, state.json.bak)
                 or File.Move on a first save)
```

- Keep one backup (`state.json.bak`). On load, if `state.json` is missing/corrupt, fall back to `.bak`, log it, and never silently start empty.
- Include a `schemaVersion` and write a migration path before changing the format.
- Serialize off the UI thread; coalesce rapid saves (debounce, e.g. 1–2 s) but flush immediately on shutdown and on terminal state changes.
- Preserve the existing mechanism if it already provides equivalent safety. JSON is fine for ZID; **do not introduce SQLite just to have a database.**

## 9.2 Stores

| Store | Content |
|---|---|
| `StateStore` | Active/unfinished downloads |
| `HistoryStore` | Completed/failed/canceled records (Section 15.4) |
| `SettingsStore` | User settings (versioned) |

Keep loading tolerant: unknown fields are ignored, missing fields get defaults, and one bad record must not prevent the others from loading.

---

# 10. Crash Recovery

**Startup sequence**

```text
Start ZID
 -> load state (fall back to .bak if needed)
 -> validate each task (paths, required fields)
 -> start or attach to aria2 (Section 11)
 -> connect RPC, authenticate
 -> reconcile: persisted tasks  <->  tasks aria2 reports
 -> resume where appropriate
 -> publish recovered state to UI
```

**Reconciliation table**

| ZID state | aria2 reports | Action |
|---|---|---|
| Unfinished | Same task active/waiting | Re-link Gid, adopt state |
| Unfinished | Not known to aria2 | Re-add to aria2 using saved request; aria2 resumes from partial file + control file |
| Unfinished | Completed | Mark Completed (verify file exists/size) |
| Unfinished | Errored | Apply retry policy |
| Completed | Anything | Trust ZID; verify file still exists, flag if missing |
| Unknown to ZID | Active | Log and decide explicitly (adopt or remove); never leave orphans running |

**Partial-file rules**

- A partial file and its aria2 control file (`.aria2`) must be kept together. Never delete one without the other.
- If the partial file exists but the control file is missing, do not assume progress; follow the engine's behavior and verify.
- Check that the server still supports resume (Range) and that the remote file has not changed (size/ETag/Last-Modified where available); if it has, tell the user rather than producing a corrupt file.

The user must never lose a 70%-complete download because ZID crashed. See the Critical Recovery Test (Section 25.2).

---

# 11. aria2 Process Management

ZID owns exactly **one** aria2 process.

**Startup**

```text
ZID -> check for existing ZID-owned aria2 (lock/pid file)
    -> start aria2 (or attach)
    -> wait for RPC readiness (timeout + log)
    -> authenticate with secret
    -> verify version/connection
```

**Unexpected exit**

```text
detect exit -> log (exit code, last output) -> restart with backoff
-> reconnect RPC -> reconcile & resume active downloads (Section 10)
-> if it keeps crashing (e.g. 3 times in 60 s): stop restarting,
   mark engine unavailable, show the user a clear message + Diagnostics
```

**Shutdown**

```text
stop accepting new work -> persist state -> graceful aria2 shutdown
(RPC shutdown, then wait; force-kill only after a timeout) -> exit
```

**Hardening and configuration** (verify each option against the bundled aria2 version):

- Bind RPC to **loopback only**; never listen on all interfaces.
- Use a **random per-session RPC secret** and a free/dynamic port; do not hard-code them.
- Launch via structured `ProcessStartInfo` argument handling; no shell string concatenation.
- Consider `--continue`, `--auto-save-interval`, `--save-session` / `--save-session-interval` as a second safety net alongside ZID's own state (ZID state remains authoritative).
- Consider `--stop-with-process=<ZID pid>` so aria2 never outlives ZID unmanaged; weigh it against the goal of letting aria2 continue across a ZID crash and document the choice in the Decision Log.
- Always clean up stale aria2 processes from a previous crashed ZID that ZID itself started; never kill unrelated aria2 processes the user runs.

---

# 12. Retry System

Centralized in `DownloadManager`/`RetryPolicy`, not in the UI or scattered in handlers.

```text
RetryPolicy
  MaxRetries       (default e.g. 5)
  InitialDelay     (default 2 s)
  MaximumDelay     (default e.g. 60 s)
  Backoff          exponential (x2)
  Jitter           small random +/- to avoid synchronized retries
```

Example schedule: `2 s, 4 s, 8 s, 16 s, 32 s`.

**Classify errors using real error information, not assumptions.**

| Category | Examples | Behavior |
|---|---|---|
| Transient | Timeout, connection reset, DNS failure, HTTP 5xx, 429 (honor `Retry-After`) | Retry with backoff |
| Network down | No connectivity | `WaitingNetwork` (Section 13), not a retry-count consumer |
| Permanent | 404/410, invalid URL, 401/403 (unless credentials/cookies refreshable), invalid destination, permission denied | Fail immediately, clear message |
| Local | Disk full, path too long, file locked | Fail with actionable message; do not retry blindly |
| Integrity | Checksum mismatch | Offer re-download; do not silently retry forever |

- Retry count and next-retry time are persisted so that a restart doesn't reset the policy.
- Manual "Retry" resets the counter.
- Map aria2 error codes to these categories in `Aria2Engine`, in one table.

---

# 13. Network Recovery

```text
Downloading -> network lost -> WaitingNetwork -> network restored -> Resume
```

- Detect connectivity with `NetworkChange` events plus a lightweight probe; do not poll aggressively.
- A temporary outage is **not** a permanent failure. Never burn the retry budget while the network is down.
- On restore, resume with a short randomized stagger (avoid starting 100 downloads in the same instant).
- Handle captive portals / HTML-instead-of-file responses: a "successful" response with an unexpected content type must not be saved as the target file without a check.

---

# 14. Queue, Scheduler, Speed, Connections

## 14.1 Queue Manager

Independent of the UI. First implementation: simple and reliable.

- Maximum simultaneous downloads (configurable)
- Start next when a slot frees
- Pause queue / resume queue
- Priority: `High`, `Normal`, `Low` (FIFO within a priority)
- Move up / move down
- Persisted order; recovered after restart
- Paused-by-user items are never auto-started by the queue

## 14.2 Scheduler (later, P3)

- Start/stop time, queue activation/pause, scheduled speed limits
- Example: `23:00 -> unlimited`, `07:00 -> stop queue`
- Must not block the UI; must handle sleep/resume, clock changes, and being closed at the scheduled time (decide and document the behavior).

## 14.3 Speed management

- Global limit, per-download limit, scheduled limits.
- Do not promise faster downloads without measurement.

## 14.4 Connection strategy

More connections do not always mean more speed (and some servers ban/throttle them).

Default starting points (tune only after benchmarking, Section 21.5):

```text
Small file    -> 1-2
Medium        -> 4
Large         -> 8
Very large    -> 8-16   (check the engine's per-server maximum)
```

- If the server does not support Range requests, use 1 connection.
- Later consider adaptive behavior using throughput, latency, failures, and Range support.

---

# 15. Features

## 15.1 Add Download

Determine before starting: URL, filename, size, content type, Range/resume support.

Show clearly: URL, Filename, Size, Destination, Connections. Put everything else under **Advanced**. Keep the dialog simple.

- Run metadata probing asynchronously with a timeout; the dialog must open instantly and never freeze.
- Allow adding even when probing fails (some servers reject HEAD).

## 15.2 Duplicate detection

Before creating a download, check: same URL, already active, already completed, same destination, existing local file.

Offer: **Resume existing / Download again (auto-rename) / Replace / Cancel.**
Normalize URLs before comparing (case of host, default ports, fragments, trailing slash where safe). Never overwrite a file without explicit confirmation.

## 15.3 Clipboard integration

```text
Clipboard changed -> is URL? -> supported protocol? -> duplicate?
 -> supported type/extension? -> ask user / automatic (per settings)
```

Settings: Monitor clipboard · Ask before downloading · Automatic download · Extension filters.
Do not intercept every URL aggressively. Debounce clipboard events; ignore content ZID itself placed on the clipboard.

## 15.4 Browser integration

Isolated from the core. Information received: URL, cookies, headers, Referer, User-Agent.

```text
Browser -> ZID integration -> DownloadRequest -> DownloadManager
```

- No browser-specific code in `MainForm`.
- Treat all browser-supplied data as untrusted; authenticate the bridge (local-only, token or equivalent) so that other local programs/web pages cannot inject downloads silently.

## 15.5 History

Separate views: Active · Downloading · Completed · Failed · Canceled.
Survives restart. Actions: Open file · Open folder · Copy URL · Redownload · Delete history entry (with or without deleting the file — always explicit).
Handle missing files gracefully (show "file moved or deleted").

## 15.6 Media processing

Keep `mp4mux.cs` and future media features outside the core.

```text
Download completed -> MediaProcessor -> optional mux/process
```

`DownloadManager` must not know about media specifics; it may only raise `DownloadCompleted`, and a subscriber decides what to do.

## 15.7 Download properties

Tabs: **General · Progress · Network · Advanced**.
Fields: URL, File, Folder, Size, Downloaded, Remaining, Speed, ETA, Connections, User-Agent, Referer, Headers, Cookies (masked), Priority, Retry, Checksum, Engine options.
Do not expose engine internals to normal users.

## 15.8 Checksum verification (P3)

Optional per-download checksum (e.g. SHA-256); state `Verifying` while computing; computed off the UI thread with progress; mismatch handled per Section 12.

---

# 16. UI Principles

The UI should be clean, fast, understandable, compact, responsive, IDM-inspired where appropriate, and not overloaded.

Main list columns: **Filename · Size · Status · Progress · Speed · ETA**. Advanced actions live in context menus and Properties.

**UI error messages**

Separate the technical error from the user message. Never show raw exceptions unless the user opens Diagnostics.

```text
Technical:  RPC connection refused on port 59558
User:       ZID could not connect to the download engine.
            [Retry] [Diagnostics]
```

Bad: `System.Net.WebException: ...`

**Windows integration**

Use the existing `TaskbarProgress.cs`:

```text
Downloading -> progress   Paused -> paused indication
Error -> error indication Completed -> clear progress
```

Notifications, startup integration, file associations, and shell integration come only after the core is stable.

**UI performance:** use virtualized or owner-drawn lists if the list grows large; update only changed rows; never rebuild the whole list on each tick.

---

# 17. Threading and Events

The UI must remain responsive. Never run long operations on the UI thread:

```text
aria2 RPC calls · file operations · checksum · process startup
network operations · large state serialization
```

Rules:

- Background code never touches controls directly; marshal via `Control.BeginInvoke` / `SynchronizationContext`.
- Prefer `BeginInvoke` over `Invoke` for high-frequency updates to avoid deadlocks.
- Document, per event, which thread raises it.
- Guard shared state (download list, queue) with one clear mechanism; avoid fine-grained lock mazes.
- Use `CancellationToken` for cancellable operations; dispose timers, processes, streams, HTTP clients, and event subscriptions.
- Watch for races between: UI, DownloadManager, aria2 events, StateStore, QueueManager.
- Never block on `.Result` / `.Wait()` on the UI thread.
- Use timers sparingly; one coordinated poll loop (only while there are active tasks) beats many independent timers.

---

# 18. Security and File-System Safety

Validate: URLs, paths, filenames, headers, cookies, external-process arguments.

**URLs**

- Allow only supported schemes (`http`, `https`, `ftp`, others only intentionally).
- Be careful with `file://`, `javascript:`, and URLs with embedded credentials (strip/mask in logs).

**Filenames and paths** (a remote filename is never trusted)

- Strip directory components; prevent path traversal (`..`, absolute paths, alternate separators).
- Reject/replace invalid Windows characters and reserved names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`), trailing dots/spaces.
- Handle long paths (>260 chars) deliberately; truncate safely, keep the extension.
- Normalize the final path and verify it stays inside the chosen destination.
- Check free disk space before starting; handle disk-full mid-download.
- Do not auto-execute downloaded files. Consider applying the Windows "Mark of the Web" where appropriate.

**Process and RPC**

- Structured `ProcessStartInfo` arguments; avoid shell execution.
- RPC on loopback with a random secret (Section 11).

**Secrets**

- Cookies, `Authorization` headers, and tokens: never logged, masked in UI, and stored only as long as needed (consider DPAPI protection for persisted secrets).

**Browser bridge/ScriptBridge**

- Authenticate and validate every message; rate-limit; reject oversized payloads.

A security review is a release criterion (Section 25.3).

---

# 19. Logging and Diagnostics

Centralized `Logger` with levels `DEBUG`, `INFO`, `WARN`, `ERROR`.

```text
[19:32:11] INFO  Download started   id=3f2a…
[19:32:13] INFO  aria2 connected
[19:32:20] WARN  Connection lost
[19:32:23] INFO  Reconnecting
[19:32:24] INFO  Download resumed
```

- Include the download `Id` in every download-related line.
- Asynchronous, buffered writes; rotation by size/count; no noticeable performance cost.
- Never log secrets.
- Provide a **Diagnostics** view or an "Open log folder" action, plus a "copy diagnostics" option (versions, engine status, recent errors).
- Replace empty `catch` blocks with logged handling.

---

# 20. Resources, Compatibility, Portability

## 20.1 Resource usage

- Near-zero CPU while idle; low RAM; no busy loops or excessive polling.
- Event-driven updates where possible; efficient timers.
- No heavyweight dependencies without a strong reason.
- Define measurable budgets and track them (e.g. idle CPU, idle RAM with N downloads listed, startup time). Record the baseline before major refactors.

## 20.2 Compatibility

Preserve the project's Windows/.NET Framework compatibility. Before changing framework requirements, verify: compiler availability, runtime availability, API compatibility, deployment impact. Do not use APIs or language features that silently break the target environment.

## 20.3 Portable mode

Prefer a portable layout where practical:

```text
ZID/
+-- ZID.exe
+-- aria2c.exe
+-- config/
+-- downloads/
+-- logs/
+-- tools/
```

- No installer required for basic operation; an installer can come later.
- Handle a read-only install directory (fall back to `%AppData%` with a clear notice).
- Verify the bundled `aria2c.exe` version at startup and log it.

---

# 21. Testing

## 21.1 Strategy

```text
Characterization tests  -> lock in CURRENT behavior before refactoring
Unit tests              -> parser, state machine, retry policy, queue, filename sanitizer
Integration tests       -> DownloadManager + fake engine; Aria2Engine + real aria2
Failure-injection tests -> kill aria2, drop network, corrupt state file, fill disk
Manual regression list  -> UI flows that are hard to automate
Stress & benchmarks     -> Sections 21.4, 21.5
```

**Make the core testable:** because the manager talks to `IDownloadEngine`, write a `FakeEngine` that can simulate progress, failures, and disconnects deterministically. Most reliability logic should be testable without aria2 or a network.

Build a **local test server** (supports Range, slow responses, mid-stream disconnects, 5xx, redirects, `Content-Disposition`) so network scenarios are reproducible.

## 21.2 Coverage checklist

**Basic:** Add · Start · Pause · Resume · Cancel · Remove
**Persistence:** restart app → recover download · corrupt state file → fall back to `.bak`
**Failure:** network loss · aria2 crash · server failure · disk full · invalid URL · 404 · 429 · redirect loops
**Queue:** 1 / 10 / 100 downloads · priority · move up/down · max concurrent respected
**Duplicates:** same URL · same file · existing file
**Parser:** URLs · filenames (unicode, reserved names, traversal attempts) · extensions · headers
**Security:** path traversal · invalid filenames · malformed browser messages

Every fixed bug should receive a regression test where practical.

## 21.3 State-machine tests

Test each row of the transition table (valid transitions succeed, invalid ones are rejected and logged).

## 21.4 Stress testing

```text
Downloads:  1 · 10 · 50 · 100
Sizes:      100 MB · 1 GB · 10 GB · 100 GB   (where practical)
Servers:    fast · slow · unstable · Range-supported · non-Range
Long runs:  many hours with repeated pause/resume; check memory and handle leaks
```

## 21.5 Benchmarking

Compare when appropriate: ZID · aria2c · curl · wget · IDM.
Use controlled, repeated runs. Measure: average speed, peak speed, time to completion, CPU, RAM, connection count.
Test connections: 1 · 2 · 4 · 8 · 16.
**Do not optimize on intuition or claim performance improvements without benchmarks.**

---

# 22. AI Refactoring Workflow

## 22.1 Per-change procedure

1. **Inspect** — read the relevant files.
2. **Map dependencies** — who calls this? what does it call? what state does it modify? what UI depends on it?
3. **Plan** — state the intended change briefly, including risks.
4. **Protect** — add/confirm a characterization test or a manual test script for the behavior you are about to move.
5. **Implement** — focused change; separate "move code" from "change behavior".
6. **Compile.**
7. **Test** — run relevant tests or provide exact manual steps; say what you could not run.
8. **Review** — regressions, duplication, resource leaks, threading, race conditions, null handling, invalid state transitions, swallowed exceptions.
9. **Report** — use the change-report template (Appendix C).

Never make a large blind rewrite. Prefer many small, individually-verifiable steps.

## 22.2 Code quality rules

- Preserve behavior; avoid unnecessary rewrites and duplicate logic.
- Meaningful names; focused methods and classes; no hidden global state.
- Avoid premature abstraction and premature optimization.
- Comment the *why* of non-obvious behavior (especially around aria2 quirks and races).
- Before writing a helper, search the project for an existing one.
- Before adding a dependency, explain why it is necessary and what it costs (size, licensing, framework compatibility).
- Maintain backward compatibility of persisted state and settings; migrate rather than break.

## 22.3 First major task: refactor `Form1.cs` into a thin UI

Do **not** rewrite all functionality at once. Recommended sequence (each step compiles and preserves behavior):

```text
Step 0  Baseline: build, run the manual regression list, record idle CPU/RAM
Step 1  Inventory Form1: list methods/fields and group by responsibility
        (lifecycle, queue, persistence, RPC, retry, formatting, browser,
         clipboard, UI-only)
Step 2  Extract pure helpers first (formatting, parsing, filename logic)
        -> Utilities/ or Networking/ (lowest risk, easy to test)
Step 3  Introduce DownloadState enum and replace string statuses
Step 4  Introduce events + a DownloadManager that WRAPS existing logic
        (move code, do not redesign it yet)
Step 5  Extract QueueManager
Step 6  Route persistence through StateStore via DownloadManager
Step 7  Introduce IDownloadEngine + Aria2Engine over the existing AriaRpc
Step 8  Move retry, recovery and aria2 process management into services
Step 9  Reduce Form1 to presentation; rename to MainForm
Step 10 Regression tests + Critical Recovery Test
```

Visual goal:

```text
Current:  Form1 (~3,500 lines, everything)

Target:   MainForm       presentation only
          DownloadManager lifecycle
          QueueManager    queue
          StateStore      persistence
          Aria2Engine     engine adapter
          AriaRpc         RPC
```

Each step should be a separate commit/PR that the owner can review and revert independently.

---

# 23. Roadmap

Each phase has an exit criterion; do not start the next phase's work while P0/P1 problems remain.

| Phase | Focus | Exit criteria |
|---|---|---|
| 0 | Baseline & safety net | Builds cleanly; manual regression list written; characterization tests for core flows; metrics recorded |
| 1 | Core extraction | `DownloadState`, `DownloadManager`, events, `QueueManager` in place; Form1 sharply reduced; behavior unchanged |
| 2 | Engine abstraction | `IDownloadEngine` + `Aria2Engine`; no aria2 references outside `Engines/`; `FakeEngine` tests pass |
| 3 | Reliability | Atomic state saving + backup; recovery/reconciliation; aria2 crash recovery; retry policy; network recovery; Critical Recovery Test passes repeatedly |
| 4 | UX & integration | Add Download, duplicates, history, properties, clipboard and browser integration cleaned up behind services |
| 5 | Hardening | Security review, logging/diagnostics, stress tests, benchmarks, portable build, clean-install test |
| 6 | Advanced (post-1.0 candidates) | Scheduler, adaptive connections, checksum verification, deeper Windows integration |

---

# 24. Priorities and Feature Discipline

## 24.1 Priority system

**P0 — Critical:** crashes, data loss, corrupted state, failed recovery, broken downloads, security problems
**P1 — Core:** DownloadManager, engine abstraction, persistence, queue, retry, recovery
**P2 — UX:** Add Download, properties, history, browser integration, clipboard, UI improvements
**P3 — Advanced:** scheduler, adaptive connections, checksum verification, advanced Windows integration
**P4 — Nice-to-have:** anything not affecting the core download experience

Never work on P4 while P0/P1 problems remain.

## 24.2 No feature creep

A feature must justify itself:

1. Does it improve download management?
2. Does it improve reliability?
3. Does it improve UX?
4. Does it justify its complexity?
5. Does it preserve ZID's lightweight nature?

If not, postpone it. Record postponed ideas in the backlog instead of implementing them "while you're there".

---

# 25. Definition of Done, Recovery Test, Release Criteria

## 25.1 Definition of Done

A feature is **not** finished merely because it compiles.

```text
[ ] Implemented
[ ] Integrated (actually reachable and used)
[ ] Compiles without new warnings
[ ] Existing functionality still works
[ ] Error cases handled (with user-friendly messages)
[ ] Relevant tests added/updated and passing
[ ] State transitions valid; persistence/migration considered
[ ] UI updated if necessary; UI thread not blocked
[ ] Logging added where appropriate (no secrets)
[ ] No obvious resource leaks (handles, timers, processes, subscriptions)
[ ] No unnecessary duplication or dead code
[ ] Comments/documentation updated where needed
[ ] Change report written, including what was NOT tested
```

## 25.2 Critical Recovery Test

Must pass reliably before ZID 1.0:

```text
1. Start a large download (use the local Range-capable test server).
2. Let it reach ~70%.
3. Kill ZID abruptly (process kill, not a graceful exit).
4. Restart ZID.
5. Recover the task.
6. Reconnect/restart aria2 if necessary.
7. Resume the download.
8. Verify already-downloaded data was preserved (no restart from 0%).
9. Complete the download.
10. Verify the final file (size + checksum against the source).
```

Repeat many times, and with variations:

```text
- kill aria2 instead of ZID
- kill both
- kill during a state-file write
- power-loss simulation (kill at random moments)
- network drop during recovery
- 10 and 50 simultaneous downloads
```

## 25.3 ZID 1.0 release criteria

```text
[ ] Core architecture separated
[ ] Form1 significantly reduced (UI only)
[ ] DownloadManager implemented
[ ] IDownloadEngine and Aria2Engine implemented
[ ] State recovery reliable
[ ] aria2 crash recovery reliable
[ ] Network recovery reliable
[ ] Retry system reliable
[ ] Queue reliable
[ ] Duplicate handling reliable
[ ] Clipboard reliable
[ ] Browser integration reliable (and authenticated)
[ ] History reliable
[ ] UI responsive under load
[ ] Logging and diagnostics implemented
[ ] Security review completed
[ ] Stress testing completed
[ ] Performance benchmark completed
[ ] Portable build works
[ ] Clean installation works
[ ] Existing functionality preserved
[ ] Critical Recovery Test passes repeatedly
```

---

# Appendix A — AI Working Style (Summary)

**Do**

- inspect first · preserve existing functionality · work incrementally
- compile after meaningful changes · test critical paths
- explain architectural decisions · reuse existing code
- keep compatibility in mind · prioritize reliability
- ask when requirements are ambiguous and the risk is high

**Do not**

- rewrite the project without permission
- delete working features
- replace the architecture because another pattern is fashionable
- add dependencies unnecessarily or create duplicate utilities
- put business logic into forms
- silently change behavior
- claim something is tested when it was not

---

# Appendix B — Decision Log (template)

Keep a short log (e.g. `docs/decisions.md`) so future AI sessions don't re-litigate settled choices.

```text
## D-001: <title>
Date:
Status: Proposed / Accepted / Superseded
Context: <problem, constraints>
Options considered:
Decision:
Consequences / trade-offs:
```

Seed entries to decide and record: `--stop-with-process` vs. aria2 surviving a ZID crash · state file schema/versioning · retry defaults · max concurrent downloads default · how cookies are stored · scheduler behavior at missed times.

---

# Appendix C — Change Report (template)

Every AI-produced change should end with:

```text
Summary:        what changed and why (1-3 sentences)
Files touched:  list
Behavior change: none / describe
Build:          succeeded / failed / not run
Tests run:      list, with results
Not tested:     what could not be verified, and why
Risks:          what could regress
Follow-ups:     next recommended step
```

---

# Appendix D — Final AI Instruction

You are working on an existing software project named **ZID — Zyzoom Download Manager**. Treat the repository as a real software product. Your objective is to move ZID toward a stable, maintainable, professional 1.0 release.

Always: inspect before changing · preserve existing functionality · make incremental changes · keep the UI separate from business logic · keep the download engine abstract · make persistence and recovery reliable · test changes honestly · avoid unnecessary dependencies and rewrites · prefer simple, robust solutions over clever ones.

When uncertain, choose the solution that:

```text
reduces complexity
+ preserves compatibility
+ improves reliability
+ keeps resource usage low
```

The ultimate goal is a **10/10 ZID** — measured by reliability, performance, usability, maintainability, and robustness, not by the number of features.
