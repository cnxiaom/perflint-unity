using PerfLint.L10n;
using PerfLint.Llm;
using UnityEditor;
using UnityEngine;

namespace PerfLint.Licensing
{
    /// <summary>
    /// Unified entry point for feature gating. Two gate categories:
    ///   ① Execution (one-click / batch auto-fix, FindingAction) — no LLM cost, pure Pro value; use <see cref="RequirePro"/>.
    ///   ② LLM (Explain, AI Fix) — hosted proxy: Free daily quota / Pro allowance (credits). A user's OWN key is
    ///      open to both tiers (self-funded, unlimited, never counted). Use <see cref="RequireAiCredit"/>.
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
                    "Pro unlocks unlimited one-click / batch auto-fix and a much larger AI allowance.",
                    $"「{feature}」是 Pro 功能。\n\n" +
                    "Free 已包含完整扫描、全部诊断、健康度报告、修复建议，以及每日少量 AI 修复/解释额度；" +
                    "Pro 解锁无限一键/批量自动修复，以及大得多的 AI 额度。"),
                L.Tr("Get Pro / Enter license", "获取 Pro / 输入许可证"),
                L.Tr("Maybe later", "以后再说"));

            if (openLicense) PerfLintLicenseWindow.Open();
            return false;
        }

        /// <summary>
        /// Gate logic for LLM actions (Explain / AI Fix):
        ///   · The user's own key (ByoKey mode) → always allow, on **either tier**. The call is self-funded and never
        ///     touches our proxy, so there is nothing to meter and no reason to charge for it. Gating it would have
        ///     meant refusing to let someone spend their own money — and it is the escape hatch the listing promises
        ///     once a bounded allowance runs out, which only works if Free can reach it too.
        ///   · Hosted mode and locally cached quota not yet exhausted → allow (authoritative enforcement is on the server /llm; this is a soft block only).
        ///   · Hosted mode and quota exhausted → show "Upgrade / use your own key" prompt and block.
        /// Note: unlike RequirePro, Free users still get the hosted daily allowance — that is the conversion hook.
        /// </summary>
        public static bool RequireAiCredit(string feature)
        {
            if (LlmSettings.Mode == LlmMode.ByoKey) return true;       // self-funded, unlimited, both tiers
            if (!CreditService.HostedExhausted) return true;           // quota remaining (or unknown → optimistic pass)

            bool openLicense = EditorUtility.DisplayDialog(
                L.Tr("Out of AI credits", "AI 额度已用完"),
                L.Tr(
                    $"\"{feature}\" needs an AI credit, but you're out for this period.\n\n" +
                    "Pro comes with a much larger allowance, or add your own API key under Advanced " +
                    "for unlimited (self-funded) use that never counts against credits.",
                    $"「{feature}」需要消耗 1 个 AI 额度，但本期额度已用完。\n\n" +
                    "Pro 附带大得多的额度；或在「高级」里填入自己的 API key，自费无限使用、永不计入 credits。"),
                L.Tr("Get Pro / Enter license", "获取 Pro / 输入许可证"),
                L.Tr("Maybe later", "以后再说"));

            if (openLicense) PerfLintLicenseWindow.Open();
            return false;
        }
    }
}
