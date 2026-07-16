using System.Runtime.CompilerServices;

// The capture protocol/parser are consumed by the editor assembly (ingest, SVC building) and locked down by the
// editor test assembly; keeping the types internal keeps them out of users' IntelliSense (this is plumbing, not API).
[assembly: InternalsVisibleTo("PerfLint.Editor")]
[assembly: InternalsVisibleTo("PerfLint.Tests.Editor")]
