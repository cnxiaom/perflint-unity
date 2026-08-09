# Changelog

User-facing changes to PerfLint for Unity. This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

First stable release — paid subscriptions are now live. What's included:

- **Local scan** across Performance, Assets, Migration, and Project Settings — one click, runs entirely on your machine.
- **Project health score (0–100)** and a self-contained, shareable **HTML report**.
- **One-click & batch fixes** for safe, deterministic issues (Pro).
- **Merge duplicate assets** (Pro): keep one copy, redirect every reference across the project, delete the rest.
- **Shader variants**: see how many each shader can produce, record the ones your project uses, warm them up at startup (Pro).
- **AI Fix & Explain** — zero-config, or bring your own API key. Every change is shown as a diff and compile-verified with automatic rollback.
- **Local-first, zero telemetry** — your code and art assets are never uploaded.
