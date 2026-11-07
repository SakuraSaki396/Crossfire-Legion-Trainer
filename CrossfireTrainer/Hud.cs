using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace CrossfireTrainer
{
    /// <summary>
    /// Canvas based HUD.
    ///
    /// The previous implementation drew the window with IMGUI (OnGUI), which allocates
    /// managed memory on every single frame. At 200+ fps that filled the Mono heap fast
    /// enough to trigger a stop-the-world GC roughly every five seconds, which showed up
    /// as a ~300 ms freeze. This version builds the widget tree once and only touches
    /// text when a value actually changes, so the steady-state cost is a few comparisons.
    /// </summary>
    internal static class Hud
    {
        private const float PanelW = 372f;
        private const float PanelH = 716f;

        // speed slider geometry
        private const float SpeedMin = 0.5f;
        private const float SpeedMax = 5f;
        private const float SpeedStep = 0.1f;
        private const float TrackW = PanelW - 32f;
        private const float KnobW = 14f;

        private static readonly Color CBg = new Color(0.055f, 0.062f, 0.072f, 0.95f);
        private static readonly Color CCard = new Color(1f, 1f, 1f, 0.06f);
        private static readonly Color CCardOn = new Color(0.16f, 0.50f, 0.31f, 0.92f);
        private static readonly Color CAccent = new Color(1f, 0.42f, 0.21f, 1f);
        private static readonly Color CText = new Color(0.93f, 0.94f, 0.96f, 1f);
        private static readonly Color CDim = new Color(0.62f, 0.65f, 0.70f, 1f);
        private static readonly Color CDotOn = new Color(0.45f, 0.92f, 0.62f, 1f);
        private static readonly Color CDotOff = new Color(1f, 1f, 1f, 0.22f);
        private static readonly Color CTrack = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color CKnob = new Color(0.94f, 0.95f, 0.97f, 1f);

        private sealed class Btn
        {
            public RectTransform Rt;
            public Image Bg;
            public Image Dot;
            public Text Label;
            public Action OnClick;
        }

        private static readonly List<Btn> _buttons = new List<Btn>();
        private static Font _font;
        private static GameObject _root;
        private static RectTransform _panel;
        private static RectTransform _header;
        private static Text _status;
        private static Text _speedValue;
        private static Text _toast;
        private static Image _toastBg;
        private static Btn _rowRes, _rowGod, _rowBuild, _rowDmg, _rowEnergy, _rowMana, _rowUnitCd, _rowVision;
        private static Btn _rowMoveSpeed, _rowPopCap;
        private static Btn[] _amounts;
        private static RectTransform _speedTrackRt, _speedFillRt, _speedKnobRt, _speedHit;
        private static bool _sliding;
        private static float _lastSlideApply;
        private static bool _built;
        private static bool _dragging;
        private static Vector2 _dragOffset;

        // cached values, so per-frame work stays allocation free
        private static string _lastStatus, _lastToastText, _lastTag;
        private static float _lastSpeedVal = -1f;
        private static int _lastAmount = -1;
        private static bool _lastVisible = true;
        private static readonly bool[] _lastRow = new bool[10];

        // ------------------------------------------------------------------
        // construction
        // ------------------------------------------------------------------
        private static Font LoadFont()
        {
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" };
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(candidates, 18);
                if (f != null) return f;
            }
            catch { }
            try { return Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            return null;
        }

        private static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        private static Image Img(string name, Transform parent, float x, float y, float w, float h, Color c)
        {
            var rt = Rect(name, parent, x, y, w, h);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        private static Text Txt(string name, Transform parent, float x, float y, float w, float h,
                                string content, int size, Color c, TextAnchor anchor, bool bold = false)
        {
            var rt = Rect(name, parent, x, y, w, h);
            var t = rt.gameObject.AddComponent<Text>();
            t.text = content;
            t.font = _font;
            t.fontSize = size;
            t.color = c;
            t.alignment = anchor;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = false;
            return t;
        }

        internal static void Build()
        {
            if (_built) return;
            _built = true;

            _font = LoadFont();

            _root = new GameObject("CrossfireTrainerHud");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // ---- panel ----
            _panel = Rect("Panel", _root.transform, 20f, -64f, PanelW, PanelH);
            var bg = _panel.gameObject.AddComponent<Image>();
            bg.color = CBg;
            bg.raycastTarget = false;

            Img("Accent", _panel, 0f, 0f, 3f, PanelH, CAccent);
            Img("Divider", _panel, 16f, -62f, PanelW - 32f, 1f, new Color(1f, 1f, 1f, 0.12f));

            _header = Rect("Header", _panel, 0f, 0f, PanelW, 46f);

            Txt("Title", _panel, 16f, -14f, 250f, 24f, "CROSSFIRE LEGION", 16, CText, TextAnchor.MiddleLeft, true);
            Txt("Ver", _panel, PanelW - 82f, -15f, 66f, 22f, "v" + BuildInfo.Version, 11, CAccent, TextAnchor.MiddleRight);

            _status = Txt("Status", _panel, 16f, -38f, PanelW - 32f, 18f, "", 11, CDim, TextAnchor.MiddleLeft);

            // ---- feature rows ----
            _rowRes = Row("Res", 16f, -70f, "无限资源", "resources");
            _rowGod = Row("God", 16f, -112f, "友方无敌 / 锁血", "god");
            _rowBuild = Row("Build", 16f, -154f, "建造 / 生产加速 ×50", "build");
            _rowDmg = Row("Dmg", 16f, -196f, "超级攻击力", "damage");
            _rowEnergy = Row("Energy", 16f, -238f, "指挥官能量无限", "energy");
            _rowMana = Row("Mana", 16f, -280f, "兵种能量无限", "mana");
            _rowUnitCd = Row("UnitCd", 16f, -322f, "技能无冷却（含兵种）", "unitcd");
            _rowVision = Row("Vision", 16f, -364f, "全图视野", "vision");
            _rowMoveSpeed = Row("MoveSpeed", 16f, -406f, "移动速度翻倍", "movespeed");
            _rowPopCap = Row("PopCap", 16f, -448f, "一键200人口", "popcap");

            // ---- speed (drag slider) ----
            Txt("SpeedLabel", _panel, 16f, -498f, 120f, 18f, "游戏速度", 13, CText, TextAnchor.MiddleLeft);
            _speedValue = Txt("SpeedValue", _panel, PanelW - 152f, -498f, 120f, 18f, "x1.0", 13, CAccent, TextAnchor.MiddleRight, true);

            var track = Img("SpeedTrack", _panel, 16f, -526f, TrackW, 6f, CTrack);
            _speedTrackRt = (RectTransform)track.transform;
            _speedFillRt = (RectTransform)Img("SpeedFill", track.transform, 0f, 0f, 0f, 6f, CAccent).transform;
            _speedKnobRt = (RectTransform)Marker("SpeedKnob", track.transform, 0f, KnobW, CKnob).transform;

            // Grab area: taller than the bar itself so it is easy to catch with the mouse.
            _speedHit = Rect("SpeedHit", _panel, 8f, -517f, PanelW - 16f, 22f);

            Txt("SpeedMin", _panel, 16f, -540f, 60f, 14f, "x0.5", 10, CDim, TextAnchor.MiddleLeft);
            Txt("SpeedMax", _panel, PanelW - 76f, -540f, 60f, 14f, "x5.0", 10, CDim, TextAnchor.MiddleRight);
            Txt("SpeedHint", _panel, 90f, -540f, PanelW - 180f, 14f, "拖动滑条", 10, CDim, TextAnchor.MiddleCenter);

            // ---- resource amount ----
            Txt("AmtLabel", _panel, 16f, -560f, 160f, 16f, "资源设定值", 11, CDim, TextAnchor.MiddleLeft);

            int[] values = { 10000, 50000, 200000, 999999 };
            string[] names = { "10k", "50k", "200k", "999k" };
            _amounts = new Btn[values.Length];
            float aw = (PanelW - 32f - 12f) / 4f;
            for (int i = 0; i < values.Length; i++)
            {
                int v = values[i];
                _amounts[i] = Small("Amt" + i, 16f + i * (aw + 4f), -576f, aw, 28f, names[i], CCard, CText, () =>
                {
                    var p = TrainerPlugin.Instance;
                    p.State.Amount = v;
                    p.PushAmount();
                    p.ShowToast("资源设定为 " + v);
                });
            }

            // ---- action buttons ----
            Small("Reset", 16f, -612f, (PanelW - 32f - 6f) / 2f, 28f, "重置速度", CCard, CText, () =>
            {
                var p = TrainerPlugin.Instance;
                p.State.Speed = 1f;
                p.ApplySpeed();
                p.PushSpeed();
                p.ShowToast("游戏速度重置为 x1.0");
            });

            Small("Instant", 16f + (PanelW - 32f - 6f) / 2f + 6f, -612f, (PanelW - 32f - 6f) / 2f, 28f,
                  "立即完成建造", CCard, CText, () =>
                  {
                      var p = TrainerPlugin.Instance;
                      p.RunLua("Trainer.InstantBuild()");
                      p.ShowToast("立即完成建造");
                  });

            Small("AllOff", 16f, -646f, PanelW - 32f, 26f, "关闭全部修改", new Color(0.55f, 0.18f, 0.16f, 0.85f), CText, () =>
            {
                var p = TrainerPlugin.Instance;
                p.State.Reset();
                foreach (var k in new[] { "resources", "god", "build", "damage", "energy", "mana", "unitcd", "vision", "movespeed", "popcap" })
                    p.SetFeature(k, false);
                p.ApplySpeed();
                p.PushSpeed();
                p.ShowToast("已关闭全部修改");
            });

            Txt("Footer", _panel, 16f, -686f, PanelW - 32f, 16f,
                "F4 显示 / 隐藏面板      仅单人 · AI 遭遇战使用", 10, CDim, TextAnchor.MiddleLeft);

            // ---- toast ----
            var toastRt = Rect("Toast", _root.transform, 0f, 0f, 340f, 36f);
            toastRt.anchorMin = new Vector2(0.5f, 0f);
            toastRt.anchorMax = new Vector2(0.5f, 0f);
            toastRt.pivot = new Vector2(0.5f, 0f);
            toastRt.anchoredPosition = new Vector2(0f, 150f);

            _toastBg = toastRt.gameObject.AddComponent<Image>();
            _toastBg.color = CBg;
            _toastBg.raycastTarget = false;
            Img("ToastAccent", toastRt, 0f, 0f, 3f, 36f, CAccent);
            _toast = Txt("ToastText", toastRt, 12f, 0f, 320f, 36f, "", 13, CText, TextAnchor.MiddleCenter);

            SetVisible(true);
        }

        /// <summary>
        /// Small square anchored to the middle-left of its parent, so it stays centered
        /// on the row no matter how tall the row is. (Anchoring from the top edge with a
        /// positive y put the marker 15px above the card, which looked off by one row.)
        /// </summary>
        private static Image Marker(string name, Transform parent, float x, float size, Color c)
        {
            var rt = Rect(name, parent, 0f, 0f, size, size);
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);

            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        private static Btn Row(string name, float x, float y, string label, string key)
        {
            var card = Img(name, _panel, x, y, PanelW - 32f, 38f, CCard);
            var dot = Marker(name + "Dot", card.transform, 12f, 8f, CDotOff);
            var txt = Txt(name + "Text", card.transform, 30f, 0f, 250f, 38f, label, 14, CText, TextAnchor.MiddleLeft);

            var btn = new Btn { Rt = (RectTransform)card.transform, Bg = card, Dot = dot, Label = txt };
            btn.OnClick = () =>
            {
                var p = TrainerPlugin.Instance;
                p.ToggleFeature(key, label);
            };
            _buttons.Add(btn);
            return btn;
        }

        private static Btn Small(string name, float x, float y, float w, float h, string label, Color bg, Color fg, Action onClick)
        {
            var img = Img(name, _panel, x, y, w, h, bg);
            var txt = Txt(name + "Text", img.transform, 0f, 0f, w, h, label, 12, fg, TextAnchor.MiddleCenter);
            var btn = new Btn { Rt = (RectTransform)img.transform, Bg = img, Label = txt, OnClick = onClick };
            _buttons.Add(btn);
            return btn;
        }

        // ------------------------------------------------------------------
        // per frame
        // ------------------------------------------------------------------
        internal static void Tick(TrainerPlugin p)
        {
            if (!_built) Build();
            if (_root == null) return;

            SyncVisible(p);
            SyncText(p);
            HandleMouse(p);
            SyncToast(p);
        }

        private static void SyncVisible(TrainerPlugin p)
        {
            if (p.MenuVisible == _lastVisible) return;
            _lastVisible = p.MenuVisible;
            SetVisible(p.MenuVisible);
        }

        private static void SetVisible(bool on)
        {
            if (_panel != null) _panel.gameObject.SetActive(on);
        }

        private static void SyncText(TrainerPlugin p)
        {
            string tag = p.InMatch ? (p.PlayerTag ?? "") : "";
            if (tag != _lastTag)
            {
                _lastTag = tag;
                _lastStatus = null;      // force the composed line below to refresh
            }

            string status = p.InMatch
                ? (tag.Length > 0 ? "对局中 · 已连接 · " + tag : "对局中 · 已连接")
                : "未在对局中（进入战斗后生效）";
            if (status != _lastStatus)
            {
                _lastStatus = status;
                _status.text = status;
            }

            if (Math.Abs(p.State.Speed - _lastSpeedVal) > 0.001f)
            {
                _lastSpeedVal = p.State.Speed;
                _speedValue.text = "x" + _lastSpeedVal.ToString("0.0");
                ApplySpeedVisual(_lastSpeedVal);
            }

            SetRow(0, _rowRes, p.State.Resources);
            SetRow(1, _rowGod, p.State.God);
            SetRow(2, _rowBuild, p.State.Build);
            SetRow(3, _rowDmg, p.State.Damage);
            SetRow(4, _rowEnergy, p.State.SkillEnergy);
            SetRow(5, _rowMana, p.State.Mana);
            SetRow(6, _rowUnitCd, p.State.UnitCd);
            SetRow(7, _rowVision, p.State.Vision);
            SetRow(8, _rowMoveSpeed, p.State.MoveSpeed);
            SetRow(9, _rowPopCap, p.State.PopCap);

            if (p.State.Amount != _lastAmount)
            {
                _lastAmount = p.State.Amount;
                int[] values = { 10000, 50000, 200000, 999999 };
                for (int i = 0; i < _amounts.Length; i++)
                    _amounts[i].Bg.color = values[i] == _lastAmount ? CCardOn : CCard;
            }
        }

        private static void SetRow(int idx, Btn row, bool on)
        {
            if (_lastRow[idx] == on) return;
            _lastRow[idx] = on;
            row.Bg.color = on ? CCardOn : CCard;
            row.Dot.color = on ? CDotOn : CDotOff;
        }

        // ------------------------------------------------------------------
        // speed slider
        // ------------------------------------------------------------------
        /// <summary>Moves the fill and the knob to match a speed value.</summary>
        private static void ApplySpeedVisual(float value)
        {
            float t = Mathf.Clamp01((value - SpeedMin) / (SpeedMax - SpeedMin));
            float x = t * (TrackW - KnobW);

            if (_speedKnobRt != null) _speedKnobRt.anchoredPosition = new Vector2(x, 0f);
            if (_speedFillRt != null) _speedFillRt.sizeDelta = new Vector2(x + KnobW * 0.5f, 6f);
        }

        /// <summary>Maps a screen position onto the track and applies the resulting speed.</summary>
        private static void SlideTo(Vector2 screen, TrainerPlugin p)
        {
            if (_speedTrackRt == null) return;

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_speedTrackRt, screen, null, out local)) return;

            float t = Mathf.Clamp01(local.x / TrackW);
            float v = SpeedMin + t * (SpeedMax - SpeedMin);
            v = Mathf.Clamp(Mathf.Round(v / SpeedStep) * SpeedStep, SpeedMin, SpeedMax);

            if (Mathf.Abs(v - p.State.Speed) < 0.0001f) return;

            p.State.Speed = v;
            ApplySpeedVisual(v);

            // TimeScale re-schedules the simulation tick, so don't hammer it on every pixel.
            if (Time.unscaledTime - _lastSlideApply >= 0.08f)
            {
                _lastSlideApply = Time.unscaledTime;
                p.ApplySpeed();
            }
        }

        private static void HandleMouse(TrainerPlugin p)
        {
            if (!p.MenuVisible)
            {
                _dragging = false;
                _sliding = false;
                return;
            }

            if (Input.GetMouseButtonDown(0))
            {
                Vector2 mouse = Input.mousePosition;

                if (_speedHit != null && _speedHit.gameObject.activeInHierarchy &&
                    RectTransformUtility.RectangleContainsScreenPoint(_speedHit, mouse, null))
                {
                    _sliding = true;
                    SlideTo(mouse, p);
                    return;
                }

                if (RectTransformUtility.RectangleContainsScreenPoint(_header, mouse, null))
                {
                    _dragging = true;
                    _dragOffset = mouse - (Vector2)_panel.position;
                    return;
                }

                for (int i = 0; i < _buttons.Count; i++)
                {
                    var b = _buttons[i];
                    if (b.Rt == null || !b.Rt.gameObject.activeInHierarchy) continue;
                    if (b.Rt.parent != null && !b.Rt.parent.gameObject.activeInHierarchy) continue;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(b.Rt, mouse, null)) continue;

                    try { b.OnClick?.Invoke(); } catch (Exception e) { TrainerPlugin.Log?.LogWarning("hud click: " + e.Message); }
                    return;
                }
            }

            if (_sliding)
            {
                if (Input.GetMouseButton(0))
                {
                    SlideTo(Input.mousePosition, p);
                }
                else
                {
                    _sliding = false;
                    p.ApplySpeed();      // land exactly on the value the knob shows
                    p.PushSpeed();       // tell the Lua side once, instead of on every pixel
                    p.ShowToast("游戏速度 x" + p.State.Speed.ToString("0.0"));
                }
            }

            if (_dragging)
            {
                if (Input.GetMouseButton(0))
                {
                    _panel.position = (Vector2)Input.mousePosition - _dragOffset;
                }
                else
                {
                    _dragging = false;
                }
            }
        }

        private static void SyncToast(TrainerPlugin p)
        {
            bool active = p.ToastUntil > Time.unscaledTime && !string.IsNullOrEmpty(p.Toast);
            if (_toastBg == null) return;

            if (_toastBg.gameObject.activeSelf != active) _toastBg.gameObject.SetActive(active);
            if (!active) return;

            if (!string.Equals(p.Toast, _lastToastText, StringComparison.Ordinal))
            {
                _lastToastText = p.Toast;
                _toast.text = p.Toast;
            }

            float remain = p.ToastUntil - Time.unscaledTime;
            float a = Mathf.Clamp01(remain / 0.6f);
            var bg = _toastBg.color; bg.a = 0.95f * a; _toastBg.color = bg;
            var tc = _toast.color; tc.a = a; _toast.color = tc;
        }
    }
}
