using PerfLint.L10n;
using PerfLint.Llm;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Licensing
{
    /// <summary>
    /// Unified entry point for feature gating. Two gate categories:
    ///   ① Execution (one-click / batch auto-fix, FindingAction) — no LLM cost, pure Pro value; use <see cref="RequirePro"/>.
    ///   ② LLM (Explain, AI Fix) — hosted proxy: Free daily quota / Pro monthly quota (credits). BYO key is a **Pro**
    ///      feature (self-funded, unlimited); Free users get the hosted daily allowance instead. Use <see cref="RequireAiCredit"/>.
    /// Gates **block only execution entry points** and never hide findings themselves — diagnostic results are the
    /// core value of the free tier and the primary viral hook.
    /// </summary>
    public static class Entitlements
    {
        public static bool IsPro => LicenseService.IsPro;

        /// <summary>Returns true to allow; otherwise shows an upgrade prompt and returns false. Call this at every Pro action's click site.</summary>
        public static bool RequirePro(string feature)
        {
            if (IsPro) return true;

            bool openLicense = EditorUtility.DisplayDialog(
                L.Tr("Pro feature", "Pro 功能"),
                L.Tr(
                    $"\"{feature}\" is a Pro feature.\n\n" +
                    "Free includes the full scan, all findings, the health report, fix guidance, and a daily allowance of AI Fix / Explain. " +
                    "Pro unlocks unlimited one-click / batch auto-fix and a much larger monthly AI allowance.",
                    $"「{feature}」是 Pro 功能。\n\n" +
                    "Free 已包含完整扫描、全部诊断、健康度报告、修复建议，以及每日少量 AI 修复/解释额度；" +
                    "Pro 解锁无限一键/批量自动修复，以及大得多的每月 AI 额度。"),
                L.Tr("Get Pro / Enter license", "获取 Pro / 输入许可证"),
                L.Tr("Maybe later", "以后再说"));

            if (openLicense) PerfLintLicenseWindow.Open();
            return false;
        }

        /// <summary>
        /// Gate logic for LLM actions (Explain / AI Fix):
        ///   · BYO key (ByoKey mode) → **Pro only** (self-funded, unlimited). Free users in this mode are blocked and
        ///     pointed at either the hosted daily allowance or upgrading — BYO is a paid-tier feature.
        ///   · Hosted mode and locally cached quota not yet exhausted → allow (authoritative enforcement is on the server /llm; this is a soft block only).
        ///   · Hosted mode and quota exhausted → show "Upgrade Pro / use your own key" prompt and block.
        /// Note: unlike RequirePro, Free users still get the hosted daily allowance — that is the conversion hook.
        /// </summary>
        public static bool RequireAiCredit(string feature)
        {
            if (LlmSettings.Mode == LlmMode.ByoKey)
            {
                if (IsPro) return true;                                 // self-funded, unlimited — Pro only
                // Free + BYO: bring-your-own key is a Pro feature. Steer to the hosted free allowance, or Pro.
                bool openByo = EditorUtility.DisplayDialog(
                    L.Tr("Pro feature", "Pro 功能"),
                    L.Tr(
                        "Using your own API key is a Pro feature.\n\n" +
                        "On Free, switch the LLM mode back to the built-in AI service to use your daily AI Fix / Explain allowance — " +
                        "or upgrade to Pro to use your own key (unlimited, self-funded, never counts against credits).",
                        "使用自己的 API key 是 Pro 功能。\n\n" +
                        "Free 档请把 LLM 模式切回内置 AI 服务，使用每日 AI 修复/解释额度；" +
                        "或升级 Pro 后用自己的 key（自费、无限、不计 credits）。"),
                    L.Tr("Get Pro / Enter license", "获取 Pro / 输入许可证"),
                    L.Tr("Maybe later", "以后再说"));
                if (openByo) PerfLintLicenseWindow.Open();
                return false;
            }
            if (!CreditService.HostedExhausted) return true;           // quota remaining (or unknown → optimistic pass)

            bool openLicense = EditorUtility.DisplayDialog(
                L.Tr("Out of AI credits", "AI 额度已用完"),
                L.Tr(
                    $"\"{feature}\" needs an AI credit, but you're out for this period.\n\n" +
                    "Upgrade to Pro for a much larger monthly allowance, or add your own API key under Advanced " +
                    "for unlimited (self-funded) use that never counts against credits.",
                    $"「{feature}」需要消耗 1 个 AI 额度，但本期额度已用完。\n\n" +
                    "升级 Pro 可获得大得多的每月额度；或在「高级」里填入自己的 API key，自费无限使用、永不计入 credits。"),
                L.Tr("Get Pro / Enter license", "获取 Pro / 输入许可证"),
                L.Tr("Maybe later", "以后再说"));

            if (openLicense) PerfLintLicenseWindow.Open();
            return false;
        }
    }
}
