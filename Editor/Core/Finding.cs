using System;
using System.Collections.Generic;

namespace PerfLint.Core
{
    /// <summary>
    /// A single diagnostic result. The rule engine (IScanner) produces Findings; the UI consumes them.
    /// A Finding itself is deterministic and zero-token — LLM explanations are attached on demand afterwards and are not part of this structure.
    /// </summary>
    public sealed class Finding
    {
        /// <summary>Stable rule identifier, e.g. "PERF.TEX001". Used for deduplication, documentation anchors, and LLM explanation cache keys.</summary>
        public string RuleId { get; }

        public Domain Domain { get; }
        public Severity Severity { get; }

        /// <summary>Single-line title, e.g. "Texture not compressed". May include quantities unique to this finding (e.g. "contains 3 Debug.Log calls"). Used for individual finding rows.</summary>
        public string Title { get; }

        /// <summary>
        /// Optional rule-level group title, without per-finding quantities (e.g. "Runtime scripts contain Debug.Log calls").
        /// Used by the UI as the heading for a rule group — findings in the same group differ in count, so using the first finding's Title as the group header would be misleading (it would show the count for one specific file).
        /// If empty, the group header falls back to Title.
        /// </summary>
        public string GroupTitle { get; }

        /// <summary>For group headers: uses GroupTitle if present, otherwise falls back to Title.</summary>
        public string GroupTitleOrTitle => string.IsNullOrEmpty(GroupTitle) ? Title : GroupTitle;

        /// <summary>Impact description: why this is a problem and what effect it has on performance or migration.</summary>
        public string Detail { get; }

        /// <summary>Issue location: asset path / object name / setting name. May be null (e.g. for project-wide settings issues).</summary>
        public string TargetPath { get; }

        /// <summary>
        /// Callback invoked when the finding is clicked to highlight/select the target in the editor. May be null.
        /// For example: Selection.activeObject = AssetDatabase.LoadAssetAtPath(...).
        /// </summary>
        public Action Ping { get; }

        /// <summary>
        /// Optional automatic fix. Null means the issue can only be handled manually (suggestion only).
        /// The fix entry point is exposed to Pro tier only; Free tier can see the finding but cannot execute the fix.
        /// </summary>
        public IFix Fix { get; }

        public bool CanAutoFix => Fix != null;

        /// <summary>
        /// Optional group of related asset paths (e.g. all copies of a duplicate asset). The UI uses this to provide a "Select all in Project" action,
        /// addressing the case where a single finding actually involves multiple assets and a single Locate is not sufficient.
        /// </summary>
        public IReadOnlyList<string> Group { get; }

        public bool HasGroup => Group != null && Group.Count > 1;

        /// <summary>Findings that point to code carry the source file and line number so that AI Fix can locate and replace the target precisely.</summary>
        public string CodeFile { get; }
        public int CodeLine { get; }

        /// <summary>
        /// Optional "action-type operation" (e.g. changing a configuration setting). The UI renders these separately as buttons and they are **excluded from Fix All**. See <see cref="FindingAction"/>.
        /// Distinction from <see cref="Fix"/>: Fix is an import-settings-type fix that supports Undo and batch re-import; Action is a configuration-change type that requires independent confirmation.
        /// </summary>
        public FindingAction Action { get; }

        public bool HasAction => Action != null;

        /// <summary>
        /// Flag indicating that this finding had a Fix / Action before it was persisted. Only set on findings restored from disk (Fix/Action instances are not serializable and have been lost,
        /// but the fact that "this rule once supported one-click fix" must not be discarded). Findings produced by a live scan are always false — they consult CanAutoFix/HasAction directly.
        /// Purpose: during serialization, hadFix is stored as "currently fixable || was previously fixable" to prevent a "restore-then-re-persist" cycle from silently clearing a previously-fixable rule's flag,
        /// which would cause the "Refresh to enable fix" button to disappear permanently and force a full re-scan.
        /// </summary>
        public bool WasAutoFixable { get; }
        public bool WasActionable { get; }

        /// <summary>Whether this finding is eligible for script-level AI Fix (has a precise code location and no deterministic automatic fix).</summary>
        public bool AiFixable => !string.IsNullOrEmpty(CodeFile) && CodeLine > 0 && !CanAutoFix;

        /// <summary>
        /// When true, this finding bypasses the ignore-path filter (IgnoreSettings). For duplication/build-bloat rules
        /// (AADUP/ABDUP/AARES): users ignore third-party folders to silence "fix this asset's import settings" advice,
        /// but a third-party asset duplicated into N bundles bloats THEIR build and the fix (extract / move out of
        /// Resources) never modifies the third-party asset — hiding those findings guts the report (real case: TMP
        /// fonts 74× ≈ 1.16GB invisible because "Dependencies/" was ignored). Default false.
        /// </summary>
        public bool IgnoreExempt { get; }

        public Finding(
            string ruleId,
            Domain domain,
            Severity severity,
            string title,
            string detail,
            string targetPath = null,
            Action ping = null,
            IFix fix = null,
            IReadOnlyList<string> group = null,
            string codeFile = null,
            int codeLine = 0,
            FindingAction action = null,
            string groupTitle = null,
            bool wasAutoFixable = false,
            bool wasActionable = false,
            bool ignoreExempt = false)
        {
            if (string.IsNullOrEmpty(ruleId)) throw new ArgumentException("ruleId is required", nameof(ruleId));
            if (string.IsNullOrEmpty(title)) throw new ArgumentException("title is required", nameof(title));

            RuleId = ruleId;
            Domain = domain;
            Severity = severity;
            Title = title;
            GroupTitle = groupTitle;
            Detail = detail;
            TargetPath = targetPath;
            Ping = ping;
            Fix = fix;
            Group = group;
            CodeFile = codeFile;
            CodeLine = codeLine;
            Action = action;
            WasAutoFixable = wasAutoFixable;
            WasActionable = wasActionable;
            IgnoreExempt = ignoreExempt;
        }
    }
}
