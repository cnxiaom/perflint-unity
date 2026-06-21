using System.Runtime.CompilerServices;

// This assembly (PerfLint.Editor.Roslyn) is only compiled when PERFLINT_ROSLYN is enabled. Expose internals
// to the isolated Roslyn test assembly—used for end-to-end testing of PerFrameAllocationWalker / ScriptIssue (feed source, assert GC/UPD rules).
[assembly: InternalsVisibleTo("PerfLint.Tests.Roslyn")]
