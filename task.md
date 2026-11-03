# ZID improvement tasks — batch 2

Previous batch: tasks 1–15 are complete. Code signing (old task 16) is still blocked on an
Authenticode certificate.

Ordered easy -> hard. One task at a time; build (and tests where useful) after each.
Status: `[ ]` todo, `[x]` done, `[!]` blocked/needs input.
Task titles keep the original idea number in brackets so both lists stay readable.

## Easy

- [x] 1. UTF-16 HTML/error-page detection [idea 5]
      `Util.LooksLikeWebPage` only checks ASCII/UTF-8 starts. Decode a UTF-16 LE/BE BOM
      before the `<html`/`<body` sniff so a portal page saved as media is still rejected.
      Add tests for UTF-16 LE, UTF-16 BE, and a real UTF-16 text file.
      Files: `src/Utilities.cs`, `src/tests.cs`.

- [x] 2. Update source fallback [idea 6]
      Some networks block `raw.githubusercontent.com`. Try a second mirror URL before
      reporting "update check failed"; keep the same timeout budget per attempt.
      Files: `src/UpdateChecker.cs`, `src/tests.cs`.

- [x] 3. Release notes in the update prompt [idea 7]
      Read an optional `notes` string from `version.json` and show it in the
      "update available" dialog. Missing/empty notes keeps today's text exactly.
      Add the language keys and escape the text.
      Files: `src/UpdateChecker.cs`, `src/MainForm.cs`, `ui.html`, `lang/en.txt`.

## Medium

- [x] 4. Speed-limit schedule (day/night) [idea 4]
      Add settings for a temporary speed cap and a time window, e.g. 200 KB/s between
      09:00 and 23:00, unlimited outside it. The scheduler tick applies/removes the cap
      on active downloads without restarting them; an empty window keeps today's global
      limit. Persist and clamp the values.
      Files: `src/SettingsStore.cs`, `src/MainForm.cs`, `ui.html`, `lang/en.txt`,
      `src/tests.cs`.

- [x] 5. Clean orphan partial files [idea 10]
      After "Clean history", optionally remove `.aria2` control files and partial files
      that no remaining row owns. Only touch files under the download folder, never a
      path still referenced by state, and make it an explicit checkbox in the clean
      confirmation.
      Files: `src/MainForm.cs`, `src/HistoryService.cs`, `ui.html`, `src/tests.cs`.

- [x] 6. Per-item "Download later" [idea 8]
      Add an optional start time to a single row, persisted in state. The queue skips
      that row until the time passes; editing it to empty clears the delay. A delayed
      row must not consume a concurrency slot.
      Files: `src/DownloadItem.cs`, `src/StateStore.cs`, `src/QueueManager.cs`,
      `src/MainForm.cs`, `ui.html`, `src/tests.cs`.

## Hard

- [x] 7. Optional per-host credentials with DPAPI [idea 13]
      Let the credential dialog offer "Remember for this site". Store only the username
      and password per host, encrypted with `Aria.Protect`/`Unprotect` (DPAPI, current
      user). Fill the prompt automatically, never write secrets to the log, and add a
      "Forget saved credentials" action. The default stays "do not remember".
      Files: `src/SettingsStore.cs`, `src/CredentialsForm.cs`, `src/MainForm.cs`,
      `src/AriaRpc.cs`, `ui.html`, `lang/en.txt`, `src/tests.cs`.

## Blocked / external

- [!] 9. Code-sign the exe and setup (carried over)
      Needs an Authenticode certificate. Optional signing hook can be wired now and
      activated later with `ZID_SIGN_PFX` / `ZID_SIGN_THUMBPRINT`.
