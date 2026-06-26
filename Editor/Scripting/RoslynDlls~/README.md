# RoslynDlls~ — bundled Roslyn compiler binaries (dormant)

This folder ends in `~`, so **Unity ignores it entirely**: the DLLs here are never compiled and
never conflict with anything while the optional script-analysis module is disabled.

They are the Microsoft **Roslyn** C# compiler API and its support dependencies, all
**MIT-licensed** by the .NET Foundation / Microsoft. See **Third-Party Notices.md** at the package
root for attribution and license text.

When you enable script analysis from **Tools ▸ PerfLint ▸ Scan Project** ("Enable script analysis"),
PerfLint copies these DLLs into `Assets/Plugins/PerfLintRoslyn/` (Editor-only, Validate References
off) and defines `PERFLINT_ROSLYN`, which compiles the `PerfLint.Editor.Roslyn` module. The install
is conflict-aware: if your project already ships a newer copy of a dependency it is kept; if it ships
an older copy that Roslyn cannot use, the install stops and tells you which file to resolve.

Removing the `PERFLINT_ROSLYN` define disables the module again; the rest of PerfLint is unaffected.
