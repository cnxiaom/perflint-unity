using PerfLint.L10n;
using PerfLint.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace PerfLint.Licensing
{
    /// <summary>
    /// Which tier this machine is on, the key field that changes it, and the way to buy one.
    ///
    /// The last window still dressing itself: five stock editor buttons, four hand-set opacities for the supporting
    /// text, and a fixed green / fixed grey for the one line the whole window exists to state. Every one of those was
    /// picked against a dark editor, and every one of them is what the shared layer was built to stop — see
    /// <see cref="PerfLintStyle"/> for why an inline colour also silently kills a button's hover.
    ///
    /// The layout follows what brings people here. This window is opened from the upgrade prompt in
    /// <see cref="Entitlements"/> — "Get Pro / Enter license" — so the two things it has to answer are, in order:
    /// <b>what am I on</b>, and <b>how do I change it</b>. The state card answers the first and carries the buy
    /// button while it is the useful one; the key section answers the second. A machine already on Pro has no
    /// primary action here, and is given none.
    /// </summary>
    public sealed class PerfLintLicenseWindow : EditorWindow
    {
        private VisualElement _bead;
        private Label _status;
        private Label _tierNote;
        private VisualElement _buyRow;
        private Label _msg;
        private TextField _keyField;
        private Button _activate;
        private Button _deactivate;
        private Button _validate;

        /// <summary>
        /// Whether the paid listing is live and linkable — gates the "buy on the Asset Store" button only, so the
        /// panel never ships a button that leads nowhere.
        /// </summary>
        private static bool StoreListingLive => !string.IsNullOrEmpty(LicenseSettings.AssetStoreProUrl);

        /// <summary>
        /// Whether an invoice number is a credential a user could be holding — gates the activation wording.
        /// Separate from <see cref="StoreListingLive"/> on purpose: the Pro package is built and submitted before
        /// its own listing URL exists, so gating the "you can type an invoice number here" wording on the URL
        /// would ship that package with no way for its buyers to work out how to activate.
        /// </summary>
        private static bool InvoiceActivationOffered => LicenseSettings.AssetStoreBuyoutAvailable;

        public static void Open()
        {
            var w = GetWindow<PerfLintLicenseWindow>(true, "PerfLint · " + L.Tr("License", "许可证"));
            w.minSize = new Vector2(480, 420);
            w.Show();
        }

        private void OnEnable() => LicenseService.Changed += Refresh;
        private void OnDisable() => LicenseService.Changed -= Refresh;

        private void CreateGUI() => Rebuild();

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            PerfLintStyle.Apply(root);
            root.style.paddingTop = 12;
            root.style.paddingLeft = 14;
            root.style.paddingRight = 14;
            root.style.paddingBottom = 10;

            // Users switch language from Tools ▸ PerfLint ▸ Language; this is the dev-only inline shortcut, injected
            // ONLY in a PERFLINT_DEV editor (no-op in release — see L.InjectDevLangSwitch). Either way the panel is
            // rebuilt in the new language.
            L.InjectDevLangSwitch(root, Rebuild);

            // Everything scrolls. The advanced foldout and the privacy block below it are both taller than the space
            // left under the key row on a 420 px window, and a settings panel that hides its own settings is the bug
            // the LLM panel had.
            var body = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1, minHeight = 0 } };
            root.Add(body);

            body.Add(Title(L.Tr("Your license", "你的许可证")));
            body.Add(Body(L.Tr(
                "Everything PerfLint diagnoses is free. Pro is about applying the fixes at project scale.",
                "PerfLint 的全部诊断能力都免费。Pro 解决的是「在整个工程规模上执行修复」。")));

            // ── What this machine is on ──
            //
            // The card, rather than a line of coloured text under a paragraph: this is the single fact somebody
            // opens this window to read. Same reasoning as the credit balance in the LLM panel.
            var state = PerfLintStyle.Card();

            var line = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            _bead = Bead();
            line.Add(_bead);
            _status = new Label
            {
                style = { marginLeft = 8, flexShrink = 1, fontSize = 13, unityFontStyleAndWeight = FontStyle.Bold,
                          color = PerfLintStyle.Ink, whiteSpace = WhiteSpace.Normal }
            };
            line.Add(_status);
            state.Add(line);

            _tierNote = new Label
            {
                style = { marginTop = 4, fontSize = 11, color = PerfLintStyle.Dimmer, whiteSpace = WhiteSpace.Normal }
            };
            state.Add(_tierNote);

            // Shown only while it is the useful button — hidden on a machine that already has Pro, rather than left
            // there to sell something already owned, AND hidden while nothing is actually on sale. Visibility is set
            // in Refresh, so a tier flip while the window is open moves it without a rebuild (which would wipe a
            // half-typed key).
            //
            // The label names no channel on purpose. LicenseSettings.BuyUrl picks between the Asset Store listing
            // and perflint.dev, so wording it after either one would be wrong the moment the other wins — the same
            // cross-reference trap CLAUDE.md calls out. Where the button leads is a fact the button should read
            // from, not repeat. (And when both are empty there is genuinely nowhere to send anyone, hence CanBuy:
            // the key field below still works, which is what somebody arriving with a credential actually needs.)
            _buyRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 10 } };
            if (LicenseSettings.CanBuy)
            {
                var buy = PerfLintStyle.Primary(L.Tr("Get Pro →", "获取 Pro →"),
                    () => Application.OpenURL(LicenseSettings.BuyUrl));
                buy.style.alignSelf = Align.FlexStart;
                _buyRow.Add(buy);
            }
            else if (InvoiceActivationOffered)
            {
                // The Pro package exists but this build predates its listing URL — which is the normal state for
                // the build submitted alongside it, since the URL only exists after approval. Saying where Pro
                // comes from costs nothing and keeps the panel from dead-ending: without this, a Free user reads
                // "Pro adds applying fixes at project scale" and is given no idea how to get it.
                _buyRow.Add(new Label(L.Tr(
                    "Pro is a separate package on the Unity Asset Store. Bought it already? Enter its invoice number below.",
                    "Pro 是 Unity Asset Store 上的另一个包。已经买了的话，在下面填那笔购买的发票号。"))
                {
                    style = { fontSize = 11, color = PerfLintStyle.Dim, whiteSpace = WhiteSpace.Normal, flexShrink = 1 }
                });
            }
            state.Add(_buyRow);

            body.Add(state);

            // ── The key ──
            //
            // One field takes every credential: an existing licence key, and — once the buyout exists — the
            // invoice number, order ID or product code from an Asset Store purchase (the store's own verify tool
            // accepts all three, so people will arrive holding any of them). That is not a shortcut: the proxy
            // already works out which channel a credential belongs to and remembers it, so a second field would
            // only ask the user to classify something the server classifies better.
            body.Add(Header(InvoiceActivationOffered
                ? L.Tr("Already bought?", "已经买了？")
                : L.Tr("Already have a key?", "已经有 key 了？")));

            _keyField = new TextField(InvoiceActivationOffered
                ? L.Tr("Key or invoice no.", "密钥 或 发票号")
                : L.Tr("License key", "许可证密钥")) { value = LicenseSettings.Key };
            body.Add(_keyField);
            if (InvoiceActivationOffered)
                body.Add(Hint(L.Tr(
                    "The invoice number, order ID or product code from your Asset Store purchase — or an existing PerfLint license key. This field takes any of them.",
                    "填 Asset Store 购买后的发票号、订单号或产品码；已有 PerfLint 许可证密钥的也填这里 —— 同一个框都收。")));

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 8 } };
            _activate = PerfLintStyle.Secondary(L.Tr("Activate", "激活"), OnActivate);
            _activate.style.flexGrow = 1;
            row.Add(_activate);

            _deactivate = PerfLintStyle.Secondary(L.Tr("Deactivate", "停用"), OnDeactivate);
            _deactivate.style.marginLeft = 6;
            row.Add(_deactivate);

            _validate = PerfLintStyle.Secondary(L.Tr("Re-check", "复验"), OnValidate);
            _validate.style.marginLeft = 6;
            row.Add(_validate);
            body.Add(row);

            _msg = new Label
            {
                style = { marginTop = 8, fontSize = 12, color = PerfLintStyle.Dim, whiteSpace = WhiteSpace.Normal }
            };
            body.Add(_msg);

            // ── Advanced: custom validation endpoint ──
            var adv = new Foldout { text = L.Tr("Advanced", "高级"), value = false };
            adv.style.marginTop = 12;

            // A recessed block, so the one setting that only exists while the foldout is open reads as belonging to
            // it rather than as another top-level row.
            var advBody = PerfLintStyle.Panel();
            advBody.style.marginTop = 4;
            var ep = new TextField(L.Tr("License endpoint", "校验端点")) { value = LicenseSettings.Endpoint };
            ep.RegisterValueChangedCallback(e => LicenseSettings.Endpoint = e.newValue);
            advBody.Add(ep);
            advBody.Add(Hint(L.Tr(
                "The license-validation proxy URL. Leave default unless self-hosting.",
                "许可证校验代理地址。除非自建，否则保持默认。")));
            adv.Add(advBody);
            body.Add(adv);

            // The trust anchor gets a block rather than fine print — the same treatment, and the same reasoning, as
            // the privacy note in the LLM panel: it is a claim the reader has to believe, not boilerplate to skip.
            var privacy = PerfLintStyle.Note(PerfLintStyle.NoteAccent);
            privacy.style.marginTop = 12;
            privacy.Add(new Label(L.Tr(
                "The key is stored only in local EditorPrefs. Validation sends the key and nothing else — never your code or assets.",
                "密钥仅存于本机 EditorPrefs。校验只发送密钥本身，绝不上传你的代码或资产。"))
            {
                style = { fontSize = 11, color = PerfLintStyle.Dim, whiteSpace = WhiteSpace.Normal }
            });
            body.Add(privacy);

            Refresh();
        }

        // ── small UI helpers (the type scale Getting Started and CLI & CI use) ──
        //
        // Tints rather than opacities: opacity fades a label towards whatever is behind it, which on a light skin
        // means fading dark text towards a white page. The palette states the colour per skin instead.
        private static Label Title(string t) => new Label(t)
        {
            style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, marginBottom = 4,
                      whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink }
        };

        private static Label Header(string t) => new Label(t)
        {
            style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 12, marginBottom = 6,
                      whiteSpace = WhiteSpace.Normal, color = PerfLintStyle.Ink }
        };

        private static Label Body(string t) => new Label(t)
        {
            style = { whiteSpace = WhiteSpace.Normal, marginBottom = 8, color = PerfLintStyle.Dim }
        };

        private static Label Hint(string t) => new Label(t)
        {
            style = { whiteSpace = WhiteSpace.Normal, unityFontStyleAndWeight = FontStyle.Italic, fontSize = 10,
                      marginTop = 4, color = PerfLintStyle.Dimmer }
        };

        /// <summary>A drawn status bead. Never a glyph — the 2021/2022 editor fonts have no emoji and no fallback.</summary>
        private static VisualElement Bead()
        {
            var dot = new VisualElement { style = { width = 9, height = 9, flexShrink = 0 } };
            PerfLintStyle.Round(dot, 5);
            return dot;
        }

        /// <summary>
        /// Re-reads the licence and repaints the parts that depend on it. Never rebuilds: this also runs on the
        /// <see cref="LicenseService.Changed"/> event, which can land while a key is half-typed in the field.
        /// </summary>
        private void Refresh()
        {
            if (_status == null) return;

            bool pro = LicenseService.IsPro;
            bool hasKey = !string.IsNullOrEmpty(LicenseSettings.Key);

            _status.text = LicenseService.StatusLine();

            // Three states, not two. Amber is the one the old green/grey pair could not say: a key IS on file and it
            // is not granting Pro — expired, deactivated, or past the offline grace window — which is a caveat, while
            // a machine with no key at all is simply on Free and nothing about it is wrong.
            if (_bead != null)
                _bead.style.backgroundColor = pro ? PerfLintStyle.Good
                                            : hasKey ? PerfLintStyle.Amber
                                            : PerfLintStyle.Dimmer;

            // The Pro line differs by channel, because what the two actually grant differs: a buyout's AI credits
            // are a one-time pack, a monthly plan's refill. Telling a buyout owner about a "monthly allowance"
            // would promise a refill that never arrives — and telling anyone to go subscribe would advertise a
            // channel that is no longer for sale. Describing the plan someone already holds is fine; steering
            // people towards buying one elsewhere is not.
            if (_tierNote != null)
                _tierNote.text = !pro
                    ? L.Tr("The full scan, every finding, the health score and the shareable report are already yours, plus a daily allowance of AI Fix / Explain — or unlimited with your own API key. Pro adds applying fixes at project scale.",
                           "完整扫描、全部诊断、健康分、可分享报告已经归你，外加每日一定次数的 AI Fix / Explain —— 填入自己的 API key 则不限量。Pro 增加的是在整个工程规模上执行修复。")
                    : LicenseSettings.IsPerpetualBuyout
                        ? L.Tr("Yours permanently: unlimited one-click and batch fixes, duplicate-asset de-duplication, and the Migration Assistant. The included AI credits are a one-time pack — once they run out, add your own API key under Advanced for unlimited, self-funded use.",
                               "永久归你：无限一键与批量修复、重复资源去重、迁移助手。附带的 AI 额度是一次性包 —— 用完后在「高级」里填入自己的 API key，自费无限使用。")
                        : L.Tr("Unlimited one-click and batch fixes, duplicate-asset de-duplication, the Migration Assistant, and your plan's AI allowance.",
                               "无限一键与批量修复、重复资源去重、迁移助手，以及你所在套餐的 AI 额度。");

            // Hidden on a machine that already has Pro, and hidden when Rebuild found nothing worth putting in it
            // (no purchase link AND no listing to point at). Keyed on childCount rather than re-deriving the
            // conditions, so this can never disagree with what Rebuild actually built.
            if (_buyRow != null)
                _buyRow.style.display = (pro || _buyRow.childCount == 0) ? DisplayStyle.None : DisplayStyle.Flex;

            // Both of these need a key on file to do anything at all — Validate returns "Not activated yet." without
            // one, and Deactivate has nothing to remove. A button whose only possible outcome is a message saying it
            // could not run is one this product does not offer.
            if (_deactivate != null) _deactivate.SetEnabled(hasKey);
            if (_validate != null) _validate.SetEnabled(hasKey);
        }

        /// <summary>
        /// The result of the last thing clicked, in the colour of its outcome — the panel's own SetStatus.
        ///
        /// Null-guarded because a callback lands whenever the network does, and a language flip rebuilds the panel
        /// out from under a request already in flight.
        /// </summary>
        private void SetMessage(string text, Color tint)
        {
            if (_msg == null) return;
            _msg.text = text;
            _msg.style.color = tint;
        }

        private void OnActivate()
        {
            SetMessage(L.Tr("Activating…", "激活中…"), PerfLintStyle.Dim);
            _activate.SetEnabled(false);
            LicenseService.Activate(_keyField.value, (ok, m) =>
            {
                if (_activate != null) _activate.SetEnabled(true);
                SetMessage(m, ok ? PerfLintStyle.Good : PerfLintStyle.Bad);
                Refresh();
            });
        }

        private void OnValidate()
        {
            SetMessage(L.Tr("Checking…", "复验中…"), PerfLintStyle.Dim);
            LicenseService.Validate((ok, m) =>
            {
                SetMessage(m, ok ? PerfLintStyle.Good : PerfLintStyle.Bad);
                Refresh();
            });
        }

        private void OnDeactivate()
        {
            if (!EditorUtility.DisplayDialog(
                    L.Tr("Deactivate", "停用"),
                    L.Tr("Remove the license from this machine? You can re-activate later.",
                         "从本机移除许可证？之后可重新激活。"),
                    L.Tr("Deactivate", "停用"), L.Tr("Cancel", "取消")))
                return;

            LicenseService.Deactivate((ok, m) =>
            {
                SetMessage(m, ok ? PerfLintStyle.Dim : PerfLintStyle.Bad);
                if (_keyField != null) _keyField.value = "";
                Refresh();
            });
        }
    }
}
