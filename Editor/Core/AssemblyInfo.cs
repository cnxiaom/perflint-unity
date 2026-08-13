using System.Runtime.CompilerServices;

// Allow the test assembly to access internal members (e.g. pure-logic unit tests for TextureImportScanner.IsUncompressedFormat).
[assembly: InternalsVisibleTo("PerfLint.Tests.Editor")]

// The CLI/MCP command layer is a separate assembly (gated on com.unity.pipeline), and perflint_apply_verified
// drives the same compile-verify/rollback machinery the in-editor AI Fix uses. Granted here rather than made
// public: PerfLintScriptFixVerifier is an internal mechanism with a contract that is easy to misuse from
// outside (the backup must exist before the write), and widening it to the package's public API would invite
// exactly that.
//
// ⚠ A compile error here does NOT show up in tests/run-editmode-tests.sh — the test project has no
//    com.unity.pipeline, so PerfLint.Editor.Pipeline is skipped there entirely. Changes that touch this
//    boundary have to be compiled in a project that HAS the package (this one was caught only in the live
//    editor, after batchmode reported 1209/1209 green).
[assembly: InternalsVisibleTo("PerfLint.Editor.Pipeline")]
