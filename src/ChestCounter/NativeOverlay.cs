using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>
/// Small always-on-top counter window drawn with plain Win32/GDI (no WinForms needed).
/// Left-drag to move it, right-click to hide it (Ctrl+Shift+O or the counter's menu shows it again).
/// Visible over Fortnite when the game runs in "windowed fullscreen" (not exclusive fullscreen).
/// </summary>
/// <summary>What the overlay shows. <see cref="Current"/> null = no match in progress.</summary>
/// <param name="Detail">Optional secondary text drawn small and grey after the value.</param>
public sealed record OverlayRow(string Label, string Value, bool Treasure = false, string? Detail = null);

/// <param name="Current">Right counter (match in progress, or last match); null = "—".</param>
/// <param name="CurrentLive">Right counter is the match in progress (green).</param>
/// <param name="Badge">Header badge ("● EN DIRECT" / "● LIVE", "● PARTIE EN COURS" / "● MATCH IN PROGRESS") or null.</param>
public sealed record OverlayView(string Total, string? Current, string CurrentCaption, bool CurrentLive, string? Badge, IReadOnlyList<OverlayRow> Rows, string Treasures = "0", string Session = "0", string? Last = null,
    string? LastLabel = null, string? ThirdLabel = null);
// LastLabel : remplace « Dernière » dans le HUD (mode PS5 : « Aujourd'hui »).
// ThirdLabel : la 3e ligne du HUD devient « libellé • Treasures » au lieu de l'étoile (mode PS5 : « Epic »).

[SupportedOSPlatform("windows")]
public static class NativeOverlay
{
    private static Thread? _thread;
    private static IntPtr _hwnd;
    private static volatile OverlayView _view = new("0", null, OverlayModel.LastMatchCaption, false, null, Array.Empty<OverlayRow>());

    // ---- Style « bar » : barre déroulante (fermée → Résumé → Coffres · chances) --------------------------------
    private static volatile BarView? _bar;
    /// <summary>0 = fermée (juste le total), 1 = onglet Résumé, 2 = onglet Coffres · chances.</summary>
    private static volatile int _panel;
    private static RECT _chevRect, _tab1Rect, _tab2Rect, _openRect;

    /// <summary>Ce que fait « Ouvrir le compteur » (bouton de la barre, Ctrl+Maj+C) : fourni par Program.</summary>
    public static Action? OpenCounter { get; set; }

    /// <summary>True when the global Ctrl+Shift+C shortcut (open the counter) could be registered.</summary>
    public static bool OpenHotkeyRegistered { get; private set; }
    private static IntPtr _fontBarTotal, _fontBarText, _fontBarBold, _fontBarSmall, _fontBarBadge;
    private static int _barH, _barTextH, _barSmallH, _barTotalH;

    // Layout (computed by Relayout, used by Paint; both on the overlay thread).
    private static int _headerH, _bigH, _captionH, _rowH, _col1W, _col2W, _labelW;
    private static IntPtr _fontHeader, _fontCaption, _fontLabel;
    private static IntPtr _brushBorder, _brushLine;
    private static string Header => L.T("COFFRES", "CHESTS", "COFRES");
    private const string CaptionTotal = "total";
    private static Action<int, int>? _onMoved;
    private static Action<bool>? _onLockChanged;
    private static volatile bool _locked;
    private static bool _offscreen;
    private static int _stableW;
    private static RECT _lockRect; // lock button, client coordinates
    private static IntPtr _fontIcon;
    private static WndProcDelegate? _wndProc; // keep the delegate alive
    private static IntPtr _fontBig, _fontSmall, _brushBg;
    private static float _scale = 1f;    // display scaling × size chosen by the player
    private static float _dpiScale = 1f; // display scaling only
    private static volatile int _sizePercent = 100;
    private static volatile int _opacityPercent = 100;

    /// <summary>"hud" (figures only, under the minimap, like the game's HUD) or "classic" (dark card).</summary>
    private static volatile string _style = "bar";
    private static IntPtr _fontHudBig, _fontHudSmall;
    private static int _hudBigH, _hudSmallH;

    /// <summary>Pixels of this color are fully transparent (styles without a background). Almost black, so that the
    /// anti-aliased edge of the black outline around the text blends into it without any colored fringe.</summary>
    private static readonly uint KeyColor = 0x00010001;

    public static string[] Styles => new[] { "bar", "hud", "classic" };

    /// <summary>Changes the look of the overlay at once.</summary>
    public static void SetStyle(string style)
    {
        _style = Array.IndexOf(Styles, style) >= 0 ? style : "bar";
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static bool Keyed => _style == "hud";

    private static bool IsBar => _style == "bar";

    private const int MinWidth = 240, Pad = 16, ColGap = 36;

    /// <summary>Size and opacity of the overlay (percent). Applied at once when the overlay is shown.</summary>
    public static void SetAppearance(int sizePercent, int opacityPercent)
    {
        _sizePercent = OverlayAppearance.ClampSize(sizePercent);
        _opacityPercent = OverlayAppearance.ClampOpacity(opacityPercent);
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_APPEARANCE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static byte Alpha => (byte)Math.Round(_opacityPercent * 255 / 100.0);

    public static bool IsRunning => _hwnd != IntPtr.Zero;

    /// <summary>True when the overlay window is shown (not hidden with the shortcut or the menu).</summary>
    public static bool IsVisible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    /// <summary>Closes the overlay window and its thread (overlay turned off in the settings). Start() can open it again.</summary>
    public static void Stop()
    {
        var thread = _thread;
        if (thread == null)
        {
            return;
        }
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
        }
        thread.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>True when the global Ctrl+Shift+O shortcut could be registered.</summary>
    public static bool HotkeyRegistered { get; private set; }

    /// <summary>True when the global Ctrl+Shift+L shortcut (lock/unlock) could be registered.</summary>
    public static bool LockHotkeyRegistered { get; private set; }

    /// <summary>True when the global Ctrl+Shift+E shortcut (open / switch the bar's tabs) could be registered.</summary>
    public static bool PanelHotkeyRegistered { get; private set; }

    /// <summary>Locked = click-through: the mouse goes through the overlay and it cannot be moved.</summary>
    public static bool IsLocked => _locked;

    public static void Start(int? x, int? y, bool locked, Action<int, int> onMoved, Action<bool> onLockChanged)
    {
        if (_thread != null)
        {
            return;
        }

        _onMoved = onMoved;
        _offscreen = x <= -10000; // --overlay-preview: never brought back on screen
        _onLockChanged = onLockChanged;
        _locked = locked;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                Run(x, y, ready);
            }
            catch (Exception ex)
            {
                Console.WriteLine(L.T($"Overlay indisponible : {ex.Message}", $"Overlay unavailable: {ex.Message}", $"Overlay no disponible: {ex.Message}"));
                ready.Set();
            }
            finally
            {
                _hwnd = IntPtr.Zero;
                HotkeyRegistered = LockHotkeyRegistered = PanelHotkeyRegistered = OpenHotkeyRegistered = false;
                _thread = null;
            }
        })
        {
            IsBackground = true,
            Name = "Overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Replaces what is shown. The window resizes itself to fit.</summary>
    public static void Update(OverlayView view)
    {
        _view = view;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_RELAYOUT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Remplace ce qu'affiche la barre (style « bar »).</summary>
    public static void Update(BarView bar)
    {
        _bar = bar;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_RELAYOUT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Fermée → Résumé → Coffres → fermée (Ctrl+Maj+E, ou l'entrée du menu).</summary>
    public static void CyclePanel()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_PANEL, (IntPtr)(-1), IntPtr.Zero);
        }
    }

    /// <summary>Ouvre directement un onglet (0 fermé, 1 Résumé, 2 Coffres) : sert aux captures de test.</summary>
    public static void ShowPanel(int panel)
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_PANEL, (IntPtr)Math.Clamp(panel, 0, 2), IntPtr.Zero);
        }
    }

    /// <summary>Saves the overlay as it is drawn to a PNG file (works off-screen). Used by --overlay-preview.</summary>
    public static bool Snapshot(string path)
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r))
        {
            return false;
        }
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        var ok = PrintWindow(_hwnd, mem, 1 /* PW_CLIENTONLY: WM_PRINT -> WM_PRINTCLIENT */);
        SelectObject(mem, old);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bgra = new byte[w * h * 4];
        GetDIBits(mem, bmp, 0, (uint)h, bgra, ref bi, 0);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        Png.Write(path, w, h, bgra);
        return ok;
    }

    public static void ToggleLock()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_LOCK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void Toggle()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void Run(int? x, int? y, ManualResetEventSlim ready)
    {
        try { SetProcessDPIAware(); } catch { /* old Windows */ }

        var screenDc = GetDC(IntPtr.Zero);
        _dpiScale = GetDeviceCaps(screenDc, LOGPIXELSY) / 96f;
        ReleaseDC(IntPtr.Zero, screenDc);

        int S(int v) => (int)Math.Round(v * _scale);

        CreateFonts();
        _brushBg = CreateSolidBrush(Rgb(18, 18, 22));
        _brushBorder = CreateSolidBrush(Rgb(58, 58, 66));
        _brushLine = CreateSolidBrush(Rgb(44, 44, 52));

        var hInstance = GetModuleHandleW(null);
        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_SIZEALL),
            hbrBackground = _brushBg,
            lpszClassName = "ChestCounterOverlay",
        };
        // Already registered when the overlay is turned off and on again in the same run (ERROR_CLASS_ALREADY_EXISTS).
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new InvalidOperationException("RegisterClassEx failed");
        }

        int w = S(MinWidth), h = S(90);
        var posX = x ?? S(24);
        var posY = y ?? S(24);

        _hwnd = CreateWindowExW(
            // Opaque (no WS_EX_LAYERED alpha): nothing for the compositor to blend every frame.
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName, L.T("Coffres", "Chests", "Cofres"), WS_POPUP,

            posX, posY, w, h, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowEx failed");
        }

        ApplyLock(_hwnd, _locked, notify: false);
        Relayout(_hwnd);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        // No timer at all: the thread sleeps in GetMessage and only wakes up when the counter changes,
        // when you drag/hide the window or press the shortcut.
        // Global shortcut, works even when the game has the focus.
        HotkeyRegistered = RegisterHotKey(_hwnd, 1, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O);
        LockHotkeyRegistered = RegisterHotKey(_hwnd, 2, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_L);
        PanelHotkeyRegistered = RegisterHotKey(_hwnd, 3, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_E);
        OpenHotkeyRegistered = RegisterHotKey(_hwnd, 4, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_C);
        ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>(Re)creates the fonts for the current display scaling and chosen size.</summary>
    private static void CreateFonts()
    {
        foreach (var old in new[] { _fontBig, _fontSmall, _fontHeader, _fontCaption, _fontLabel, _fontIcon, _fontHudBig, _fontHudSmall, _fontBarTotal, _fontBarText, _fontBarBold, _fontBarSmall, _fontBarBadge })
        {
            if (old != IntPtr.Zero) DeleteObject(old);
        }
        _scale = _dpiScale * _sizePercent / 100f;
        int S(int v) => (int)Math.Round(v * _scale);
        _fontBig = CreateFontW(-S(38), 0, 0, 0, 800, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontSmall = CreateFontW(-S(14), 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontHeader = CreateFontW(-S(12), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontCaption = CreateFontW(-S(11), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontLabel = CreateFontW(-S(13), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontIcon = CreateFontW(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe MDL2 Assets");
        // Game-like styles: condensed italic, grayscale anti-aliasing (ClearType would leave colored edges once the
        // background is made transparent).
        _fontHudBig = CreateFontW(-S(40), 0, 0, 0, 700, 1, 0, 0, 1, 0, 0, 4, 0, "Bahnschrift Condensed");
        _fontHudSmall = CreateFontW(-S(18), 0, 0, 0, 600, 1, 0, 0, 1, 0, 0, 4, 0, "Bahnschrift SemiCondensed");
        _fontBarTotal = CreateFontW(-S(21), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontBarText = CreateFontW(-S(13), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontBarBold = CreateFontW(-S(13), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontBarSmall = CreateFontW(-S(11), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _fontBarBadge = CreateFontW(-S(10), 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // Lock button = normal client area (clickable); everywhere else = caption (drag to move).
                // When locked the window is WS_EX_TRANSPARENT, so it never even receives this message.
                GetWindowRect(hwnd, out var wr);
                var px = (short)((long)lParam & 0xFFFF) - wr.Left;
                var py = (short)(((long)lParam >> 16) & 0xFFFF) - wr.Top;
                var onButton = In(_lockRect, px, py) || (IsBar && (In(_chevRect, px, py) || (_panel > 0 && (In(_tab1Rect, px, py) || In(_tab2Rect, px, py) || In(_openRect, px, py)))));
                return (IntPtr)(onButton ? HTCLIENT : HTCAPTION);
            }

            case WM_SETCURSOR:
                if (((long)lParam & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                    return (IntPtr)1;
                }
                break;

            case WM_LBUTTONUP:
            {
                var cx = (short)((long)lParam & 0xFFFF);
                var cy = (short)(((long)lParam >> 16) & 0xFFFF);
                if (IsBar && _panel > 0 && In(_openRect, cx, cy)) OpenCounter?.Invoke();
                else if (IsBar && In(_chevRect, cx, cy)) SetPanel(hwnd, -1);
                else if (IsBar && _panel > 0 && In(_tab1Rect, cx, cy)) SetPanel(hwnd, 1);
                else if (IsBar && _panel > 0 && In(_tab2Rect, cx, cy)) SetPanel(hwnd, 2);
                else ApplyLock(hwnd, true, notify: true);
                return IntPtr.Zero;
            }

            case WM_APP_PANEL:
                SetPanel(hwnd, (int)(long)wParam);
                return IntPtr.Zero;

            case WM_APP_LOCK:
                ApplyLock(hwnd, !_locked, notify: true);
                return IntPtr.Zero;

            case WM_NCRBUTTONUP:
                SetVisible(hwnd, false);
                return IntPtr.Zero;

            case WM_HOTKEY:
                if ((long)wParam == 2)
                {
                    ApplyLock(hwnd, !_locked, notify: true);
                }
                else if ((long)wParam == 4)
                {
                    OpenCounter?.Invoke();
                }
                else if ((long)wParam == 3)
                {
                    if (!IsWindowVisible(hwnd)) SetVisible(hwnd, true);
                    SetPanel(hwnd, -1);
                }
                else
                {
                    SetVisible(hwnd, !IsWindowVisible(hwnd));
                }
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                if (GetWindowRect(hwnd, out var r))
                {
                    _onMoved?.Invoke(r.Left, r.Top);
                }
                return IntPtr.Zero;

            case WM_APP_STOP:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WM_APP_TOGGLE:
                SetVisible(hwnd, !IsWindowVisible(hwnd));
                return IntPtr.Zero;

            case WM_APP_RELAYOUT:
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_APP_APPEARANCE:
                CreateFonts();
                _stableW = 0; // the width may shrink when the overlay gets smaller
                ApplyLock(hwnd, _locked, notify: false); // also applies the opacity
                Relayout(hwnd);
                return IntPtr.Zero;

            case WM_PAINT:
                Paint(hwnd);
                return IntPtr.Zero;

            case WM_PRINTCLIENT: // snapshot (--overlay-preview): draw into the given DC
                PaintTo(hwnd, wParam);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterHotKey(hwnd, 1);
                UnregisterHotKey(hwnd, 2);
                UnregisterHotKey(hwnd, 3);
                UnregisterHotKey(hwnd, 4);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Locked: click-through (WS_EX_LAYERED + WS_EX_TRANSPARENT). Unlocked: normal window. Layered also when the
    /// player made it see-through; a fully opaque unlocked overlay stays a plain window (nothing to blend).
    /// </summary>
    private static void ApplyLock(IntPtr hwnd, bool locked, bool notify)
    {
        _locked = locked;
        var alpha = Alpha;
        var layered = locked || alpha < 255 || Keyed;
        var ex = (long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        ex = layered ? ex | WS_EX_LAYERED : ex & ~(long)WS_EX_LAYERED;
        ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (IntPtr)ex);
        if (layered)
        {
            // Required for a layered window to be drawn; the key color makes the background of the game-like styles invisible.
            SetLayeredWindowAttributes(hwnd, KeyColor, alpha, Keyed ? LWA_ALPHA | LWA_COLORKEY : LWA_ALPHA);
        }
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        InvalidateRect(hwnd, IntPtr.Zero, true);

        if (notify)
        {
            _onLockChanged?.Invoke(locked);
            Console.WriteLine(locked
                ? L.T($"Overlay verrouillé : la souris passe au travers. {(LockHotkeyRegistered ? "Ctrl+Maj+L" : "« Déverrouiller l'overlay » dans le compteur")} pour le déverrouiller.",
                      $"Overlay locked: the mouse goes through it. {(LockHotkeyRegistered ? "Ctrl+Shift+L" : "\"Unlock the overlay\" in the counter")} to unlock it.",
                      $"Overlay bloqueado: el ratón lo atraviesa. {(LockHotkeyRegistered ? "Ctrl+Mayús+L" : "«Desbloquear el overlay» en el contador")} para desbloquearlo.")
                : L.T("Overlay déverrouillé : clic gauche maintenu pour le déplacer, cadenas pour le reverrouiller.",
                      "Overlay unlocked: hold the left mouse button to move it, click the padlock to lock it again.",
                      "Overlay desbloqueado: arrástralo con el clic izquierdo; el candado lo vuelve a bloquear."));
        }
    }

    private static void SetVisible(IntPtr hwnd, bool visible)
    {
        if (visible)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else
        {
            ShowWindow(hwnd, SW_HIDE);
            Console.WriteLine(HotkeyRegistered
                ? L.T("Overlay masqué : Ctrl+Maj+O (n'importe où) pour le réafficher.", "Overlay hidden: Ctrl+Shift+O (anywhere) to show it again.", "Overlay oculto: Ctrl+Mayús+O (desde cualquier sitio) para volver a mostrarlo.")
                : L.T("Overlay masqué : « Afficher l'overlay » dans le compteur pour le réafficher.", "Overlay hidden: \"Show the overlay\" in the counter to show it again.", "Overlay oculto: «Mostrar el overlay» en el contador para volver a mostrarlo."));
        }
    }

    private static int TextWidth(IntPtr hdc, IntPtr font, string text, out int height)
    {
        SelectObject(hdc, font);
        var r = new RECT();
        DrawTextW(hdc, text.Length == 0 ? " " : text, -1, ref r, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
        height = r.Bottom - r.Top;
        return r.Right - r.Left;
    }

    /// <summary>Measures the text and resizes the window (and its rounded region) to fit it.</summary>
    private static void Relayout(IntPtr hwnd)
    {
        if (IsBar)
        {
            RelayoutBar(hwnd);
            return;
        }
        if (Keyed)
        {
            RelayoutStyled(hwnd);
            return;
        }
        int S(int v) => (int)Math.Round(v * _scale);
        var v = _view;
        var hdc = GetDC(hwnd);
        try
        {
            // Header: "COFFRES" ... "● EN DIRECT" [lock]
            var headerW = TextWidth(hdc, _fontHeader, Header, out _headerH) + S(16) + (v.Badge != null ? TextWidth(hdc, _fontHeader, v.Badge, out _) : 0) + S(30);

            // Two counters side by side.
            _col1W = Math.Max(TextWidth(hdc, _fontBig, v.Total, out _bigH), TextWidth(hdc, _fontCaption, CaptionTotal, out _captionH));
            _col2W = Math.Max(TextWidth(hdc, _fontBig, v.Current ?? "—", out _), Math.Max(TextWidth(hdc, _fontCaption, v.CurrentCaption, out _), TextWidth(hdc, _fontCaption, OverlayModel.LastMatchCaption, out _)));
            var countersW = _col1W + S(ColGap) + _col2W;

            // Detail rows: grey label column + value.
            _labelW = 0;
            var valueW = 0;
            _rowH = 0;
            foreach (var row in v.Rows)
            {
                _labelW = Math.Max(_labelW, TextWidth(hdc, _fontLabel, row.Label, out var lh));
                valueW = Math.Max(valueW, TextWidth(hdc, _fontSmall, row.Value, out var vh)
                    + (row.Detail != null ? S(6) + TextWidth(hdc, _fontCaption, row.Detail, out _) : 0));
                _rowH = Math.Max(_rowH, Math.Max(lh, vh));
            }
            var rowsW = v.Rows.Count > 0 ? _labelW + S(12) + valueW : 0;

            // Never shrinks: the overlay keeps the same size between a match in progress and the end of the match.
            var w = Math.Max(_stableW, Math.Max(S(MinWidth), Math.Max(headerW, Math.Max(countersW, rowsW)) + 2 * S(Pad)));
            _stableW = w;
            var h = S(12) + _headerH + S(6) + _bigH + _captionH + S(12);
            if (v.Rows.Count > 0)
            {
                h += 1 + S(10) + v.Rows.Count * (_rowH + S(4)) + S(4);
            }
            _lockRect = new RECT { Left = w - S(Pad) - S(18), Top = S(6), Right = w - S(6), Bottom = S(12) + _headerH + S(6) };
            // Keep it fully on its screen: on the right half of the screen it grows to the left, and it is
            // pushed back inside when it would overflow (a wider overlay used to be cut by the screen edge).
            GetWindowRect(hwnd, out var cur);
            int nx = cur.Left, ny = cur.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var work = mi.rcWork;
                if (cur.Right - cur.Left > 0 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
                {
                    nx = cur.Right - w; // anchored on the right edge
                }
                nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
                ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, S(18), S(18)), true); // window owns the region
            if (nx != cur.Left || ny != cur.Top)
            {
                _onMoved?.Invoke(nx, ny);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
        UpdateWindow(hwnd); // paint now (one repaint per update, no timer)
    }

    private static void Text(IntPtr hdc, IntPtr font, uint color, string text, int left, int top, int right, int bottom, uint align = DT_LEFT)
    {
        SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var r = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        DrawTextW(hdc, text, -1, ref r, DT_SINGLELINE | DT_NOPREFIX | align);
    }

    private static void Paint(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        try
        {
            PaintTo(hwnd, hdc);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private static void PaintTo(IntPtr hwnd, IntPtr hdc)
    {
        if (IsBar)
        {
            PaintBar(hwnd, hdc);
            return;
        }
        if (Keyed)
        {
            PaintStyled(hwnd, hdc);
            return;
        }
        var v = _view;
        {
            GetClientRect(hwnd, out var rc);
            FillRect(hdc, ref rc, _brushBg);
            SetBkMode(hdc, TRANSPARENT);

            int S(int n) => (int)Math.Round(n * _scale);
            var x = S(Pad);
            var right = rc.Right - S(Pad);
            var y = S(12);

            // Thin border following the rounded shape.
            var border = CreateRoundRectRgn(0, 0, rc.Right + 1, rc.Bottom + 1, S(18), S(18));
            FrameRgn(hdc, border, _brushBorder, 1, 1);
            DeleteObject(border);

            // Header
            Text(hdc, _fontHeader, Rgb(150, 150, 162), Header, x, y, right, y + _headerH);
            if (v.Badge != null)
            {
                Text(hdc, _fontHeader, Rgb(96, 214, 128), v.Badge, x, y, _lockRect.Left - S(6), y + _headerH, DT_RIGHT);
            }
            // Lock button (Segoe MDL2 Assets: E785 = open padlock, E72E = closed padlock).
            SelectObject(hdc, _fontIcon);
            SetTextColor(hdc, _locked ? Rgb(96, 96, 106) : Rgb(200, 200, 208));
            var lr = _lockRect;
            DrawTextW(hdc, _locked ? "" : "", -1, ref lr, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
            y += _headerH + S(6);

            // Counters: total | match in progress
            var x2 = x + _col1W + S(ColGap);
            Text(hdc, _fontBig, Rgb(242, 184, 75), v.Total, x - S(1), y, x + _col1W + S(4), y + _bigH);
            Text(hdc, _fontBig, v.Current == null ? Rgb(80, 80, 92) : v.CurrentLive ? Rgb(110, 224, 150) : Rgb(226, 226, 232), v.Current ?? "—", x2 - S(1), y, right + S(4), y + _bigH);
            var sep = new RECT { Left = x + _col1W + S(ColGap) / 2, Top = y + S(8), Right = x + _col1W + S(ColGap) / 2 + 1, Bottom = y + _bigH + _captionH };
            FillRect(hdc, ref sep, _brushLine);
            y += _bigH;
            Text(hdc, _fontCaption, Rgb(130, 130, 142), CaptionTotal, x, y, x + _col1W + S(4), y + _captionH);
            Text(hdc, _fontCaption, Rgb(130, 130, 142), v.CurrentCaption, x2, y, right + S(4), y + _captionH);
            y += _captionH + S(12);

            if (v.Rows.Count > 0)
            {
                var line = new RECT { Left = x, Top = y, Right = right, Bottom = y + 1 };
                FillRect(hdc, ref line, _brushLine);
                y += 1 + S(10);
                foreach (var row in v.Rows)
                {
                    Text(hdc, _fontLabel, Rgb(130, 130, 142), row.Label, x, y, x + _labelW, y + _rowH);
                    var vx = x + _labelW + S(12);
                    Text(hdc, _fontSmall, row.Treasure ? Rgb(196, 160, 255) : Rgb(226, 226, 232), row.Value, vx, y, right + S(4), y + _rowH);
                    if (row.Detail != null)
                    {
                        var dx = vx + TextWidth(hdc, _fontSmall, row.Value, out _) + S(6);
                        Text(hdc, _fontCaption, Rgb(130, 130, 142), row.Detail, dx, y, right + S(4), y + _rowH - S(2), DT_BOTTOM);
                    }
                    y += _rowH + S(4);
                }
            }
        }
    }


    // ---- Style « bar » : barre des coffres déroulante ------------------------------------------------------------
    //
    //   [coffre] 1 432 coffres [● EN DIRECT]        ▼
    //   ─────────────────────────────────────────────
    //   [ Résumé ] [ Coffres · chances ]
    //   Partie en cours                            7
    //   Session                                   31
    //   3 parties · moy. 10
    //   ...

    private const int BarWidth = 270;

    private static bool In(RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    /// <summary>-1 = onglet suivant (fermée → Résumé → Coffres → fermée), sinon l'onglet demandé.</summary>
    private static void SetPanel(IntPtr hwnd, int panel)
    {
        _panel = panel < 0 ? (_panel + 1) % 3 : panel;
        _stableW = 0;
        Relayout(hwnd);
    }

    private static string BarCaption => L.T("coffres", "chests", "cofres");

    private static int BarContentHeight(BarView b)
    {
        if (_panel == 1)
        {
            return b.Summary.Sum(l => _barTextH + (l.Detail != null ? _barSmallH : 0) + Sc(7));
        }
        var lines = b.Chances.Count == 0 ? _barTextH + Sc(7) : b.Chances.Sum(_ => _barTextH + Sc(3) + Sc(4) + Sc(3) + _barSmallH + Sc(7));
        return lines + _barSmallH + Sc(4); // + la ligne d'aide
    }

    private static void RelayoutBar(IntPtr hwnd)
    {
        var b = _bar ?? new BarView("0", BarState.Confirmed, "", Array.Empty<BarLine>(), Array.Empty<ChanceLine>(), null);
        var hdc = GetDC(hwnd);
        try
        {
            TextWidth(hdc, _fontBarText, "Ag", out _barTextH);
            TextWidth(hdc, _fontBarSmall, "Ag", out _barSmallH);
            var totalW = TextWidth(hdc, _fontBarTotal, b.Total, out _barTotalH);
            var badgeW = b.Badge.Length > 0 ? TextWidth(hdc, _fontBarBadge, b.Badge, out _) + Sc(12) : 0;
            var needed = Sc(11) + Sc(18) + Sc(8) + totalW + Sc(6) + TextWidth(hdc, _fontBarText, BarCaption, out _) + Sc(6) + badgeW + Sc(8) + Sc(14) + Sc(22) + Sc(6);
            var w = Math.Max(_stableW, Math.Max(Sc(BarWidth), needed));
            _stableW = w;
            _barH = Math.Max(Sc(40), _barTotalH + Sc(12));
            var h = _barH;
            if (_panel > 0)
            {
                h += 1 + Sc(8) + Sc(24) + Sc(8) + BarContentHeight(b) + Sc(4) + Sc(26) + Sc(10);
            }
            _lockRect = _locked ? new RECT() : new RECT { Left = w - Sc(28), Top = 0, Right = w - Sc(6), Bottom = _barH };
            var chevRight = _locked ? w - Sc(6) : _lockRect.Left;
            _chevRect = new RECT { Left = chevRight - Sc(24), Top = 0, Right = chevRight, Bottom = _barH };
            var tabTop = _barH + 1 + Sc(8);
            var mid = w / 2;
            _tab1Rect = new RECT { Left = Sc(11), Top = tabTop, Right = mid - Sc(2), Bottom = tabTop + Sc(24) };
            _tab2Rect = new RECT { Left = mid + Sc(2), Top = tabTop, Right = w - Sc(11), Bottom = tabTop + Sc(24) };
            _openRect = _panel > 0 ? new RECT { Left = Sc(11), Top = h - Sc(10) - Sc(26), Right = w - Sc(11), Bottom = h - Sc(10) } : new RECT();

            GetWindowRect(hwnd, out var cur);
            int nx = cur.Left, ny = cur.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var work = mi.rcWork;
                if (cur.Right - cur.Left > 0 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
                {
                    nx = cur.Right - w; // ancrée à droite sur la moitié droite de l'écran
                }
                nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
                ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, Sc(14), Sc(14)), true);
            if (nx != cur.Left || ny != cur.Top)
            {
                _onMoved?.Invoke(nx, ny);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
        UpdateWindow(hwnd);
    }

    private static void Fill(IntPtr hdc, int l, int t, int r, int btm, uint color, int radius = 0)
    {
        var brush = CreateSolidBrush(color);
        if (radius <= 0)
        {
            var rc = new RECT { Left = l, Top = t, Right = r, Bottom = btm };
            FillRect(hdc, ref rc, brush);
        }
        else
        {
            var pen = CreatePen(0, 1, color);
            var oldPen = SelectObject(hdc, pen);
            var oldBrush = SelectObject(hdc, brush);
            RoundRect(hdc, l, t, r, btm, radius, radius);
            SelectObject(hdc, oldBrush);
            SelectObject(hdc, oldPen);
            DeleteObject(pen);
        }
        DeleteObject(brush);
    }

    private static void PaintBar(IntPtr hwnd, IntPtr hdc)
    {
        var b = _bar ?? new BarView("0", BarState.Confirmed, "", Array.Empty<BarLine>(), Array.Empty<ChanceLine>(), null);
        GetClientRect(hwnd, out var rc);
        // Couleurs de la maquette validée.
        uint bg = Rgb(11, 15, 24), gold = Rgb(255, 214, 102), label = Rgb(184, 191, 204), value = Rgb(238, 238, 255),
            dim = Rgb(125, 133, 151), muted = Rgb(154, 163, 181), line = Rgb(30, 34, 42);
        Fill(hdc, 0, 0, rc.Right, rc.Bottom, bg);
        SetBkMode(hdc, TRANSPARENT);
        var border = CreateRoundRectRgn(0, 0, rc.Right + 1, rc.Bottom + 1, Sc(14), Sc(14));
        var borderBrush = CreateSolidBrush(Rgb(96, 85, 51));
        FrameRgn(hdc, border, borderBrush, 1, 1);
        DeleteObject(borderBrush);
        DeleteObject(border);

        // Barre : coffre, total, « coffres », badge, chevron (et cadenas quand déverrouillée).
        var x = Sc(11);
        var ih = Sc(15);
        var iw = Sc(18);
        Chest(hdc, x, (_barH - ih) / 2, iw, ih, gold, Rgb(224, 165, 38), Rgb(60, 40, 10), Rgb(60, 40, 10));
        x += iw + Sc(8);
        var totalW = TextWidth(hdc, _fontBarTotal, b.Total, out _);
        Text(hdc, _fontBarTotal, gold, b.Total, x, 0, x + totalW + Sc(2), _barH, DT_VCENTER);
        x += totalW + Sc(6);
        var capW = TextWidth(hdc, _fontBarText, BarCaption, out _);
        Text(hdc, _fontBarText, muted, BarCaption, x, Sc(1), x + capW + Sc(2), _barH, DT_VCENTER);
        x += capW + Sc(6);
        if (b.Badge.Length > 0)
        {
            var ok = b.State is BarState.Confirmed or BarState.EstimateConfirmed;
            var bw = TextWidth(hdc, _fontBarBadge, b.Badge, out var bh) + Sc(12);
            var by = (_barH - bh - Sc(4)) / 2;
            Fill(hdc, x, by, x + bw, by + bh + Sc(4), ok ? Rgb(29, 52, 44) : Rgb(31, 43, 66), Sc(10));
            Text(hdc, _fontBarBadge, ok ? Rgb(141, 224, 165) : Rgb(156, 192, 255), b.Badge, x, by, x + bw, by + bh + Sc(4), DT_CENTER | DT_VCENTER);
        }
        Text(hdc, _fontBarSmall, muted, _panel == 0 ? "▼" : "▲", _chevRect.Left, 0, _chevRect.Right, _barH, DT_CENTER | DT_VCENTER);
        if (!_locked)
        {
            SelectObject(hdc, _fontIcon);
            SetTextColor(hdc, Rgb(200, 200, 208));
            var lr = _lockRect;
            DrawTextW(hdc, "", -1, ref lr, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
        }
        if (_panel == 0)
        {
            return;
        }

        // Onglets
        var y = _barH;
        Fill(hdc, 0, y, rc.Right, y + 1, line);
        foreach (var (r, text, on) in new[]
        {
            (_tab1Rect, L.T("Résumé", "Summary", "Resumen"), _panel == 1),
            (_tab2Rect, L.T("Coffres · chances", "Chests · chances", "Cofres · probabilidades"), _panel == 2),
        })
        {
            Fill(hdc, r.Left, r.Top, r.Right, r.Bottom, on ? Rgb(50, 47, 36) : Rgb(20, 24, 33), Sc(8));
            Text(hdc, on ? _fontBarBold : _fontBarText, on ? gold : muted, text, r.Left, r.Top, r.Right, r.Bottom, DT_CENTER | DT_VCENTER);
        }
        y = _tab1Rect.Bottom + Sc(8);
        int left = Sc(11), right = rc.Right - Sc(11);

        if (_panel == 1)
        {
            foreach (var l in b.Summary)
            {
                Text(hdc, _fontBarText, label, l.Label, left, y, right, y + _barTextH);
                Text(hdc, _fontBarBold, value, l.Value, left, y, right, y + _barTextH, DT_RIGHT);
                y += _barTextH;
                if (l.Detail != null)
                {
                    Text(hdc, _fontBarSmall, dim, l.Detail, left, y, right, y + _barSmallH);
                    y += _barSmallH;
                }
                y += Sc(7);
            }
            PaintOpenButton(hdc);
            return;
        }

        if (b.Chances.Count == 0)
        {
            Text(hdc, _fontBarText, label, b.ChancesEmpty ?? "", left, y, right, y + _barTextH);
            y += _barTextH + Sc(7);
        }
        foreach (var c in b.Chances)
        {
            var pw = TextWidth(hdc, _fontBarBold, c.Percent, out _);
            Text(hdc, _fontBarText, Rgb(223, 227, 234), c.Label, left, y, right - pw - Sc(8), y + _barTextH);
            Text(hdc, _fontBarBold, gold, c.Percent, left, y, right, y + _barTextH, DT_RIGHT);
            y += _barTextH + Sc(3);
            Fill(hdc, left, y, right, y + Sc(4), Rgb(28, 32, 41));
            var fillW = (int)Math.Round((right - left) * Math.Clamp(c.Fill, 0, 1));
            Fill(hdc, left, y, left + Math.Max(Sc(2), fillW), y + Sc(4), gold);
            y += Sc(4) + Sc(3);
            Text(hdc, _fontBarSmall, dim, c.Odds, left, y, right, y + _barSmallH, DT_END_ELLIPSIS);
            y += _barSmallH + Sc(7);
        }
        Text(hdc, _fontBarSmall, dim, L.T("Ce que tu as eu : « Trésors obtenus » dans le compteur.", "Got one? \"Treasures obtained\" in the counter.", "¿Ya lo tienes? «Tesoros conseguidos» en el contador."),
            left, y, right, y + _barSmallH, DT_END_ELLIPSIS);
        PaintOpenButton(hdc);
    }

    /// <summary>Bouton « Ouvrir le compteur » en bas de la barre déroulée (cliquable quand elle est déverrouillée).</summary>
    private static void PaintOpenButton(IntPtr hdc)
    {
        var r = _openRect;
        Fill(hdc, r.Left, r.Top, r.Right, r.Bottom, Rgb(20, 24, 33), Sc(8));
        var keys = L.T("Ctrl+Maj+C", "Ctrl+Shift+C", "Ctrl+Mayús+C");
        var text = $"{L.T("Ouvrir le compteur", "Open the counter", "Abrir el contador")}  ·  {keys}";
        Text(hdc, _fontBarText, Rgb(223, 227, 234), text, r.Left, r.Top, r.Right, r.Bottom, DT_CENTER | DT_VCENTER | DT_END_ELLIPSIS);
    }

    // ---- "hud" style: figures only, outlined like the game's own HUD, right-aligned under the minimap -------------
    //
    //   [chest] 3 934        total (gold while a match is counted live)
    //   Dernière • 14        last match / "● En partie • …" / "● En direct • 12"
    //    Session • 83
    //      [star] 36         treasures

    private sealed record HudRow(string Label, string Value, bool Dot);

    private static HudRow StateRow(OverlayView v)
    {
        if (v.CurrentLive && v.Current is { } cur && cur != "…")
        {
            return new HudRow(L.T("En direct", "Live", "En directo"), cur, true);
        }
        if (v.CurrentLive)
        {
            return new HudRow(L.T("En partie", "In a match", "En partida"), "", true); // figures readable only at the end
        }
        return new HudRow(v.LastLabel ?? L.T("Dernière", "Last", "Última"), v.Last ?? "—", false);
    }

    private static int Sc(double v) => (int)Math.Round(v * _scale);

    private const string Sep = "  •  ";

    private static void RelayoutStyled(IntPtr hwnd)
    {
        var v = _view;
        var hdc = GetDC(hwnd);
        int w, h;
        try
        {
            var pad = Sc(8);
            var lockW = Sc(22);
            var bigW = TextWidth(hdc, _fontHudBig, v.Total, out _hudBigH);
            var state = StateRow(v);
            var sessionLabel = L.T("Session", "Session", "Sesión");
            var rowsW = new[]
            {
                Sc(14) + TextWidth(hdc, _fontHudSmall, state.Label + Sep + state.Value, out _hudSmallH),
                TextWidth(hdc, _fontHudSmall, sessionLabel + Sep + v.Session, out _),
                v.ThirdLabel != null ? TextWidth(hdc, _fontHudSmall, v.ThirdLabel + Sep + v.Treasures, out _) : Sc(22) + TextWidth(hdc, _fontHudSmall, v.Treasures, out _),
            }.Max();
            w = lockW + pad + Math.Max(Sc(26) + Sc(8) + bigW, rowsW) + pad + Sc(14);
            h = pad + _hudBigH + 3 * (_hudSmallH + Sc(2)) + pad;
            w = Math.Max(_stableW, w);
            _stableW = w;
            _lockRect = new RECT { Left = Sc(2), Top = 0, Right = lockW, Bottom = h };

            GetWindowRect(hwnd, out var cur);
            int nx = cur.Left, ny = cur.Top;
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!_offscreen && GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var work = mi.rcWork;
                if (cur.Right - cur.Left > 0 && (cur.Left + cur.Right) / 2 > (work.Left + work.Right) / 2)
                {
                    nx = cur.Right - w; // anchored on the right edge (under the minimap)
                }
                nx = Math.Max(work.Left, Math.Min(nx, work.Right - w));
                ny = Math.Max(work.Top, Math.Min(ny, work.Bottom - h));
            }
            SetWindowPos(hwnd, HWND_TOPMOST, nx, ny, w, h, SWP_NOACTIVATE);
            SetWindowRgn(hwnd, IntPtr.Zero, true); // plain rectangle: the corners are made invisible by the key color
            if (nx != cur.Left || ny != cur.Top)
            {
                _onMoved?.Invoke(nx, ny);
            }
        }
        finally
        {
            ReleaseDC(hwnd, hdc);
        }
        InvalidateRect(hwnd, IntPtr.Zero, true);
        UpdateWindow(hwnd);
    }

    /// <summary>Text outlined in near black, like the game's own HUD figures (readable on any background).</summary>
    private static void Outlined(IntPtr hdc, IntPtr font, uint color, string text, int left, int top, int right, int bottom, uint align, bool thin = false)
    {
        SelectObject(hdc, font);
        var o = thin ? 1 : Math.Max(1, Sc(1.6));
        SetTextColor(hdc, Rgb(11, 15, 24));
        for (var dx = -o; dx <= o; dx++)
        {
            for (var dy = -o; dy <= o + 1; dy++)
            {
                if (Math.Abs(dx) != o && Math.Abs(dy) < o) continue; // only the ring (and one pixel of drop shadow)
                var r = new RECT { Left = left + dx, Top = top + dy, Right = right + dx, Bottom = bottom + dy };
                DrawTextW(hdc, text, -1, ref r, DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP | align);
            }
        }
        SetTextColor(hdc, color);
        var rc = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        DrawTextW(hdc, text, -1, ref rc, DT_SINGLELINE | DT_NOPREFIX | DT_NOCLIP | align); // italic glyphs lean past the box
    }

    /// <summary>A treasure chest made of three shapes: body, rounded lid, latch.</summary>
    private static void Chest(IntPtr hdc, int x, int y, int w, int h, uint body, uint lid, uint latch, uint outline)
    {
        var pen = CreatePen(0, Math.Max(1, Sc(1.5)), outline);
        var oldPen = SelectObject(hdc, pen);
        var lidH = (int)(h * 0.42);
        var bLid = CreateSolidBrush(lid);
        var old = SelectObject(hdc, bLid);
        Chord(hdc, x, y, x + w, y + 2 * lidH, x + w, y + lidH, x, y + lidH);
        var bBody = CreateSolidBrush(body);
        SelectObject(hdc, bBody);
        RoundRect(hdc, x, y + lidH, x + w, y + h, Sc(4), Sc(4));
        var bLatch = CreateSolidBrush(latch);
        SelectObject(hdc, bLatch);
        var lw = Math.Max(3, w / 5);
        Rectangle(hdc, x + (w - lw) / 2, y + lidH - Sc(2), x + (w + lw) / 2, y + lidH + Math.Max(4, h / 4));
        SelectObject(hdc, old);
        SelectObject(hdc, oldPen);
        DeleteObject(bLid);
        DeleteObject(bBody);
        DeleteObject(bLatch);
        DeleteObject(pen);
    }

    /// <summary>A five-pointed star (treasures), outlined like the text.</summary>
    private static void Star(IntPtr hdc, int cx, int cy, int r, uint fill, uint outline)
    {
        var pts = new POINT[10];
        for (var i = 0; i < 10; i++)
        {
            var a = -Math.PI / 2 + i * Math.PI / 5;
            var rr = i % 2 == 0 ? r : r * 0.45;
            pts[i] = new POINT { X = cx + (int)Math.Round(rr * Math.Cos(a)), Y = cy + (int)Math.Round(rr * Math.Sin(a)) };
        }
        var pen = CreatePen(0, Math.Max(1, Sc(1.5)), outline);
        var brush = CreateSolidBrush(fill);
        var oldPen = SelectObject(hdc, pen);
        var old = SelectObject(hdc, brush);
        Polygon(hdc, pts, pts.Length);
        SelectObject(hdc, old);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    private static void Disc(IntPtr hdc, int x, int y, int d, uint fill, uint outline, int penW)
    {
        var pen = CreatePen(0, penW, outline);
        var brush = CreateSolidBrush(fill);
        var oldPen = SelectObject(hdc, pen);
        var old = SelectObject(hdc, brush);
        Ellipse(hdc, x, y, x + d, y + d);
        SelectObject(hdc, old);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    /// <summary>"Label  •  value" right-aligned: pale label, white value.</summary>
    private static int LabelValue(IntPtr hdc, string label, string value, int left, int right, int top, int height, uint valueColor)
    {
        if (value.Length == 0)
        {
            Outlined(hdc, _fontHudSmall, Rgb(255, 233, 166), label, left, top, right, top + height, DT_RIGHT, thin: true);
            return right - TextWidth(hdc, _fontHudSmall, label, out _);
        }
        var valueW = TextWidth(hdc, _fontHudSmall, value, out _);
        Outlined(hdc, _fontHudSmall, valueColor, value, left, top, right, top + height, DT_RIGHT, thin: true);
        var labelRight = right - valueW;
        Outlined(hdc, _fontHudSmall, Rgb(255, 233, 166), label + Sep, left, top, labelRight, top + height, DT_RIGHT, thin: true);
        return labelRight - TextWidth(hdc, _fontHudSmall, label + Sep, out _);
    }

    private static void PaintStyled(IntPtr hwnd, IntPtr hdc)
    {
        GetClientRect(hwnd, out var rc);
        var key = CreateSolidBrush(KeyColor);
        FillRect(hdc, ref rc, key);
        DeleteObject(key);
        SetBkMode(hdc, TRANSPARENT);
        var v = _view;
        var pad = Sc(8);
        var right = rc.Right - pad - Sc(12); // italic glyphs and their outline lean past the measured width
        var white = Rgb(255, 255, 255);
        var gold = Rgb(255, 210, 63);
        var ink = Rgb(11, 15, 24);

        // Unlocked: a dark backdrop to grab it, and the padlock (locked, only the figures remain, mouse goes through).
        if (!_locked)
        {
            var pen = CreatePen(0, 1, Rgb(58, 66, 86));
            var brush = CreateSolidBrush(Rgb(28, 34, 48));
            var oldPen = SelectObject(hdc, pen);
            var old = SelectObject(hdc, brush);
            RoundRect(hdc, 0, 0, rc.Right, rc.Bottom, Sc(12), Sc(12));
            SelectObject(hdc, old);
            SelectObject(hdc, oldPen);
            DeleteObject(brush);
            DeleteObject(pen);
            SelectObject(hdc, _fontIcon);
            SetTextColor(hdc, Rgb(200, 200, 208));
            var lr = _lockRect;
            DrawTextW(hdc, "", -1, ref lr, DT_SINGLELINE | DT_CENTER | DT_VCENTER | DT_NOPREFIX);
        }

        var left = _lockRect.Right;
        var y = pad;
        var state = StateRow(v);
        var live = v.CurrentLive && state.Value.Length > 0;

        // Total, with the chest.
        var bigW = TextWidth(hdc, _fontHudBig, v.Total, out _);
        Outlined(hdc, _fontHudBig, live ? gold : white, v.Total, left, y, right, y + _hudBigH, DT_RIGHT);
        var iw = Sc(26);
        var ih = Sc(22);
        var ix = right - bigW - Sc(8) - iw;
        Chest(hdc, ix, y + (_hudBigH - ih) / 2, iw, ih, gold, Rgb(224, 165, 38), ink, ink);
        y += _hudBigH + Sc(2);

        // Last match (or the match in progress), session, treasures.
        var rowH = _hudSmallH;
        var stateLeft = LabelValue(hdc, state.Label, state.Value, left, right, y, rowH, live ? gold : white);
        if (state.Dot)
        {
            Disc(hdc, stateLeft - Sc(12), y + rowH / 2 - Sc(4), Sc(9), Rgb(255, 59, 48), ink, 1);
        }
        y += rowH + Sc(2);
        LabelValue(hdc, L.T("Session", "Session", "Sesión"), v.Session, left, right, y, rowH, white);
        y += rowH + Sc(2);
        if (v.ThirdLabel != null)
        {
            LabelValue(hdc, v.ThirdLabel, v.Treasures, left, right, y, rowH, white);
            return;
        }
        var tw = TextWidth(hdc, _fontHudSmall, v.Treasures, out _);
        Outlined(hdc, _fontHudSmall, Rgb(214, 190, 255), v.Treasures, left, y, right, y + rowH, DT_RIGHT, thin: true);
        Star(hdc, right - tw - Sc(12), y + rowH / 2, Sc(8), Rgb(196, 155, 255), ink);
    }

    private static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    // ---- Win32 ------------------------------------------------------------------------------
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000, WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_EXSTYLE = -20, HTCLIENT = 1;
    private const uint WM_SETCURSOR = 0x0020, WM_LBUTTONUP = 0x0202, WM_APP_LOCK = 0x8003, VK_L = 0x4C, SWP_FRAMECHANGED = 0x20;
    private static readonly IntPtr IDC_HAND = new(32649);
    private const uint LWA_ALPHA = 0x2, LWA_COLORKEY = 0x1;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_NCHITTEST = 0x0084, WM_NCRBUTTONUP = 0x00A5,
        WM_TIMER = 0x0113, WM_EXITSIZEMOVE = 0x0232, WM_APP_TOGGLE = 0x8001, WM_PRINTCLIENT = 0x0318, WM_APP_RELAYOUT = 0x8002, WM_HOTKEY = 0x0312, WM_APP_APPEARANCE = 0x8004, WM_APP_STOP = 0x8005, WM_APP_PANEL = 0x8006;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_O = 0x4F, VK_E = 0x45, VK_C = 0x43;
    private const int HTCAPTION = 2;
    private const int TRANSPARENT = 1, LOGPIXELSY = 90;
    private const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_BOTTOM = 0x8, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_NOCLIP = 0x100, DT_END_ELLIPSIS = 0x8000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr IDC_SIZEALL = new(32646);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool FrameRgn(IntPtr hdc, IntPtr rgn, IntPtr brush, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool Polygon(IntPtr hdc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool Chord(IntPtr hdc, int l, int t, int r, int b, int x1, int y1, int x2, int y2);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr hdc, ref RECT rect, IntPtr brush);
}
