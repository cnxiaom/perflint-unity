# Changelog

User-facing changes to PerfLint for Unity. This project follows [Semantic Versioning](https://semver.org/).

## [1.7.1] — 2026-08-13

### Added
- **An AI agent connected to your open editor can now edit C# and shaders under compile verification.** `perflint_apply_verified` backs the file up first, applies the edit, and compiles it; if the edit fails to compile the file is restored byte-for-byte. The edit can be a targeted old-text/new-text replacement — the preferred form, physically incapable of truncating a file — or a whole-file rewrite. Shaders are verified by actively compiling the shader's passes and return their verdict immediately; C# reports through `perflint_verify_status`, because a successful compile reloads the domain. Free on both tiers: the model is yours, so no AI credit is spent.

### Changed
- **The keyboard shortcuts moved to `Ctrl/Cmd + Alt`.** Scan Project is now `Ctrl/Cmd + Alt + L` and the Runtime Profiler `Ctrl/Cmd + Alt + M`. The old bindings collided with commands you already had: `Ctrl+Shift+L` is Unity's own Edit ▸ Lighting ▸ Generate Lighting, and `Ctrl+Shift+K` is ProBuilder's New Shape Toggle — pressing either opened a "binding conflicts with multiple commands" dialog and ran neither. Both are rebindable in Edit ▸ Shortcuts.
- **Your own API key now works on the free tier.** Bringing your own key used to require Pro. It is self-funded and goes straight to your provider — our service never sees the request — so there is nothing to meter and no reason to charge for it: unlimited AI Fix and Explain on either tier, with no credits consumed.
- **Rule ids in agent commands accept the short form.** `GC003` finds `PERF.GC003` in `perflint_list_findings` and `perflint_fix`; a rule filter that matches nothing now names the closest rule ids in the scan instead of returning an empty list.
- **The shader-variant screen now shows you where it is going.** Warm-up — the recommended, safe destination of the whole panel — is marked in green, and on-device capture in blue, instead of five identical grey blocks with nothing to tell them apart.
- **Cards are visible as cards.** Every panel's blocks sat 1.13:1 against the window behind them — close enough to nothing that a screen full of cards read as one flat sheet of grey. They now carry a slightly lifted fill and a hairline edge, on both the dark and the light editor skin. Severity-coloured cards are unchanged.

### Fixed
- **Measuring with Deep Profile on no longer slows the editor to a crawl.** Deep Profile stretches every frame, and the stutter detector read those stretched frames as stutters — so it chased every single one, rebuilding call trees on the editor's update loop while the measurement was running, which stretched the next frame further still. It now judges a stutter against the run's own baseline whenever Deep Profile is on, and the capture that follows is capped per frame. What Deep Profile is for is unchanged: you still get the exact method names, and a real freeze is still caught.
- **The health score is described where it actually appears.** The welcome screen and the README said a scan hands you a 0–100 project health score; the panel stopped showing one a while back. The score is unchanged and still free — it lives in the exported HTML report, the CLI output, and the JSON your agent reads.
- **The README no longer claims one-click fixes can be undone with `Edit ▸ Undo`.** They cannot: import-setting changes are written through the asset importer, which Unity's undo stack does not record. The product has always said so at the moment you apply one — but the README said the opposite, in four places across both languages. Commit to version control first; the preview shows exactly what each fix will change before it runs.
- **The shader-variant panel now reads like the rest of PerfLint.** Its body text and footnotes were a size or two below the floor the other panels hold, the capture count was set larger than any window title in the product, and the how-to and the record buttons sat outside the cards everything else lives in — so the screen had two different left edges. It also offered two "primary" buttons at once; Save is the one now.
- **Comments AI Migrate and AI Fix add to your code are written in English.** A migration could come back with Chinese comments in it whatever language the interface was set to. Comments already in the file are still left exactly as they were.
- **A complete migration is no longer rejected as truncated because the file ends with a `#pragma` or a comment.** A rewrite that closed with `#pragma warning restore` — or `#endif`, `#endregion`, a trailing comment — was refused every time it was generated. Genuinely cut-off output is still refused.
- **Regenerating tells you which attempt you are on.** When a second attempt failed for the same reason as the first, the panel was identical before and after the click, so the button looked dead.
- **Compile errors that are already in the Console are now reported per file.** Opening a project that was broken before PerfLint loaded gave you one "details pending" finding asking you to trigger a recompile — while the errors sat in the Console. They are now read from there instead, each file with AI Migrate on it.
- **An AI change waiting for compile verification now survives closing the editor.** The pending list and its backups only lasted for the editor session, so quitting before the verifying compile left the change in place, unverified, with nothing to roll back to — and a project that doesn't compile can wait a long time for that verdict. Both now live under `Library/`, and the check resumes on the next start.
- **AI Migrate is told what the replacement API actually contains.** Asked to migrate off `GetInstanceID()`, the model invented three `EntityId` members in a row on one file; each was caught and rolled back, but the round trips were wasted. The exact member list is now sent with the request.
- **A restored report no longer shows the compile state it was saved with.** Compile findings are re-derived when the panel is restored and whenever you click back into it, so recompiling — or fixing the file — is reflected without a full rescan. Everything else in the restored report is unchanged.

## [1.6.0] — 2026-08-09

### Added
- **Autopilot — a guided round instead of a list.** Ranked by what's actually limiting your game, applying the reversible fixes you tick, and measuring before and after. A change smaller than the drift PerfLint has measured on your machine is reported as "no measurable change", not as a win.
- **A measurement can be aimed at the scene your game loads on its own.** Projects that boot through an Init scene and play their way to a level were being measured against Init. Set "boot from" and "measure", and nothing is sampled until the game has loaded the second one — play to it if it takes a menu. A strip across the top of the Game view carries the phase, the repetition and the time left, for the minutes when the panel is behind the game.
- **Play Mode results are kept.** The Runtime Profiler reopens on your last session, its findings join the main list, and the HTML report can carry them.
- **A Resources file that ships far more than its own size is now reported, one finding per file.** Everything under a Resources folder enters the build along with everything it references: on one project a **73 KB prefab shipped 66.4 MB**. Each finding names the file you can act on, and reports memory as well as on-disk size — they disagree in both directions.
- **An Addressable entry the editor cannot pack is named before a build runs into it** — no importable form, or square brackets in the address — with the group it sits in and the three ways out.
- **Three kinds of Unity 6 upgrade blocker are named before you hit them.** Custom render passes that compile perfectly and draw nothing under Render Graph (one project had eight); `[SerializeField]` on an enum or a method, which older editors accept silently and Unity 6 rejects with CS0592; and three removed or changed APIs, each with its exact replacement.
- **The main panel can take you to each asset a finding names**, not just the first — and those rows now survive a restart and reach the exported report.
- **The interface can be read in Chinese**, and a Chinese README ships with the package.

### Changed
- **Recording a baseline now includes the calibration that has to follow it** — one press, nothing to click in the middle. A baseline that was cancelled or refused stops there.
- **PerfLint now looks like one tool and follows your editor theme.** Autopilot redrawn, the dialogs match the panels that led you to them, the licence panel states what your tier actually gets you, and the target is now just a frame rate.

### Fixed
- **A measurement is filed under the scene it was actually taken in.** The warmup is longer than most games take to leave their entry scene, so a run started there sampled the level and was recorded as the entry scene's — then thrown away the moment you set the level as the target. When every repetition sampled cleanly in one other scene, the measurement is kept and re-filed under it; when sampling straddled a scene load, nothing is relabelled and no baseline can be built from it.
- **A figure that was moving while it was being sampled can no longer be used to claim a change.** Repeating a measurement does not establish that it described a steady state — three repetitions that each pan across the same scene land in the same average every time. Measured on one project, camera moving against camera parked: **30–40% apart on vertices and draw calls**. Each figure's movement inside its own window now widens that figure's noise band, per figure rather than per measurement.
- **Frame-rate and allocation figures judge your game, not the editor hosting it** — including per-frame GC without Deep Profile, allocations inside packages, and a capped frame rate no longer read as "the CPU is spending your frame".
- **A measurement of the scene you set as the target now counts**, instead of being reported as taken somewhere else; and playing your game between two measurements is no longer banked as machine drift.
- **Cancelling a measurement no longer leaves VSync switched off**, and comparisons across different graphics APIs are refused rather than reported.
- **Duplicate assets: the reclaimable figure counts only the copies that can actually be removed.** The merge dialog defaults to the copy that can absorb the others and says what will happen to each row; "extract all to shared group" no longer makes two identical copies permanently unmergeable; and one unpackable asset no longer erases every Addressables duplicate finding.
- **Buttons that would not have changed anything are gone.** Mipmap Streaming, duplicate groups with nothing to merge, AI Migrate on files past its line cap, and findings that pointed at a screen their button was not on.
- **Autopilot no longer hides work it cannot apply for you**, recommends build-size work once your frame-rate target is met, and keeps its one-click plan across a domain reload.
- **A round of readability and layout fixes across the panels and dialogs** — notices that were one colour saying the same thing three times, a settings panel that could hide its own settings, checkboxes on the wrong side of their labels, controls pushed off the bottom or against the far edge of a wide window, and multi-line findings collapsing in the exported report.

## [1.5.1] — 2026-07-25

### Fixed
- A scan no longer leaves hundreds of megabytes resident afterwards.
- Icons no longer render as empty boxes on Unity 2021 / 2022.

## [1.5.0] — 2026-07-25

### Added
- **`perflint_ai_migrate` — AI Migrate over the Unity CLI, for shaders.** Rewrite the file the compiler error points at, re-import, compile, restore the original if it still fails.
- **A Getting Started guide**, once, right after you install.

### Fixed
- Scanning no longer darkens your open scene.

## [1.4.1] — 2026-07-25

### Fixed
- The optimize plan points at `perflint_fix` for the fixes it can't apply itself, instead of implying there are none.

## [1.4.0] — 2026-07-24

### Added
- **Optimize by goal from your agent** (`perflint_optimize_plan` / `perflint_optimize_apply`). "Shrink my build" or "cut memory" gets a real plan. Only the safe, reversible tier is ever applied over the wire; trade-offs and anything that deletes files are listed and left for you in the editor.
- **`perflint_list_findings`** — the actual problem list, not just the counts.
- **`perflint_fix` can fix one category at a time.**

## [1.3.0] — 2026-07-23

### Added
- **Run PerfLint from the Unity CLI** against your open editor: `unity command perflint_scan` / `perflint_gate` / `perflint_fix`, no boilerplate.
- **Headless entry points for CI** — gate a merge on a health regression, export the HTML report, or apply the deterministic fixes.
- **One-click optimize by goal (Pro)**: "Optimize memory…" / "Optimize build size…". Trade-offs are opt-in checkboxes, never silent, and the result is a verified before/after rather than a tally of attempts.
- **The scan panel shows what fixing the findings is worth**, in build size and memory. Rules whose payoff depends on a judgment call show no number rather than a made-up one.

## [1.2.2] — 2026-07-16

### Changed
- The AI credits counter refreshes whenever you return to the LLM panel.

## [1.2.1] — 2026-07-16

### Fixed
- The AI credits counter shows your real remaining balance up front, not the plan's standby allowance.

## [1.2.0] — 2026-07-16

### Added
- **One-click disable for unused built-in modules and packages (Pro), with a compile-verified safety net.** PerfLint edits the manifest, recompiles, and automatically reverts if any script still references what was removed.
- **Capture shader variants from a real build** — desktop players stream them back into the editor live, Android/iOS import from a device log — then merge them into a collection wired into startup warm-up (Pro).
- **AI Migrate for `GetInstanceID`.** Not a rename: the `EntityId`→`int` conversion is deprecated too, so the id's receivers migrate with the call.
- **New checks**: GPU Instancing that does nothing or hurts (MAT004, MAT005), WebGL compression disabled (PROJ010), Strip Engine Code off (PROJ011), IL2CPP favouring speed on a size-sensitive target (PROJ012).

### Fixed
- **Assets used only through Project Settings are no longer flagged as unreferenced (ASSET.UNREF001)** — most visibly the Input System's actions asset, on every Unity 6 / URP-template project.
- **Modules your project can never actually disable are no longer reported as "unused."** SRP Core depends on Terrain, so no URP/HDRP project can ever disable it.
- **Scanning very large projects no longer risks an out-of-memory crash.**

## [1.1.0] — 2026-07-04

The "stuck Unity upgrade" release — turns a project that won't compile after a Unity 6 upgrade into a fixable checklist, and fixes most of it for you.

### Added
- **AI Migrate (Pro): whole-file structural migrations**, compile-verified with automatic rollback and error-driven retries. Starts with URP's removed `RenderTargetHandle`, and repairs shaders that no longer compile.
- **Any compile error is now a finding**, so a project that won't build shows exactly which scripts are blocked and why — including errors no curated rule knows about.
- **"Why is everything magenta?" has full coverage**: pipeline mismatch (MAT001), shaders that fail to compile (SHDR004), materials whose shader is missing (MAT003).
- **Mipmap Streaming advisor** with a live tuning deck in the Runtime Profiler.
- **Static Batching memory bill (PERF.SBATCH001)** — what static batching costs you in build-time Combined Meshes, with a one-click toggle-off (Pro).
- **Resources ↔ Addressables duplication (ASSET.AARES001)**, where the biggest offenders hide: one real project had a TMP font atlas duplicated 74× ≈ 1.16 GB.

### Fixed
- **Addressables duplicate detection no longer misses same-group duplication.** Duplicates are counted per bundle now, matching the official Build Report.
- **Critical severity means "this blocks your project."** Removed APIs and shaders that fail to compile are Critical; deprecation warnings that block nothing stay Warning.

## [1.0.0] — 2026-06-30

First stable release — paid plans are now live. What's included:

- **Local scan** across Performance, Assets, Migration, and Project Settings — one click, runs entirely on your machine.
- **Project health score (0–100)** and a self-contained, shareable **HTML report**.
- **One-click & batch fixes** for safe, deterministic issues (Pro).
- **Merge duplicate assets** (Pro): keep one copy, redirect every reference across the project, delete the rest.
- **Shader variants**: see how many each shader can produce, record the ones your project uses, warm them up at startup (Pro).
- **AI Fix & Explain** — zero-config, or bring your own API key. Every change is shown as a diff and compile-verified with automatic rollback.
- **Local-first, zero telemetry** — your code and art assets are never uploaded.
