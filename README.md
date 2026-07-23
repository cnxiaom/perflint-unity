# PerfLint for Unity

> A senior tech lead, inside your Unity Editor. One click scans your project, pinpoints performance
> bottlenecks and version-migration blockers, and hands you fixes you can actually apply — the safe ones in one click.
>
> **Local-first · Zero telemetry · Your code and art assets are never uploaded.**

**[perflint.dev](https://perflint.dev)** · [Docs](https://perflint.dev/docs) · [Blog](https://perflint.dev/blog)

https://github.com/user-attachments/assets/0261ba42-09d8-437a-b083-539f3b3696b9

*▶ 80-second demo — a real production build: **337 duplicate bundle dependencies → 5**, on disk **1.3 GB → 808 MB**. (also at [perflint.dev](https://perflint.dev/#demo))*

## Find what is slowing, bloating, or breaking your Unity project

One scan, in seconds, across four domains. Every finding carries a **severity**, an exact **Locate**, a plain-language explanation of the impact, and a **fix** — the safe ones you can apply in one click.

Detection is a **deterministic rule engine** — reproducible, offline, zero-token, the same result every run. No account is required to scan, and nothing ever leaves your machine.

## Real fixes. Measured before and after.

Not mockups — screenshots from a real Unity project after applying the fixes PerfLint found.

**Addressables de-dup** — the killer feature, in Unity's own Addressables analysis:

| Before | After |
|:---:|:---:|
| ![Unity Addressables analysis: 337 duplicate assets, 77 bundles totaling 1.29 GB](https://perflint.dev/before-after/aadup-before.jpg?v=2) | ![Unity Addressables analysis after de-dup: 5 duplicate assets, bundles totaling 805 MB](https://perflint.dev/before-after/aadup-after.jpg?v=2) |
| 337 duplicate assets · 1.29 GB bundles | **5** · 805 MB **(−485 MB)** |

**Unity 6 migration** — a URP scene whose water rendered magenta after the upgrade:

| Before | After |
|:---:|:---:|
| ![The lake renders as solid magenta after a Unity 6 / URP upgrade](https://perflint.dev/before-after/mig-water-before.png) | ![The same scene with the water rendering correctly again](https://perflint.dev/before-after/mig-water-after.png) |
| Broken water shader → magenta | **Restored** — compile-verified AI Migrate fix |

**Memory** — redundant cubemaps and oversized textures the Assets scan flagged, in Unity's own Memory Profiler:

| Graphics memory | On device |
|:---:|:---:|
| ![Unity Memory Profiler: graphics memory drops 227.9 MB](https://perflint.dev/before-after/memory-graphics.png?v=2) | ![Unity Memory Profiler: total resident memory falls from 1.48 GB to 0.92 GB](https://perflint.dev/before-after/memory-device.png?v=2) |
| **−227.9 MB** graphics memory | **−0.55 GB** resident (1.48 GB → 0.92 GB) |

→ [More case studies on the blog](https://perflint.dev/blog)

## Deterministic diagnosis. AI only where it helps.

Detection runs on a rule engine — reproducible, offline, zero tokens. The LLM is only used to explain a finding in plain language, answer follow-ups, and generate fix snippets. Stable results, controllable cost.

- **🎯 Deterministic engine** — Roslyn script analysis + asset / import / project-settings scanners. Same project, same findings, every time. No cloud round-trip to get a report.
- **🛠️ Safe one-click fixes** — import-setting fixes apply in a batch with a preview and full Undo. You stay in control; nothing changes your project silently.
- **🤖 AI Fix with a safety net** — script-level fixes are compile-verified after writing; if the build breaks, the change is rolled back automatically. Only the snippet you choose is ever sent.

**Free — find and understand every issue.** Full scan, every finding, the shareable health report, written fix guidance, and a daily allowance of AI Fix / Explain credits.

**Pro — apply fixes at project scale.** One-click, batch, and optimize-by-goal fixes, the whole-file Migration Assistant, more AI credits, and bring-your-own key. → [See plans on perflint.dev](https://perflint.dev/#pricing)

## Diagnostic domains

Every finding has a severity, an exact location, the impact, and a fix.

### 🚀 Performance — find what actually costs frames
- Uncompressed / oversized textures, redundant Read/Write, Sprite mipmaps
- Per-frame GC: `GetComponent` / `Camera.main` / `FindObjectOfType` in `Update`, string concat, LINQ, `WaitForSeconds`
- `Debug.Log` left in builds, batching-breaking materials, SRP instancing hints
- Mesh & audio import settings that bloat memory
- Play-Mode profiling that locates stutter, per-frame GC, and CPU hotspots down to the exact script

### 📦 Assets — trim the dead weight
- Duplicate assets (content-hashed groups) with one-click select + CSV export
- Assets double-packed across AssetBundles / Addressables
- Unreferenced assets pulled into the build, shader-variant blowups
- Conservative and low-false-positive — report-class items are never auto-deleted

### ⬆️ Migration — survive the Unity 6 upgrade
- Deprecated / removed APIs, located to the exact line, with replacements
- Old vs. new Input System mixing, package-version vs. your Unity compatibility
- `manifest.json` preview / legacy package checks, Unity 6 blockers (RenderTargetHandle, magenta shaders)
- Pro Migration Assistant for whole-file fixes — compile-verified, auto-rollback on failure

## Run it anywhere — editor, CLI, or CI

![The Unity CLI](https://perflint.dev/blog/cli/unity-cli.png)

Drive PerfLint from the terminal. With Unity's Pipeline package it runs against your open editor with no boilerplate — no editor path, no `-batchmode`, no `-projectPath`:

```
unity command perflint_scan               # health score, grade, finding counts
unity command perflint_gate --min_score 60
unity command perflint_fix                # apply the safe fixes   (--dry_run to preview)
```

And headless in CI — a gate that fails the build on a health regression, with a real exit code you can trust:

```
Unity -batchmode -projectPath . -executeMethod PerfLint.Ci.PerfLintCli.RunGate -perflintMinScore 60 -logFile -
```

→ [Full CLI &amp; CI guide](https://perflint.dev/docs#ci)

## Install (Unity Package Manager — Git URL)

`Window ▸ Package Manager ▸ + ▸ Add package from git URL…` and paste:

```
https://github.com/cnxiaom/perflint-unity.git
```

Or add it to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.perflint.unity": "https://github.com/cnxiaom/perflint-unity.git"
  }
}
```

Pin a version by appending a tag, e.g. `…perflint-unity.git#v1.0.0`. Requires **Unity 2021.3+** (including Unity 6).

## From install to fixed in minutes

1. **Install via UPM.** Add the package by Git URL — no account, no login.
2. **Click Scan.** Open **Tools ▸ PerfLint ▸ Scan Project** (`Ctrl/Cmd + Shift + L`) and click **Scan Project**. In seconds you get a **project health score (0–100)** and findings grouped by **Performance / Assets / Migration / Project Settings** — each with a severity, an exact **Locate**, the impact in plain language, and a fix.
3. **Fix &amp; ship.** Follow the free guidance or use your daily AI Fix allowance, and export a self-contained, shareable **HTML report** to send your team. Upgrade to Pro for one-click, batch, and whole-file migration fixes.

Prefer the terminal? See **[Run it anywhere](#run-it-anywhere--editor-cli-or-ci)** above.

## Privacy

All scanning runs locally — your code and art assets never leave your machine, and no account is needed to scan.
The only thing ever sent is, when you explicitly use the optional Explain / AI Fix helpers, the finding text or the
single code snippet you chose. That's it. **Zero telemetry.**

## FAQ

**Does it upload my project or source code?**
No. All scanning and analysis happen locally in your editor. The only thing ever sent is, when you explicitly use Explain or AI Fix, the finding's metadata or the single code snippet you chose — through a zero-log proxy, or direct to your own provider if you add a key.

**Do I need an API key?**
No. AI Fix and Explain work out of the box using your plan's AI credits. Pro users can instead add their own Claude or DeepSeek key; those calls go direct to the provider and are unlimited. The deterministic scan, findings, and health report need no key and no network at all.

**Which Unity versions are supported?**
Unity 2021.3 and newer, including Unity 6. Migration rules are version-aware — you won't get noise about APIs that aren't actually deprecated in your version.

**Will AI Fix break my code?**
AI Fix writes the change, triggers a compile, and automatically rolls back if compilation fails. It's scoped to safe, mechanical changes (e.g. API renames). We still recommend committing to version control first.

## Learn more

- **[Docs](https://perflint.dev/docs)** — install, first scan, CLI &amp; CI, privacy.
- **[Blog](https://perflint.dev/blog)** — case studies, Unity 6 migration guides, and deep dives.

## License

Copies from the **Unity Asset Store** are governed by the Unity Asset Store EULA; copies from our website or the UPM
Git URL are governed by **[perflint.dev/license](https://perflint.dev/license)**. See [LICENSE.md](LICENSE.md).
