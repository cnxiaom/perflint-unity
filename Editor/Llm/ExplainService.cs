using System;
using System.Collections.Generic;
using PerfLint.Core;
using PerfLint.L10n;

namespace PerfLint.Llm
{
    /// <summary>
    /// A conversation for a single Finding that explains the issue in plain language and provides a fix.
    /// Privacy-critical: only the finding's metadata (rule / title / detail / path) is sent —
    /// source code and art assets are <b>never</b> transmitted. Supports follow-up questions
    /// (conversation history is retained across turns).
    /// </summary>
    public sealed class ExplainConversation
    {
        // The system prompt follows the UI language → English users get an English explanation, Chinese users get a Chinese one. L.Tr cannot be used in a const, so this is a property.
        private static string SystemPrompt => L.Tr(
            "You are a senior Unity performance/migration engineer embedded in PerfLint, a Unity-editor diagnostics tool. " +
            "For the single diagnostic the user gives you, explain concisely in English why it is a problem and give directly actionable " +
            "fix steps in Unity; include a short code snippet when relevant. Important: the user only provides the diagnostic's metadata — " +
            "you cannot see their full source code or art assets, so don't pretend to and don't ask for the whole codebase. " +
            "Keep answers to the key points; avoid filler.",
            "你是嵌入在 Unity 编辑器性能诊断工具 PerfLint 里的资深 Unity 性能/迁移工程师。" +
            "针对用户给出的一条诊断结果，用简洁中文解释它为什么是问题，并给出在 Unity 中可直接执行的" +
            "修复步骤；涉及代码时给简短片段。重要：用户只提供了诊断条目的元数据，你看不到他们的完整" +
            "源代码或美术资产——不要假装能看到，也不要索要整份代码。回答控制在要点内，避免空话。");

        private readonly List<LlmMessage> _messages = new List<LlmMessage>();
        private readonly string _model;

        public IReadOnlyList<LlmMessage> Messages => _messages;

        public ExplainConversation(Finding finding)
        {
            // Migration-domain rules require more complex reasoning, so a stronger model is used (Opus / deepseek-reasoner); all other domains use the default (Haiku / deepseek-chat).
            _model = LlmSettings.ModelFor(finding.Domain == Domain.Migration);
            _messages.Add(new LlmMessage("user", BuildFirstPrompt(finding)));
        }

        private static string BuildFirstPrompt(Finding f)
        {
            string loc = string.IsNullOrEmpty(f.TargetPath) ? L.Tr("(global / project settings)", "（全局/项目设置）") : f.TargetPath;
            return L.Tr(
                $"Rule {f.RuleId} ({f.Severity} · {f.Domain}): {f.Title}\n" +
                $"Description: {f.Detail}\n" +
                $"Location: {loc}\n\n" +
                "Explain why this is a problem and how to fix it in Unity.",
                $"规则 {f.RuleId}（{f.Severity} · {f.Domain}）：{f.Title}\n" +
                $"说明：{f.Detail}\n" +
                $"位置：{loc}\n\n" +
                "请解释为什么这是个问题，以及在 Unity 中应如何修复。");
        }

        /// <summary>Initiates the first explain request or a follow-up question. onDone is called back on the main thread; on success the reply has already been appended to the conversation history.</summary>
        public void Ask(string followUp, Action<LlmResult> onDone)
        {
            if (!string.IsNullOrEmpty(followUp))
                _messages.Add(new LlmMessage("user", followUp));

            LlmClient.Send(
                model: _model,
                system: SystemPrompt,
                messages: _messages,
                maxTokens: 8192, // Leave enough budget for providers that "think" (chain-of-thought), so the thinking phase does not consume the entire context and truncate the actual answer
                onDone: r =>
                {
                    if (r.Success)
                        _messages.Add(new LlmMessage("assistant", r.Text));
                    onDone(r);
                });
        }
    }
}
