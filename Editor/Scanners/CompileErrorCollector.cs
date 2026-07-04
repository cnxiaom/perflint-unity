using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace PerfLint.Scanners
{
    /// <summary>One captured compiler error. Serializable for the SessionState safety-net snapshot.</summary>
    [Serializable]
    internal sealed class CollectedError
    {
        public string file;   // project-relative, '/'-normalized (CompilerMessage.file is complete — no shader-style truncation)
        public int line;
        public string message; // includes the CS code, e.g. "error CS0246: The type or namespace name 'WWW' could not be found…"
    }

    /// <summary>
    /// Captures C# compiler errors as they happen (compile-error ingestion, docs/compile-error-ingestion-plan.md).
    /// Subscribes to CompilationPipeline.assemblyCompilationFinished and keeps the latest error set per assembly.
    /// The lifecycle mirrors the AI Migrate retry loop's insight: a FAILED compilation never reloads the domain, so
    /// this static store survives exactly as long as the errors are relevant; a SUCCESSFUL compilation reloads the
    /// domain and evaporates the store — which is precisely when it should reset. A SessionState snapshot covers
    /// the in-between timing gaps; the one true blind spot (a cold first-ever compile failing before any managed
    /// subscriber exists) degrades to a "details pending — recompile to capture" finding in the scanner.
    /// </summary>
    [InitializeOnLoad]
    internal static class CompileErrorCollector
    {
        private const string SessionKey = "PerfLint.CompileErrors.v1";
        private const string RestoredAssemblyKey = "__session_restored__";

        // assembly path -> that assembly's errors from its most recent compilation.
        private static readonly Dictionary<string, List<CollectedError>> ByAssembly =
            new Dictionary<string, List<CollectedError>>(StringComparer.OrdinalIgnoreCase);

        static CompileErrorCollector()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            RestoreFromSession();
        }

        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            try
            {
                var errs = new List<CollectedError>();
                if (messages != null)
                {
                    foreach (var m in messages)
                        if (m.type == CompilerMessageType.Error)
                            errs.Add(new CollectedError { file = NormPath(m.file), line = m.line, message = m.message });
                }

                // A recompile of this assembly replaces its previous entry; the session-restored blob (assembly
                // granularity unknown) is superseded by ANY live event — live data always wins over the snapshot.
                ByAssembly.Remove(RestoredAssemblyKey);
                if (errs.Count > 0) ByAssembly[assemblyPath ?? ""] = errs;
                else ByAssembly.Remove(assemblyPath ?? "");

                SaveToSession();
            }
            catch { /* the collector must never break a compilation callback */ }
        }

        /// <summary>All currently-known errors, flattened. Empty when the last compilation succeeded.</summary>
        public static List<CollectedError> Snapshot()
        {
            var all = new List<CollectedError>();
            foreach (var list in ByAssembly.Values) all.AddRange(list);
            return all;
        }

        // ── SessionState safety net ────────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class SessionBlob { public List<CollectedError> errors = new List<CollectedError>(); }

        private static void SaveToSession()
        {
            try
            {
                var blob = new SessionBlob { errors = Snapshot() };
                SessionState.SetString(SessionKey, JsonUtility.ToJson(blob));
            }
            catch { }
        }

        private static void RestoreFromSession()
        {
            try
            {
                // A domain reload after a SUCCESSFUL compile means the errors are gone for real — clear the snapshot.
                if (!EditorUtility.scriptCompilationFailed) { SessionState.EraseString(SessionKey); return; }

                string json = SessionState.GetString(SessionKey, null);
                if (string.IsNullOrEmpty(json)) return;
                var blob = JsonUtility.FromJson<SessionBlob>(json);
                if (blob?.errors != null && blob.errors.Count > 0)
                    ByAssembly[RestoredAssemblyKey] = blob.errors;
            }
            catch { }
        }

        private static string NormPath(string p) => string.IsNullOrEmpty(p) ? "" : p.Replace('\\', '/');
    }
}
