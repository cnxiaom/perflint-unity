# PerfLint for Unity

> A senior tech lead, inside your Unity Editor. One click scans your project, pinpoints performance
> bottlenecks and version-migration blockers, and gives you fixes you can actually apply — the safe ones in one click.
>
> **Local-first · Zero telemetry · Your code and art assets are never uploaded.**

Website: **[perflint.dev](https://perflint.dev)** · Docs: **[perflint.dev/docs](https://perflint.dev/docs)**

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

Pin a version by appending a tag, e.g. `…perflint-unity.git#v0.24.3`. Requires **Unity 2021.3+** (including Unity 6).

## Quick start

Open **Tools ▸ PerfLint ▸ Scan Project** (`Ctrl/Cmd + Shift + L`) and click **Scan Project**. In seconds you get:

- A **project health score (0–100)** and findings grouped by **Performance / Assets / Migration / Project Settings**.
- Each finding has a severity, exact location (**Locate**), the impact, and a fix.
- A self-contained, shareable **HTML report** you can export and send.

## Free vs Pro

The full scan, every finding, and the health report are **free, forever** — plus a daily allowance of AI Fix / Explain.
**Pro** unlocks unlimited one-click / batch auto-fix and a large monthly AI allowance. See pricing at
**[perflint.dev/#pricing](https://perflint.dev/#pricing)**.

AI features work out of the box (zero config) through PerfLint's zero-log proxy, or with your own API key (Advanced) —
direct to the provider, unlimited, never counted against credits.

## Privacy

All scanning runs locally; your code and art assets never leave your machine. The only thing ever sent is — when you
explicitly use Explain or AI Fix — the finding metadata or the single snippet you chose. License checks send only your key.

## License

Proprietary. See **[perflint.dev/license](https://perflint.dev/license)**.
