using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Owns the VR Settings panel canvas (Phase 2: Graphics tab fully wired).
///
/// Canvas lifecycle rules:
///   - Created as ScreenSpaceOverlay → ScanAndConvertCanvases converts to WorldSpace.
///   - DontDestroyOnLoad mandatory — scene transitions destroy non-persistent GOs.
///   - Register canvas ID in _ownedCanvasIds BEFORE scan fires.
///   - AddComponent&lt;RectTransform&gt;() works here (plain Transform on new GO, IL2CPP
///     does NOT auto-upgrade on SetParent). DO NOT use AddComponent&lt;CanvasGroup&gt;()
///     and then GetComponent&lt;RectTransform&gt;() — CanvasGroup has no [RequireComponent(RT)].
/// </summary>
public static class VRSettingsPanel
{
    private static ManualLogSource Log => Plugin.Log;

    private const int   UILayer  = 5;
    private const float ROW_H    = 60f;   // row height px
    private const float ROW_STEP = 68f;   // row height + gap
    private const float TOP_PAD  = 8f;

    public static int         CanvasInstanceId { get; private set; }
    public static GameObject? RootGO           { get; private set; }

    private static Action<int>?    _removeFromPositioned;
    private static RectTransform?  _graphicsPaneRT;
    private static RectTransform?  _generalPaneRT;
    private static CanvasGroup?    _graphicsGroup;
    private static CanvasGroup?    _generalGroup;
    private static Image?          _graphicsTabImg;
    private static Image?          _generalTabImg;

    // Image colour refresh — called in Show() to re-apply toggle states and static colors.
    private static readonly List<(Image img, TextMeshProUGUI txt, Func<bool> getter)> _toggleRefs    = new();
    private static readonly List<(Image img, Color col)>                               _staticImgRefs = new();

    // Panel image vertex tints — keep in [0,1] range.
    // VRCamera.ApplyReadableImageBoost boosts each Image's material._Color by ×4 once,
    // so the GPU sees: final_HDR = vertex_color × mat_color = tint × (4,4,4,1).
    // E.g. ColBtnOff (0.85,0.85,0.85) × 4 ≈ (3.4,3.4,3.4) — bright enough to survive
    // HDRP auto-exposure, the same way TMP_Text vertex colours do after ApplyReadableTextBoost.
    private static readonly Color ColTabActive   = new(0.50f, 0.85f, 1.00f, 1f); // cyan-blue
    private static readonly Color ColTabInactive = new(0.55f, 0.55f, 0.90f, 1f); // purple
    private static readonly Color ColBtnOn       = new(0.70f, 1.00f, 0.70f, 1f); // green
    private static readonly Color ColBtnOff      = new(0.85f, 0.85f, 0.85f, 1f); // light grey
    private static readonly Color ColNavBtn      = new(0.65f, 0.75f, 1.00f, 1f); // lavender

    // ── Init ──────────────────────────────────────────────────────────────────

    public static GameObject? Init(Action<int> removeFromPositioned)
    {
        _removeFromPositioned = removeFromPositioned;
        _toggleRefs.Clear();
        _staticImgRefs.Clear();
        try
        {
            // ── Canvas ────────────────────────────────────────────────────────
            var root = new GameObject("VRSettingsPanelInternal");
            root.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(root);

            var cv = root.AddComponent<Canvas>();
            cv.renderMode   = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 50;
            CanvasInstanceId = cv.GetInstanceID();
            // NOT added to ownedCanvasIds — RescanCanvasAlpha applies ZTest Always patch,
            // bypassing HDRP auto-exposure so panel graphics render at full brightness.

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(900f, 700f);

            // ── Background ────────────────────────────────────────────────────
            var bgGO   = MakeGO("Background", root.transform);
            var bgRT   = bgGO.AddComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one; bgRT.sizeDelta = Vector2.zero;
            var bgImg  = bgGO.AddComponent<Image>();
            bgImg.color = new Color(0.08f, 0.08f, 0.14f, 0.88f); bgImg.raycastTarget = false;

            // ── Title ─────────────────────────────────────────────────────────
            var titleGO   = MakeGO("Title", root.transform);
            var titleRT   = titleGO.AddComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f); titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f); titleRT.sizeDelta = new Vector2(0f, 70f);
            var titleTxt  = titleGO.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "VR Settings"; titleTxt.fontSize = 44; titleTxt.color = Color.white;
            titleTxt.alignment = TextAlignmentOptions.Center; titleTxt.raycastTarget = false;

            // ── Close button ──────────────────────────────────────────────────
            var closeBtnGO  = MakeGO("CloseButton", root.transform);
            var closeBtnImg = closeBtnGO.AddComponent<Image>();
            var closeBtnRT  = closeBtnGO.GetComponent<RectTransform>();
            closeBtnRT.anchorMin = new Vector2(1f, 1f); closeBtnRT.anchorMax = new Vector2(1f, 1f);
            closeBtnRT.pivot = new Vector2(1f, 1f); closeBtnRT.sizeDelta = new Vector2(80f, 56f);
            closeBtnRT.anchoredPosition = new Vector2(-8f, -8f);
            closeBtnImg.color = new Color(1.00f, 0.45f, 0.40f, 1f); // red tint (mat ×4 → HDR)
            _staticImgRefs.Add((closeBtnImg, new Color(1.00f, 0.45f, 0.40f, 1f)));
            closeBtnGO.AddComponent<Button>().onClick.AddListener(new Action(Hide));
            var closeLblGO   = MakeGO("CloseLabel", closeBtnGO.transform);
            var closeLblRT   = closeLblGO.AddComponent<RectTransform>();
            closeLblRT.anchorMin = Vector2.zero; closeLblRT.anchorMax = Vector2.one; closeLblRT.sizeDelta = Vector2.zero;
            var closeLbl = closeLblGO.AddComponent<TextMeshProUGUI>();
            closeLbl.text = "\u2715"; closeLbl.fontSize = 32; closeLbl.color = Color.white;
            closeLbl.alignment = TextAlignmentOptions.Center; closeLbl.raycastTarget = false;

            // ── Tab row ───────────────────────────────────────────────────────
            var tabRowGO   = MakeGO("TabRow", root.transform);
            var tabRowRT   = tabRowGO.AddComponent<RectTransform>();
            tabRowRT.anchorMin = new Vector2(0f, 1f); tabRowRT.anchorMax = new Vector2(1f, 1f);
            tabRowRT.pivot = new Vector2(0.5f, 1f); tabRowRT.sizeDelta = new Vector2(0f, 56f);
            tabRowRT.anchoredPosition = new Vector2(0f, -70f);

            MakeTabButton("GraphicsTab", "Graphics", tabRowGO.transform, new Vector2(-110f, 0f), true,
                          out _graphicsTabImg, out var graphicsBtn);
            MakeTabButton("GeneralTab",  "General",  tabRowGO.transform, new Vector2( 110f, 0f), false,
                          out _generalTabImg,  out var generalBtn);
            graphicsBtn.onClick.AddListener(new Action(ActivateGraphicsTab));
            generalBtn.onClick.AddListener(new Action(ActivateGeneralTab));

            // ── Scrollable content panes (topOffset = -126 = below 70px title + 56px tabs) ──
            // No ScrollRect / RectMask2D — those corrupt HDRP stencil in WorldSpace canvas.
            // Scrolling done by shifting content.anchoredPosition.y via ▲/▼ buttons.
            var (graphicsPaneGO, graphicsContent) = MakeScrollablePane("GraphicsPane", root.transform, -126f);
            var (generalPaneGO,  generalContent)  = MakeScrollablePane("GeneralPane",  root.transform, -126f);
            _graphicsPaneRT = graphicsPaneGO.GetComponent<RectTransform>();
            _generalPaneRT  = generalPaneGO.GetComponent<RectTransform>();
            _graphicsGroup  = graphicsPaneGO.AddComponent<CanvasGroup>();
            _generalGroup   = generalPaneGO.AddComponent<CanvasGroup>();
            // Start with General pane hidden — shift off-canvas via localPosition.
            // VRCamera.ApplyReadableImageBoost handles Image brightness; no alpha tricks needed.
            SetPaneVisible(_generalPaneRT, _generalGroup, false);

            // ── Graphics tab rows ─────────────────────────────────────────────
            float gy = TOP_PAD;

            // Family B — dedicated Game.Instance bool setters
            AddToggleRow(graphicsContent, ref gy, "VSync",
                () => ReadBool("vsync"),
                v => { try { Game.Instance?.SetVsync(v); PlayerPrefs.SetInt("vsync", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] vsync: {ex.Message}"); } });

            AddToggleRow(graphicsContent, ref gy, "Depth Blur",
                () => ReadBool("depthBlur"),
                v => { try { Game.Instance?.SetDepthBlur(v); PlayerPrefs.SetInt("depthBlur", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] depthBlur: {ex.Message}"); } });

            AddToggleRow(graphicsContent, ref gy, "Dithering",
                () => ReadBool("dithering"),
                v => { try { Game.Instance?.SetDithering(v); PlayerPrefs.SetInt("dithering", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] dithering: {ex.Message}"); } });

            AddToggleRow(graphicsContent, ref gy, "Screen Space Refl.",
                () => ReadBool("screenSpaceReflection"),
                v => { try { Game.Instance?.SetScreenSpaceReflection(v); PlayerPrefs.SetInt("screenSpaceReflection", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] ssr: {ex.Message}"); } });

            // Family A — gameSettingControls + SessionData guard (in-gameplay only)
            foreach (var row in new (string lbl, string id)[]
            {
                ("Motion Blur",       "motionBlur"),
                ("Bloom",             "bloom"),
                ("Colour Grading",    "colourGrading"),
                ("Film Grain",        "filmGrain"),
                ("Flickering Lights", "flickeringLights"),
            })
            {
                var capturedId = row.id;
                AddToggleRow(graphicsContent, ref gy, row.lbl,
                    () => ReadBool(capturedId),
                    v => SetFamilyA(capturedId, v));
            }

            // Family C — enum via Game.Instance
            AddPrevNextRow(graphicsContent, ref gy, "AA Mode",
                new[] { "Off", "SMAA", "TAA", "DLSS" },
                () => ClampIdx(ReadInt("aaMode"), 3),
                v => { try { Game.Instance?.SetAAMode(v); PlayerPrefs.SetInt("aaMode", v); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] aaMode: {ex.Message}"); } });

            AddPrevNextRow(graphicsContent, ref gy, "AA Quality",
                new[] { "Low", "Medium", "High" },
                () => ClampIdx(ReadInt("aaQuality"), 2),
                v => { try { Game.Instance?.SetAAQuality(v); PlayerPrefs.SetInt("aaQuality", v); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] aaQuality: {ex.Message}"); } });

            // Family D — float via Game.Instance
            var ldVals = new[] { 0.5f, 0.75f, 1.0f, 1.5f, 2.0f };
            AddPrevNextRow(graphicsContent, ref gy, "Light Distance",
                new[] { "0.5×", "0.75×", "1.0×", "1.5×", "2.0×" },
                () => FloatToIdx(ReadFloat("lightDistance", 1f), ldVals),
                v => { try { Game.Instance?.SetLightDistance(ldVals[v]); PlayerPrefs.SetFloat("lightDistance", ldVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] lightDist: {ex.Message}"); } });

            // Family E — frame cap: toggle + value
            AddToggleRow(graphicsContent, ref gy, "Frame Cap",
                () => ReadBool("enableFrameCap"),
                v => { try { Game.Instance?.SetEnableFrameCap(v); PlayerPrefs.SetInt("enableFrameCap", v ? 1 : 0); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] enableFrameCap: {ex.Message}"); } });

            var fcVals = new[] { 30, 60, 90, 120, 144, 165, 240, 0 };
            AddPrevNextRow(graphicsContent, ref gy, "Cap Value",
                new[] { "30", "60", "90", "120", "144", "165", "240", "Unlimited" },
                () => IntToIdx(ReadInt("frameCap", 60), fcVals),
                v => { try { Game.Instance?.SetFrameCap(fcVals[v]); PlayerPrefs.SetInt("frameCap", fcVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] frameCap: {ex.Message}"); } });

            FinalizeContent(graphicsContent, gy);

            // ── General tab ───────────────────────────────────────────────────
            float gy2 = TOP_PAD;

            // Family C — FOV (Game.Instance.SetFOV)
            var fovVals = new[] { 50, 60, 70, 80, 90, 100, 110, 120 };
            AddPrevNextRow(generalContent, ref gy2, "FOV",
                new[] { "50", "60", "70", "80", "90", "100", "110", "120" },
                () => IntToIdx(ReadInt("fpsfov", 90), fovVals),
                v => { try { Game.Instance?.SetFOV(fovVals[v]); PlayerPrefs.SetInt("fpsfov", fovVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] fpsfov: {ex.Message}"); } });

            // Family A — Head Bob
            AddToggleRow(generalContent, ref gy2, "Head Bob",
                () => ReadBool("headBob"),
                v => SetFamilyA("headBob", v));

            // Family A — Rain Detail
            AddToggleRow(generalContent, ref gy2, "Rain Detail",
                () => ReadBool("rainDetail"),
                v => SetFamilyA("rainDetail", v));

            // Family A — Dynamic Resolution
            AddToggleRow(generalContent, ref gy2, "Dynamic Res.",
                () => ReadBool("dynamicResolution"),
                v => {
                    try {
                        var drc = DynamicResolutionController.Instance;
                        if (drc != null) drc.SetDynamicResolutionEnabled(v);
                        SetFamilyA("dynamicResolution", v);
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRSettings] dynamicResolution: {ex.Message}"); }
                });

            // Family A — UI Scale (0=Small, 1=Normal, 2=Large)
            AddPrevNextRow(generalContent, ref gy2, "UI Scale",
                new[] { "Small", "Normal", "Large" },
                () => ReadInt("uiScale", 1),
                v => { try { PlayerPrefs.SetInt("uiScale", v); PlayerPrefs.Save(); SetFamilyA("uiScale", v > 0); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] uiScale: {ex.Message}"); } });

            // Family A — Text Speed (0=Slow, 1=Normal, 2=Fast)
            AddPrevNextRow(generalContent, ref gy2, "Text Speed",
                new[] { "Slow", "Normal", "Fast" },
                () => ReadInt("textspeed", 1),
                v => { try { PlayerPrefs.SetInt("textspeed", v); PlayerPrefs.Save(); SetFamilyA("textspeed", v > 0); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] textspeed: {ex.Message}"); } });

            // Family A — Word-by-word Text
            AddToggleRow(generalContent, ref gy2, "Word-by-word",
                () => ReadBool("wordByWordText"),
                v => SetFamilyA("wordByWordText", v));

            // Family D — Draw Distance (Game.Instance.SetDrawDistance)
            var ddVals  = new[] { 0.5f, 0.75f, 1.0f, 1.5f, 2.0f };
            AddPrevNextRow(generalContent, ref gy2, "Draw Distance",
                new[] { "50%", "75%", "100%", "150%", "200%" },
                () => FloatToIdx(ReadFloat("drawDist", 1f), ddVals),
                v => { try { Game.Instance?.SetDrawDistance(ddVals[v]); PlayerPrefs.SetFloat("drawDist", ddVals[v]); PlayerPrefs.Save(); }
                       catch (Exception ex) { Log.LogWarning($"[VRSettings] drawDist: {ex.Message}"); } });

            FinalizeContent(generalContent, gy2);

            // Start with Graphics tab active
            SetTabVisual(true);

            RootGO = root;
            root.SetActive(false);  // hidden by default — F10 to open
            Log.LogInfo("[VRSettingsPanel] Init complete (ScreenSpaceOverlay, sortOrder=50).");
            return root;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRSettingsPanel] Init failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    public static void Show()
    {
        if (RootGO == null) return;
        RootGO.SetActive(true);
        // Re-apply toggle states and static colors in case they changed while hidden.
        RefreshColors();
        // Re-enforce tab pane visibility. Graphics tab shown, General tab off-canvas.
        SetPaneVisible(_graphicsPaneRT, _graphicsGroup, true);
        SetPaneVisible(_generalPaneRT,  _generalGroup,  false);
        _removeFromPositioned?.Invoke(CanvasInstanceId);
        Log.LogInfo("[VRSettingsPanel] Shown.");
    }

    public static void Hide()
    {
        if (RootGO == null) return;
        RootGO.SetActive(false);
        Log.LogInfo("[VRSettingsPanel] Hidden.");
    }

    public static void Toggle()
    {
        if (RootGO == null) return;
        if (RootGO.activeSelf) Hide(); else Show();
    }

    private static void RefreshColors()
    {
        foreach (var (img, txt, getter) in _toggleRefs)
        {
            if (img == null || txt == null) continue;
            try { SetToggleVisual(img, txt, getter()); } catch { }
        }
        foreach (var (img, col) in _staticImgRefs)
        {
            if (img != null) img.color = col;
        }
        SetTabVisual(true); // always open on Graphics tab
    }

    public static void Destroy()
    {
        if (RootGO == null) return;
        try { UnityEngine.Object.Destroy(RootGO); } catch { }
        RootGO = null;
        _graphicsPaneRT = null;
        _generalPaneRT  = null;
        _graphicsGroup  = null;
        _generalGroup   = null;
        _toggleRefs.Clear();
        _staticImgRefs.Clear();
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    private static void ActivateGraphicsTab()
    {
        SetTabVisual(true);
        SetPaneVisible(_graphicsPaneRT, _graphicsGroup, true);
        SetPaneVisible(_generalPaneRT,  _generalGroup,  false);
        Log.LogInfo("[VRSettingsPanel] Graphics tab active.");
    }

    private static void ActivateGeneralTab()
    {
        SetTabVisual(false);
        SetPaneVisible(_graphicsPaneRT, _graphicsGroup, false);
        SetPaneVisible(_generalPaneRT,  _generalGroup,  true);
        Log.LogInfo("[VRSettingsPanel] General tab active.");
    }

    // Hides/shows a pane by shifting it off-canvas (localPosition.x = 9999 when hidden).
    // VRCamera's RescanCanvasAlpha resets CanvasGroup.alpha to 1 every 30 frames on other
    // canvases, but the isVrPanel bypass means our CanvasGroups are left alone.
    // Visibility is purely positional — no alpha hacks needed.
    private static void SetPaneVisible(RectTransform? rt, CanvasGroup? cg, bool visible)
    {
        if (rt != null)
        {
            var lp = rt.localPosition;
            rt.localPosition = new Vector3(visible ? 0f : 9999f, lp.y, lp.z);
        }
        if (cg != null) { cg.interactable = visible; cg.blocksRaycasts = visible; }
    }

    private static void SetTabVisual(bool graphicsActive)
    {
        if (_graphicsTabImg != null) _graphicsTabImg.color = graphicsActive ? ColTabActive : ColTabInactive;
        if (_generalTabImg  != null) _generalTabImg.color  = graphicsActive ? ColTabInactive : ColTabActive;
    }

    // ── Scrollable pane builder ───────────────────────────────────────────────
    // No ScrollRect or RectMask2D — both corrupt HDRP's stencil buffer in WorldSpace
    // canvas, causing the world geometry to disappear behind the masked region.
    // Scrolling is done by shifting content.anchoredPosition.y directly from ▲/▼ buttons.
    // Content overflows the pane bottom visually (no GPU clipping), which is acceptable.

    private static (GameObject pane, RectTransform content) MakeScrollablePane(
        string name, Transform parent, float topOffset)
    {
        var paneGO = MakeGO(name, parent);
        var paneRT = paneGO.AddComponent<RectTransform>();
        paneRT.anchorMin = new Vector2(0f, 0f);
        paneRT.anchorMax = new Vector2(1f, 1f);
        paneRT.offsetMin = new Vector2(10f,  20f);
        paneRT.offsetMax = new Vector2(-10f, topOffset);

        // Content: top-anchored in pane; height set by FinalizeContent after rows added.
        var contentGO = MakeGO(name + "Content", paneGO.transform);
        var contentRT = contentGO.AddComponent<RectTransform>();
        contentRT.anchorMin        = new Vector2(0f, 1f);
        contentRT.anchorMax        = new Vector2(1f, 1f);
        contentRT.pivot            = new Vector2(0.5f, 1f);
        contentRT.anchoredPosition = Vector2.zero;
        contentRT.sizeDelta        = new Vector2(0f, 100f);

        // Scroll arrows at right corners of the pane — shift content.anchoredPosition.y.
        // topOffset captured so each arrow can compute maxScroll from content vs. viewport.
        AddScrollArrow(name + "Up",   paneGO.transform, up: true,  contentRT, topOffset);
        AddScrollArrow(name + "Down", paneGO.transform, up: false, contentRT, topOffset);

        return (paneGO, contentRT);
    }

    private static void AddScrollArrow(
        string name, Transform parent, bool up, RectTransform content, float topOffset)
    {
        var go  = MakeGO(name, parent);
        var img = go.AddComponent<Image>();
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, up ? 1f : 0f);
        rt.anchorMax = new Vector2(1f, up ? 1f : 0f);
        rt.pivot     = new Vector2(1f, up ? 1f : 0f);
        rt.sizeDelta = new Vector2(55f, 55f);
        rt.anchoredPosition = new Vector2(-5f, up ? -5f : 5f);
        img.color = ColNavBtn;
        _staticImgRefs.Add((img, ColNavBtn));

        var lblGO = MakeGO("Lbl", go.transform);
        var lblRT = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one; lblRT.sizeDelta = Vector2.zero;
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text = up ? "\u25B2" : "\u25BC";   // ▲ / ▼
        lbl.fontSize = 26; lbl.color = Color.white;
        lbl.alignment = TextAlignmentOptions.Center; lbl.raycastTarget = false;

        // viewportH ≈ canvas reference height (700) minus title+tabs (126) minus bottom pad (20)
        float viewportH = 700f + topOffset - 20f;
        float scrollStep = ROW_STEP * 2.5f;

        go.AddComponent<Button>().onClick.AddListener(new Action(() =>
        {
            float contentH = content.sizeDelta.y;
            float maxY     = Mathf.Max(0f, contentH - viewportH);
            // ▼ = reveal lower rows = shift content UP = increase anchoredPosition.y
            // ▲ = back towards top  = decrease anchoredPosition.y towards 0
            float newY = Mathf.Clamp(content.anchoredPosition.y + (up ? -scrollStep : scrollStep), 0f, maxY);
            content.anchoredPosition = new Vector2(0f, newY);
        }));
    }

    private static void FinalizeContent(RectTransform content, float usedHeight)
    {
        content.sizeDelta     = new Vector2(0f, usedHeight + TOP_PAD);
        content.anchoredPosition = Vector2.zero;   // reset to top
    }

    // ── Row builders ──────────────────────────────────────────────────────────

    private static void AddToggleRow(
        RectTransform content, ref float yTop,
        string label, Func<bool> getter, Action<bool> setter)
    {
        var rowGO = MakeGO("Row_" + label, content.transform);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f); rowRT.sizeDelta = new Vector2(0f, ROW_H);
        rowRT.anchoredPosition = new Vector2(0f, -yTop);

        AddRowLabel(rowGO.transform, label);

        // Toggle button anchored to row's right edge
        var btnGO  = MakeGO("TogBtn", rowGO.transform);
        var btnImg = btnGO.AddComponent<Image>();
        var btnRT  = btnGO.GetComponent<RectTransform>();
        btnRT.anchorMin = new Vector2(1f, 0.5f); btnRT.anchorMax = new Vector2(1f, 0.5f);
        btnRT.pivot     = new Vector2(1f, 0.5f);
        btnRT.sizeDelta = new Vector2(130f, ROW_H - 12f);
        btnRT.anchoredPosition = new Vector2(-8f, 0f);

        var btnLblGO   = MakeGO("Lbl", btnGO.transform);
        var btnLblRT   = btnLblGO.AddComponent<RectTransform>();
        btnLblRT.anchorMin = Vector2.zero; btnLblRT.anchorMax = Vector2.one; btnLblRT.sizeDelta = Vector2.zero;
        var btnTxt = btnLblGO.AddComponent<TextMeshProUGUI>();
        btnTxt.fontSize = 28; btnTxt.color = Color.white;
        btnTxt.alignment = TextAlignmentOptions.Center; btnTxt.raycastTarget = false;

        bool curVal = false;
        try { curVal = getter(); } catch { }
        SetToggleVisual(btnImg, btnTxt, curVal);
        _toggleRefs.Add((btnImg, btnTxt, getter));

        btnGO.AddComponent<Button>().onClick.AddListener(new Action(() =>
        {
            bool newVal = false;
            try { newVal = !getter(); } catch { }
            try { setter(newVal); } catch (Exception ex) { Log.LogWarning($"[VRSettings] toggle '{label}': {ex.Message}"); }
            SetToggleVisual(btnImg, btnTxt, newVal);
            Log.LogInfo($"[VRSettings] {label} → {(newVal ? "ON" : "OFF")}");
        }));

        yTop += ROW_STEP;
    }

    private static void AddPrevNextRow(
        RectTransform content, ref float yTop,
        string label, string[] options, Func<int> getter, Action<int> setter)
    {
        var rowGO = MakeGO("Row_" + label, content.transform);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f); rowRT.sizeDelta = new Vector2(0f, ROW_H);
        rowRT.anchoredPosition = new Vector2(0f, -yTop);

        AddRowLabel(rowGO.transform, label);

        // Layout (right-anchored): [◄ 50] [value 120] [► 50] with 8px gaps, 8px from right
        //   ► right edge:  -8
        //   ► left edge:   -58    anchoredPos.x = -33
        //   value right:  -63
        //   value left:   -183   anchoredPos.x = -123
        //   ◄ right edge: -188
        //   ◄ left edge:  -238   anchoredPos.x = -213

        var prevGO  = MakeGO("Prev", rowGO.transform);
        var prevImg = prevGO.AddComponent<Image>();
        var prevRT  = prevGO.GetComponent<RectTransform>();
        prevRT.anchorMin = new Vector2(1f, 0.5f); prevRT.anchorMax = new Vector2(1f, 0.5f);
        prevRT.pivot = new Vector2(0.5f, 0.5f); prevRT.sizeDelta = new Vector2(50f, ROW_H - 12f);
        prevRT.anchoredPosition = new Vector2(-213f, 0f);
        prevImg.color = ColNavBtn;
        _staticImgRefs.Add((prevImg, ColNavBtn));
        AddNavBtnLabel(prevGO.transform, "\u25C4");

        var valGO   = MakeGO("Val", rowGO.transform);
        var valRT   = valGO.AddComponent<RectTransform>();
        valRT.anchorMin = new Vector2(1f, 0.5f); valRT.anchorMax = new Vector2(1f, 0.5f);
        valRT.pivot = new Vector2(0.5f, 0.5f); valRT.sizeDelta = new Vector2(120f, ROW_H - 12f);
        valRT.anchoredPosition = new Vector2(-123f, 0f);
        var valTxt  = valGO.AddComponent<TextMeshProUGUI>();
        valTxt.fontSize = 26; valTxt.color = Color.white;
        valTxt.alignment = TextAlignmentOptions.Center; valTxt.raycastTarget = false;

        var nextGO  = MakeGO("Next", rowGO.transform);
        var nextImg = nextGO.AddComponent<Image>();
        var nextRT  = nextGO.GetComponent<RectTransform>();
        nextRT.anchorMin = new Vector2(1f, 0.5f); nextRT.anchorMax = new Vector2(1f, 0.5f);
        nextRT.pivot = new Vector2(0.5f, 0.5f); nextRT.sizeDelta = new Vector2(50f, ROW_H - 12f);
        nextRT.anchoredPosition = new Vector2(-33f, 0f);
        nextImg.color = ColNavBtn;
        _staticImgRefs.Add((nextImg, ColNavBtn));
        AddNavBtnLabel(nextGO.transform, "\u25BA");

        int curIdx = 0;
        try { curIdx = ClampIdx(getter(), options.Length - 1); } catch { }
        valTxt.text = options[curIdx];

        prevGO.AddComponent<Button>().onClick.AddListener(new Action(() =>
        {
            int cur = 0;
            try { cur = getter(); } catch { }
            int newIdx = Math.Max(0, cur - 1);
            try { setter(newIdx); } catch (Exception ex) { Log.LogWarning($"[VRSettings] prev '{label}': {ex.Message}"); }
            valTxt.text = options[ClampIdx(newIdx, options.Length - 1)];
            Log.LogInfo($"[VRSettings] {label} → {options[ClampIdx(newIdx, options.Length - 1)]}");
        }));

        nextGO.AddComponent<Button>().onClick.AddListener(new Action(() =>
        {
            int cur = 0;
            try { cur = getter(); } catch { }
            int newIdx = Math.Min(options.Length - 1, cur + 1);
            try { setter(newIdx); } catch (Exception ex) { Log.LogWarning($"[VRSettings] next '{label}': {ex.Message}"); }
            valTxt.text = options[ClampIdx(newIdx, options.Length - 1)];
            Log.LogInfo($"[VRSettings] {label} → {options[ClampIdx(newIdx, options.Length - 1)]}");
        }));

        yTop += ROW_STEP;
    }

    private static void AddPlaceholderRow(RectTransform content, ref float yTop, string label)
    {
        var rowGO = MakeGO("Row_" + label, content.transform);
        var rowRT = rowGO.AddComponent<RectTransform>();
        rowRT.anchorMin = new Vector2(0f, 1f); rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot = new Vector2(0.5f, 1f); rowRT.sizeDelta = new Vector2(0f, ROW_H);
        rowRT.anchoredPosition = new Vector2(0f, -yTop);

        AddRowLabel(rowGO.transform, label);

        var valGO  = MakeGO("Val", rowGO.transform);
        var valRT  = valGO.AddComponent<RectTransform>();
        valRT.anchorMin = new Vector2(0.55f, 0f); valRT.anchorMax = new Vector2(1f, 1f);
        valRT.offsetMin = Vector2.zero; valRT.offsetMax = new Vector2(-8f, 0f);
        var valTxt = valGO.AddComponent<TextMeshProUGUI>();
        valTxt.text = "[---]"; valTxt.fontSize = 26;
        valTxt.color = new Color(0.55f, 0.55f, 0.55f, 1f);
        valTxt.alignment = TextAlignmentOptions.MidlineRight; valTxt.raycastTarget = false;

        yTop += ROW_STEP;
    }

    // ── Shared layout helpers ─────────────────────────────────────────────────

    private static void AddRowLabel(Transform row, string text)
    {
        var lblGO   = MakeGO("Lbl", row);
        var lblRT   = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = new Vector2(0f, 0f); lblRT.anchorMax = new Vector2(0.55f, 1f);
        lblRT.offsetMin = new Vector2(10f, 0f); lblRT.offsetMax = Vector2.zero;
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text = text; lbl.fontSize = 30; lbl.color = Color.white;
        lbl.alignment = TextAlignmentOptions.MidlineLeft; lbl.raycastTarget = false;
    }

    private static void AddNavBtnLabel(Transform parent, string text)
    {
        var lblGO   = MakeGO("Lbl", parent);
        var lblRT   = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one; lblRT.sizeDelta = Vector2.zero;
        var lbl = lblGO.AddComponent<TextMeshProUGUI>();
        lbl.text = text; lbl.fontSize = 22; lbl.color = Color.white;
        lbl.alignment = TextAlignmentOptions.Center; lbl.raycastTarget = false;
    }

    private static void SetToggleVisual(Image img, TextMeshProUGUI txt, bool on)
    {
        img.color = on ? ColBtnOn : ColBtnOff;
        txt.text  = on ? "ON" : "OFF";
    }

    private static void MakeTabButton(
        string name, string label, Transform parent,
        Vector2 pos, bool startActive,
        out Image bgImg, out Button button)
    {
        var go  = MakeGO(name, parent);
        var img = go.AddComponent<Image>();
        var rt  = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(200f, 50f); rt.anchoredPosition = pos;
        img.color = startActive ? ColTabActive : ColTabInactive;
        button = go.AddComponent<Button>();
        bgImg  = img;
        var lblGO   = MakeGO(name + "Label", go.transform);
        var lblRT   = lblGO.AddComponent<RectTransform>();
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one; lblRT.sizeDelta = Vector2.zero;
        var txt = lblGO.AddComponent<TextMeshProUGUI>();
        txt.text = label; txt.fontSize = 30; txt.color = Color.white;
        txt.alignment = TextAlignmentOptions.Center; txt.raycastTarget = false;
    }

    private static GameObject MakeGO(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = UILayer;
        go.transform.SetParent(parent, false);
        return go;
    }

    // ── Settings read helpers ─────────────────────────────────────────────────

    private static bool ReadBool(string id)
    {
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null) return ppc.GetSettingInt(id) != 0;
        }
        catch { }
        return PlayerPrefs.GetInt(id, 0) != 0;
    }

    private static int ReadInt(string id, int def = 0)
    {
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null) return ppc.GetSettingInt(id);
        }
        catch { }
        return PlayerPrefs.GetInt(id, def);
    }

    private static float ReadFloat(string id, float def = 0f) =>
        PlayerPrefs.GetFloat(id, def);  // no GetSettingFloat on PlayerPrefsController

    // ── Settings write helpers ────────────────────────────────────────────────

    /// <summary>
    /// Family A write: uses gameSettingControls list + OnToggleChanged (in-gameplay only).
    /// Falls back to raw PlayerPrefs when session or controller not available.
    /// </summary>
    private static void SetFamilyA(string id, bool newVal)
    {
        try
        {
            var ppc = PlayerPrefsController.Instance;
            if (ppc != null)
            {
                // Find by indexed loop — avoids Il2Cpp delegate conversion issues with Find(predicate)
                PlayerPrefsController.GameSetting? gs = null;
                var controls = ppc.gameSettingControls;
                if (controls != null)
                    for (int i = 0; i < controls.Count; i++)
                    {
                        var s = controls[i];
                        if (s != null && s.identifier == id) { gs = s; break; }
                    }

                if (gs != null)
                {
                    gs.intValue = newVal ? 1 : 0;
                    if (SessionData.Instance != null)
                        ppc.OnToggleChanged(id, false);
                    else
                        PlayerPrefs.SetInt(id, gs.intValue);
                }
                else
                {
                    PlayerPrefs.SetInt(id, newVal ? 1 : 0);
                }
            }
            else
            {
                PlayerPrefs.SetInt(id, newVal ? 1 : 0);
            }
            PlayerPrefs.Save();
            Log.LogInfo($"[VRSettings] FamilyA '{id}' → {newVal}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRSettings] FamilyA '{id}': {ex.Message}");
        }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static int ClampIdx(int v, int max) => v < 0 ? 0 : v > max ? max : v;

    private static int FloatToIdx(float val, float[] arr)
    {
        int   best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < arr.Length; i++)
        {
            float d = Math.Abs(arr[i] - val);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    private static int IntToIdx(int val, int[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] == val) return i;
        return 0;
    }
}
