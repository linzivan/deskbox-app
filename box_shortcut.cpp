// KeyBox — interactive desktop shortcut cheatsheet.
//
// A desktop-pinned box that shows a list of keyboard shortcuts ("key = description").
// Editing is done ON the box (no text file): click "+" to add, click a row to edit,
// right-click a row for 删除/上移/下移. When the shortcut field is focused, press a key
// combo and it is captured into the field (Ctrl/Alt/Shift/Win modifiers + F1-F24, arrows,
// media keys ...). Data is persisted to shortcuts.txt next to the exe.
//
// Architecture reused from DeskBox: C++ (MSVC) + Win32 + Direct2D/DirectWrite, a layered
// captionless window pinned above Progman, Win+D-proof (refuse SC_MINIMIZE + strip
// SWP_HIDEWINDOW + 2s watchdog).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <dwmapi.h>
#include <wrl/client.h>
#include <commctrl.h>

#include <string>
#include <vector>
#include <fstream>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <tlhelp32.h>
#include <algorithm>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "comctl32.lib")

// Common Controls v6 manifest: enables visual styles AND the EDIT cue-banner
// (placeholder) support that EM_SETCUEBANNER depends on.
#pragma comment(linker,"/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

#ifndef EM_SETCUEBANNER
#define EM_SETCUEBANNER 0x1501
#endif

using Microsoft::WRL::ComPtr;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------
static const int  DEFAULT_W = 360;
static const int  TITLE_H   = 32;   // drag strip (title) height
static const int  ROW_H     = 30;   // per-entry row height
static const int  PAD       = 12;   // horizontal padding
static const int  GAP       = 10;   // key<->desc gap
static const UINT TIMER_PIN    = 1;
static const UINT PIN_MS       = 2000;
static const UINT TIMER_RELOAD = 2;
static const UINT RELOAD_MS    = 2000;

// dialog control ids
enum { ID_KEY = 101, ID_DESC = 102, ID_OK = 1, ID_CANCEL = 2 };

// ---------------------------------------------------------------------------
// Global state
// ---------------------------------------------------------------------------
static HWND g_hwnd = nullptr;

static ComPtr<ID2D1Factory>         g_d2dFactory;
static ComPtr<ID2D1HwndRenderTarget> g_rt;
static ComPtr<IDWriteFactory>       g_dwriteFactory;
static ComPtr<ID2D1SolidColorBrush> g_bgBrush, g_mutedBrush, g_whiteBrush,
                                     g_keyBrush, g_plusBrush, g_dimBrush;
static ComPtr<IDWriteTextFormat>    g_titleFmt, g_keyFmt, g_descFmt, g_plusFmt;

static std::vector<std::pair<std::wstring, std::wstring>> g_entries;
static std::wstring g_exeDirW;
static std::string  g_exeDir;
static int g_fontSize = 16;
static unsigned int g_bgColor = 0x1E1E2E;  // background RGB (dark)
static int g_opacity = 204;                 // window alpha 0-255 (lower = more transparent)
static unsigned int g_textColor = 0xCDD6F4; // description text RGB
static long long g_fileMtime = 0;

// layout rects (recomputed on every render), used for hit-testing
static RECT g_plusRect = {};
static std::vector<RECT> g_rowRects;

// ---------------------------------------------------------------------------
// Logger
// ---------------------------------------------------------------------------
static void Log(const char* fmt, ...) {
    char buf[512];
    va_list ap; va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    std::ofstream f(g_exeDir + "\\keybox.log", std::ios::app);
    if (f.is_open()) {
        SYSTEMTIME st; GetLocalTime(&st);
        char ts[64];
        snprintf(ts, sizeof(ts), "%04d-%02d-%02d %02d:%02d:%02d",
                 st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        f << "[" << ts << "] " << buf << "\n";
    }
}

// ---------------------------------------------------------------------------
// Paths / encoding
// ---------------------------------------------------------------------------
static std::wstring ExeDirW() {
    wchar_t buf[MAX_PATH];
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    std::wstring p(buf);
    size_t i = p.find_last_of(L"\\/");
    return i == std::wstring::npos ? L"." : p.substr(0, i);
}

static std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

static std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

static std::wstring DecodeFile(const std::string& bytes) {
    if (bytes.empty()) return L"";
    const char* p = bytes.data();
    size_t n = bytes.size();
    UINT cp = CP_UTF8;
    if (n >= 3 && (unsigned char)p[0] == 0xEF && (unsigned char)p[1] == 0xBB && (unsigned char)p[2] == 0xBF) {
        p += 3; n -= 3;
    } else {
        int wl = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes.data(), (int)bytes.size(), nullptr, 0);
        if (wl == 0) cp = CP_ACP; // not valid UTF-8 -> assume GBK/ANSI
    }
    int wl = MultiByteToWideChar(cp, 0, p, (int)n, nullptr, 0);
    std::wstring w(wl, 0);
    if (wl) MultiByteToWideChar(cp, 0, p, (int)n, &w[0], wl);
    return w;
}

static std::wstring Trim(const std::wstring& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == L' ' || s[a] == L'\t' || s[a] == L'\r' || s[a] == L'\n')) a++;
    while (b > a && (s[b - 1] == L' ' || s[b - 1] == L'\t' || s[b - 1] == L'\r' || s[b - 1] == L'\n')) b--;
    return s.substr(a, b - a);
}

// ---------------------------------------------------------------------------
// Data file (shortcuts.txt, UTF-8 with BOM)
// ---------------------------------------------------------------------------
static void LoadEntries() {
    g_entries.clear();
    std::ifstream f(g_exeDir + "\\shortcuts.txt", std::ios::binary);
    if (!f.is_open()) return;
    std::string bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    std::wstring text = DecodeFile(bytes);
    size_t pos = 0;
    while (pos <= text.size()) {
        size_t nl = text.find(L'\n', pos);
        std::wstring line = (nl == std::wstring::npos) ? text.substr(pos) : text.substr(pos, nl - pos);
        pos = (nl == std::wstring::npos) ? text.size() + 1 : nl + 1;
        while (!line.empty() && line.back() == L'\r') line.pop_back();
        std::wstring t = Trim(line);
        if (t.empty() || t[0] == L'#') continue;
        size_t eq = t.find(L'=');
        if (eq == std::wstring::npos) continue;
        std::wstring key = Trim(t.substr(0, eq));
        std::wstring desc = Trim(t.substr(eq + 1));
        if (key.empty()) continue;
        g_entries.push_back({ key, desc });
    }
    g_fileMtime = 0;
}

static void SaveEntries() {
    std::string out = "\xEF\xBB\xBF"; // UTF-8 BOM
    for (auto& e : g_entries)
        out += WideToUtf8(e.first) + " = " + WideToUtf8(e.second) + "\n";
    std::ofstream f(g_exeDir + "\\shortcuts.txt", std::ios::binary | std::ios::trunc);
    if (f.is_open()) f.write(out.data(), (std::streamsize)out.size());
    g_fileMtime = 0;
}

static long long FileMtime() {
    WIN32_FILE_ATTRIBUTE_DATA d = {};
    std::wstring p = g_exeDirW + L"\\shortcuts.txt";
    if (GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &d)) {
        ULARGE_INTEGER u; u.LowPart = d.ftLastWriteTime.dwLowDateTime; u.HighPart = d.ftLastWriteTime.dwHighDateTime;
        return (long long)u.QuadPart;
    }
    return 0;
}

// ---------------------------------------------------------------------------
// Key name mapping (VK -> display string)
// ---------------------------------------------------------------------------
static std::wstring VkName(UINT vk) {
    if (vk >= 'A' && vk <= 'Z') return std::wstring(1, (wchar_t)vk);
    if (vk >= '0' && vk <= '9') return std::wstring(1, (wchar_t)vk);
    if (vk >= VK_F1 && vk <= VK_F24) { wchar_t b[8]; swprintf_s(b, L"F%d", vk - VK_F1 + 1); return b; }
    if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) { wchar_t b[8]; swprintf_s(b, L"Num%d", vk - VK_NUMPAD0); return b; }
    switch (vk) {
    case VK_UP:    return L"Up";
    case VK_DOWN:  return L"Down";
    case VK_LEFT:  return L"Left";
    case VK_RIGHT: return L"Right";
    case VK_RETURN: return L"Enter";
    case VK_SPACE:  return L"Space";
    case VK_TAB:    return L"Tab";
    case VK_ESCAPE: return L"Esc";
    case VK_BACK:   return L"Backspace";
    case VK_DELETE: return L"Delete";
    case VK_INSERT: return L"Insert";
    case VK_HOME:   return L"Home";
    case VK_END:    return L"End";
    case VK_PRIOR:  return L"PgUp";
    case VK_NEXT:   return L"PgDn";
    case VK_CAPITAL: return L"CapsLock";
    case VK_LSHIFT: case VK_RSHIFT: return L"Shift";
    case VK_LCONTROL: case VK_RCONTROL: return L"Ctrl";
    case VK_LMENU: case VK_RMENU: return L"Alt";
    case VK_LWIN: case VK_RWIN: return L"Win";
    case VK_SNAPSHOT: return L"PrintScreen";
    case VK_SCROLL:   return L"ScrollLock";
    case VK_PAUSE:    return L"Pause";
    case VK_APPS:     return L"Menu";
    case VK_MULTIPLY: return L"Num*";
    case VK_ADD:      return L"Num+";
    case VK_SUBTRACT: return L"Num-";
    case VK_DECIMAL:  return L"Num.";
    case VK_DIVIDE:   return L"Num/";
    case VK_OEM_1:    return L";";
    case VK_OEM_2:    return L"/";
    case VK_OEM_3:    return L"`";
    case VK_OEM_4:    return L"[";
    case VK_OEM_5:    return L"\\";
    case VK_OEM_6:    return L"]";
    case VK_OEM_7:    return L"'";
    case VK_OEM_PLUS: return L"=";
    case VK_OEM_COMMA: return L",";
    case VK_OEM_MINUS: return L"-";
    case VK_OEM_PERIOD: return L".";
    // media / extra keys
    case VK_VOLUME_MUTE: return L"静音";
    case VK_VOLUME_DOWN: return L"音量-";
    case VK_VOLUME_UP:   return L"音量+";
    case VK_MEDIA_NEXT_TRACK: return L"下一曲";
    case VK_MEDIA_PREV_TRACK: return L"上一曲";
    case VK_MEDIA_STOP:       return L"停止";
    case VK_MEDIA_PLAY_PAUSE: return L"播放/暂停";
    case VK_BROWSER_BACK:    return L"浏览器后退";
    case VK_BROWSER_FORWARD: return L"浏览器前进";
    case VK_BROWSER_HOME:    return L"浏览器主页";
    }
    wchar_t b[16]; swprintf_s(b, L"VK_0x%02X", vk);
    return b;
}

// ---------------------------------------------------------------------------
// D2D resources
// ---------------------------------------------------------------------------
static float MeasureText(IDWriteTextFormat* fmt, const std::wstring& s) {
    if (s.empty()) return 0;
    ComPtr<IDWriteTextLayout> layout;
    if (FAILED(g_dwriteFactory->CreateTextLayout(s.c_str(), (UINT32)s.size(), fmt,
                                                 10000, 10000, layout.GetAddressOf())))
        return 0;
    DWRITE_TEXT_METRICS m = {};
    layout->GetMetrics(&m);
    return m.widthIncludingTrailingWhitespace;
}

static void CreateTextFormats() {
    g_titleFmt.Reset(); g_keyFmt.Reset(); g_descFmt.Reset(); g_plusFmt.Reset();
    g_dwriteFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, (float)g_fontSize * 0.8f, L"zh-cn",
        g_titleFmt.GetAddressOf());
    g_dwriteFactory->CreateTextFormat(L"Consolas", nullptr, DWRITE_FONT_WEIGHT_BOLD,
        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, (float)g_fontSize, L"zh-cn",
        g_keyFmt.GetAddressOf());
    g_dwriteFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, (float)g_fontSize, L"zh-cn",
        g_descFmt.GetAddressOf());
    g_dwriteFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_BOLD,
        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, (float)g_fontSize * 1.1f, L"zh-cn",
        g_plusFmt.GetAddressOf());
    if (g_plusFmt) {
        g_plusFmt->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
        g_plusFmt->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
    }
}

static bool InitD2D() {
    g_d2dFactory.Reset(); g_rt.Reset(); g_dwriteFactory.Reset();
    g_bgBrush.Reset(); g_mutedBrush.Reset(); g_whiteBrush.Reset();
    g_keyBrush.Reset(); g_plusBrush.Reset(); g_dimBrush.Reset();
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, g_d2dFactory.GetAddressOf()))) return false;
    RECT rc; GetClientRect(g_hwnd, &rc);
    if (FAILED(g_d2dFactory->CreateHwndRenderTarget(
        D2D1::RenderTargetProperties(),
        D2D1::HwndRenderTargetProperties(g_hwnd, D2D1::SizeU(rc.right ? rc.right : 1, rc.bottom ? rc.bottom : 1)),
        g_rt.GetAddressOf()))) return false;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
        reinterpret_cast<IUnknown**>(g_dwriteFactory.GetAddressOf())))) return false;

    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x1E1E2E, 1.0f), g_bgBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x9399B2, 1.0f), g_mutedBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(g_textColor, 1.0f), g_whiteBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x89DCEB, 1.0f), g_keyBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0xA6E3A1, 1.0f), g_plusBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x6C7086, 1.0f), g_dimBrush.GetAddressOf());

    CreateTextFormats();
    return true;
}

// ---------------------------------------------------------------------------
// Layout + render
// ---------------------------------------------------------------------------
static void UpdateLayout() {
    D2D1_SIZE_F sz = g_rt ? g_rt->GetSize() : D2D1::SizeF((float)DEFAULT_W, TITLE_H);
    float w = sz.width;

    // "+" button (top-right)
    g_plusRect = { (LONG)(w - 34), 4, (LONG)(w - 8), TITLE_H - 4 };

    // entry rows
    g_rowRects.clear();
    size_t n = g_entries.empty() ? 1 : g_entries.size();
    for (size_t i = 0; i < n; i++) {
        RECT r = { 0, TITLE_H + (LONG)i * ROW_H, (LONG)w, TITLE_H + (LONG)(i + 1) * ROW_H };
        g_rowRects.push_back(r);
    }

    // auto-height
    int wantH = TITLE_H + (int)n * ROW_H + 8;
    RECT wr; GetWindowRect(g_hwnd, &wr);
    int curH = wr.bottom - wr.top;
    if (wantH != curH) {
        SetWindowPos(g_hwnd, nullptr, 0, 0, wr.right - wr.left, wantH,
                     SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

static void Render() {
    if (!g_rt) return;
    UpdateLayout();
    g_rt->BeginDraw();
    g_rt->Clear(D2D1::ColorF(g_bgColor, 1.0f));
    D2D1_SIZE_F sz = g_rt->GetSize();
    float w = sz.width;

    // title
    g_rt->DrawTextW(L"快捷键", 3, g_titleFmt.Get(), D2D1::RectF((float)PAD, 0, w - 40, (float)TITLE_H), g_mutedBrush.Get());
    // "+" button
    g_rt->DrawTextW(L"+", 1, g_plusFmt.Get(),
        D2D1::RectF((float)g_plusRect.left, (float)g_plusRect.top, (float)g_plusRect.right, (float)g_plusRect.bottom),
        g_plusBrush.Get());

    // measure key column width
    float keyCol = 40;
    for (auto& e : g_entries)
        keyCol = std::max(keyCol, MeasureText(g_keyFmt.Get(), e.first) + GAP);

    if (g_entries.empty()) {
        g_rt->DrawTextW(L"点右上角 + 添加快捷键", 12, g_descFmt.Get(),
            D2D1::RectF((float)PAD, (float)TITLE_H, w - PAD, (float)(TITLE_H + ROW_H)), g_dimBrush.Get());
    } else {
        for (size_t i = 0; i < g_entries.size(); i++) {
            float y = (float)(TITLE_H + i * ROW_H);
            g_rt->DrawTextW(g_entries[i].first.c_str(), (UINT32)g_entries[i].first.size(), g_keyFmt.Get(),
                D2D1::RectF((float)PAD, y, (float)PAD + keyCol, y + ROW_H), g_keyBrush.Get());
            g_rt->DrawTextW(g_entries[i].second.c_str(), (UINT32)g_entries[i].second.size(), g_descFmt.Get(),
                D2D1::RectF((float)PAD + keyCol, y, w - PAD, y + ROW_H), g_whiteBrush.Get());
        }
    }
    g_rt->EndDraw();
}

// ---------------------------------------------------------------------------
// Add / edit dialog (with key capture)
// ---------------------------------------------------------------------------
static HWND g_dlgKey = nullptr, g_dlgDesc = nullptr;
static bool g_dlgDone = false, g_dlgOk = false;
static WNDPROC g_keyOrigProc = nullptr;
static std::wstring g_resultKey, g_resultDesc;
static HHOOK g_llHook = nullptr;
static HWND  g_captureTarget = nullptr;

// Low-level keyboard hook, installed while the shortcut field has focus. It runs
// BEFORE the system's global-hotkey dispatch, so a combo already bound elsewhere
// (e.g. Ctrl+1 -> screenshot) is swallowed here and recorded instead of triggering
// that other action.
static LRESULT CALLBACK LLKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) {
    if (nCode == HC_ACTION && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)) {
        KBDLLHOOKSTRUCT* k = (KBDLLHOOKSTRUCT*)lParam;
        UINT vk = k->vkCode;
        bool ctrl  = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
        bool alt   = (GetKeyState(VK_MENU) & 0x8000) != 0;
        bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        bool win   = ((GetKeyState(VK_LWIN) & 0x8000) != 0) || ((GetKeyState(VK_RWIN) & 0x8000) != 0);

        // pure modifier press: pass through, wait for the real key
        if (vk == VK_CONTROL || vk == VK_MENU || vk == VK_SHIFT ||
            vk == VK_LWIN || vk == VK_RWIN || vk == VK_LCONTROL || vk == VK_RCONTROL ||
            vk == VK_LMENU || vk == VK_RMENU || vk == VK_LSHIFT || vk == VK_RSHIFT)
            return CallNextHookEx(nullptr, nCode, wParam, lParam);

        bool hasModifier = ctrl || alt || win; // Shift is a typing modifier, not a trigger
        bool isSpecial =
            (vk >= VK_F1 && vk <= VK_F24) ||
            (vk >= VK_UP && vk <= VK_DOWN) ||
            (vk >= VK_VOLUME_MUTE && vk <= VK_LAUNCH_APP2) ||
            vk == VK_HOME || vk == VK_END || vk == VK_PRIOR || vk == VK_NEXT ||
            vk == VK_INSERT || vk == VK_DELETE;

        if (hasModifier || isSpecial) {
            std::wstring s;
            if (ctrl)  s += L"Ctrl+";
            if (alt)   s += L"Alt+";
            if (shift) s += L"Shift+";
            if (win)   s += L"Win+";
            s += VkName(vk);
            if (g_captureTarget)
                SetWindowTextW(g_captureTarget, s.c_str());
            return 1; // swallow: keeps the combo from reaching its registered owner
        }
        // plain char: pass through so the EDIT types it (manual "Fn+W" etc.)
    }
    return CallNextHookEx(nullptr, nCode, wParam, lParam);
}

static LRESULT CALLBACK KeyEditProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_SETFOCUS:
        g_captureTarget = hwnd;
        if (!g_llHook)
            g_llHook = SetWindowsHookExW(WH_KEYBOARD_LL, LLKeyboardProc, GetModuleHandleW(nullptr), 0);
        break;
    case WM_KILLFOCUS:
        if (g_llHook) { UnhookWindowsHookEx(g_llHook); g_llHook = nullptr; }
        g_captureTarget = nullptr;
        break;
    case WM_DESTROY:
        if (g_llHook) { UnhookWindowsHookEx(g_llHook); g_llHook = nullptr; }
        g_captureTarget = nullptr;
        break;
    }
    return CallWindowProcW(g_keyOrigProc, hwnd, msg, wp, lp);
}

static LRESULT CALLBACK DlgProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_COMMAND:
        switch (LOWORD(wp)) {
        case ID_OK: {
            // Read the fields BEFORE DestroyWindow — destroying the dialog also
            // destroys the EDIT children, so they are unreadable afterwards.
            wchar_t k[256] = {}, d[1024] = {};
            GetWindowTextW(g_dlgKey, k, 256);
            GetWindowTextW(g_dlgDesc, d, 1024);
            g_resultKey = Trim(k);
            g_resultDesc = Trim(d);
            g_dlgOk = true; g_dlgDone = true; DestroyWindow(hwnd); return 0;
        }
        case ID_CANCEL: g_dlgOk = false; g_dlgDone = true; DestroyWindow(hwnd); return 0;
        }
        break;
    case WM_CLOSE:
        g_dlgOk = false; g_dlgDone = true; DestroyWindow(hwnd); return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// Returns true on OK, filling key/desc.
static bool ShowEditDialog(HWND parent, std::wstring& key, std::wstring& desc) {
    WNDCLASSW wc = {};
    wc.lpfnWndProc = DlgProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"KeyBoxDlg";
    wc.hbrBackground = (HBRUSH)(COLOR_BTNFACE + 1);
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassW(&wc);

    int w = 400, h = 170;
    RECT pr; GetWindowRect(parent, &pr);
    int x = pr.left + ((pr.right - pr.left) - w) / 2;
    int y = pr.top + ((pr.bottom - pr.top) - h) / 2;

    g_dlgDone = false; g_dlgOk = false;
    HWND dlg = CreateWindowExW(WS_EX_DLGMODALFRAME, L"KeyBoxDlg",
        desc.empty() ? L"添加快捷键" : L"编辑快捷键",
        WS_CAPTION | WS_SYSMENU | WS_POPUP, x, y, w, h, parent, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!dlg) return false;

    HFONT fnt = CreateFontW(-12, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");

    auto mkStatic = [&](const wchar_t* txt, int X, int Y, int W, int H) {
        HWND c = CreateWindowExW(0, L"STATIC", txt, WS_CHILD | WS_VISIBLE, X, Y, W, H, dlg, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (fnt) SendMessageW(c, WM_SETFONT, (WPARAM)fnt, TRUE);
        return c;
    };
    auto mkEdit = [&](const wchar_t* txt, int X, int Y, int W, int H, int id) {
        HWND c = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", txt, WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL,
            X, Y, W, H, dlg, (HMENU)(INT_PTR)id, GetModuleHandleW(nullptr), nullptr);
        if (fnt) SendMessageW(c, WM_SETFONT, (WPARAM)fnt, TRUE);
        return c;
    };

    mkStatic(L"快捷键", 16, 18, 56, 20);
    g_dlgKey = mkEdit(key.c_str(), 76, 14, 292, 26, ID_KEY);
    SendMessageW(g_dlgKey, EM_SETCUEBANNER, TRUE, (LPARAM)L"点此框后按按键");

    mkStatic(L"描述", 16, 52, 56, 20);
    g_dlgDesc = mkEdit(desc.c_str(), 76, 48, 292, 26, ID_DESC);

    HWND ok = CreateWindowExW(0, L"BUTTON", L"确定", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
        208, 90, 80, 30, dlg, (HMENU)(INT_PTR)ID_OK, GetModuleHandleW(nullptr), nullptr);
    HWND cancel = CreateWindowExW(0, L"BUTTON", L"取消", WS_CHILD | WS_VISIBLE | WS_TABSTOP,
        288, 90, 80, 30, dlg, (HMENU)(INT_PTR)ID_CANCEL, GetModuleHandleW(nullptr), nullptr);
    if (fnt) { SendMessageW(ok, WM_SETFONT, (WPARAM)fnt, TRUE); SendMessageW(cancel, WM_SETFONT, (WPARAM)fnt, TRUE); }

    // subclass the key field to capture key combos
    g_keyOrigProc = (WNDPROC)SetWindowLongPtrW(g_dlgKey, GWLP_WNDPROC, (LONG_PTR)KeyEditProc);

    EnableWindow(parent, FALSE);
    ShowWindow(dlg, SW_SHOWNORMAL);
    SetForegroundWindow(dlg);
    SetFocus(g_dlgKey);

    // modal loop
    while (!g_dlgDone) {
        MSG m;
        if (GetMessageW(&m, nullptr, 0, 0) > 0) {
            if (IsDialogMessageW(dlg, &m)) continue;
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }
    }

    if (g_dlgOk) {
        key = g_resultKey;
        desc = g_resultDesc;
    }
    if (fnt) DeleteObject(fnt);
    EnableWindow(parent, TRUE);
    SetForegroundWindow(parent);
    return g_dlgOk && !key.empty();
}

// ---------------------------------------------------------------------------
// Entry operations
// ---------------------------------------------------------------------------
static void ReloadIfChanged() {
    long long mt = FileMtime();
    if (g_fileMtime == 0) { g_fileMtime = mt; return; }
    if (mt != g_fileMtime) { g_fileMtime = mt; LoadEntries(); Render(); }
}

static void AddEntry() {
    std::wstring key, desc;
    if (ShowEditDialog(g_hwnd, key, desc)) {
        g_entries.push_back({ key, desc });
        SaveEntries(); Render();
    }
}

static void EditEntry(size_t i) {
    std::wstring key = g_entries[i].first, desc = g_entries[i].second;
    if (ShowEditDialog(g_hwnd, key, desc)) {
        g_entries[i] = { key, desc };
        SaveEntries(); Render();
    }
}

static void DeleteEntry(size_t i) {
    if (i < g_entries.size()) {
        g_entries.erase(g_entries.begin() + i);
        SaveEntries(); Render();
    }
}

static void MoveEntry(size_t i, int delta) {
    size_t j = i + delta;
    if (i < g_entries.size() && j < g_entries.size()) {
        std::swap(g_entries[i], g_entries[j]);
        SaveEntries(); Render();
    }
}

// ---------------------------------------------------------------------------
// Window proc
// ---------------------------------------------------------------------------
static void PinToDesktop(HWND hwnd);
static void SaveGeometry(HWND hwnd);

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_NCHITTEST: {
        POINT p = { (short)LOWORD(lp), (short)HIWORD(lp) };
        ScreenToClient(hwnd, &p);
        RECT rc; GetClientRect(hwnd, &rc);
        const int edge = 8;
        bool right = p.x >= rc.right - edge;
        bool bottom = p.y >= rc.bottom - edge;
        if (right && bottom) return HTBOTTOMRIGHT;
        if (right)  return HTRIGHT;
        if (bottom) return HTBOTTOM;
        if (PtInRect(&g_plusRect, p)) return HTCLIENT;       // "+" is clickable
        for (auto& r : g_rowRects) if (PtInRect(&r, p)) return HTCLIENT; // rows are clickable
        return HTCAPTION; // title strip / empty space drags the window
    }
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;

    case WM_LBUTTONDOWN: {
        POINT p = { (short)LOWORD(lp), (short)HIWORD(lp) };
        if (PtInRect(&g_plusRect, p)) { AddEntry(); return 0; }
        for (size_t i = 0; i < g_rowRects.size(); i++)
            if (PtInRect(&g_rowRects[i], p)) { if (i < g_entries.size()) EditEntry(i); return 0; }
        return 0;
    }

    case WM_RBUTTONUP: {
        POINT p = { (short)LOWORD(lp), (short)HIWORD(lp) };
        for (size_t i = 0; i < g_rowRects.size(); i++) {
            if (i < g_entries.size() && PtInRect(&g_rowRects[i], p)) {
                POINT sp; GetCursorPos(&sp);
                HMENU m = CreatePopupMenu();
                AppendMenuW(m, MF_STRING, 1, L"编辑");
                AppendMenuW(m, MF_STRING, 2, L"删除");
                AppendMenuW(m, MF_SEPARATOR, 0, nullptr);
                AppendMenuW(m, MF_STRING | (i == 0 ? MF_GRAYED : 0), 3, L"上移");
                AppendMenuW(m, MF_STRING | (i == g_entries.size() - 1 ? MF_GRAYED : 0), 4, L"下移");
                SetForegroundWindow(hwnd);
                int cmd = (int)TrackPopupMenu(m, TPM_RETURNCMD | TPM_RIGHTBUTTON, sp.x, sp.y, 0, hwnd, nullptr);
                PostMessageW(hwnd, WM_NULL, 0, 0);
                DestroyMenu(m);
                switch (cmd) {
                case 1: EditEntry(i); break;
                case 2: DeleteEntry(i); break;
                case 3: MoveEntry(i, -1); break;
                case 4: MoveEntry(i, +1); break;
                }
                return 0;
            }
        }
        return 0;
    }

    case WM_NCRBUTTONUP: {
        // Right-click the title strip (non-client) -> appearance settings.
        POINT sp; GetCursorPos(&sp);
        HMENU bgSub = CreatePopupMenu();
        AppendMenuW(bgSub, MF_STRING | (g_bgColor == 0x1E1E2E ? MF_CHECKED : 0), 101, L"深色（默认）");
        AppendMenuW(bgSub, MF_STRING | (g_bgColor == 0x000000 ? MF_CHECKED : 0), 102, L"纯黑");
        AppendMenuW(bgSub, MF_STRING | (g_bgColor == 0x16213E ? MF_CHECKED : 0), 103, L"深蓝");
        AppendMenuW(bgSub, MF_STRING | (g_bgColor == 0x2A2A2A ? MF_CHECKED : 0), 104, L"深灰");
        HMENU textSub = CreatePopupMenu();
        AppendMenuW(textSub, MF_STRING | (g_textColor == 0xCDD6F4 ? MF_CHECKED : 0), 121, L"白色（默认）");
        AppendMenuW(textSub, MF_STRING | (g_textColor == 0xFFFFFF ? MF_CHECKED : 0), 122, L"纯白");
        AppendMenuW(textSub, MF_STRING | (g_textColor == 0xB0B0C0 ? MF_CHECKED : 0), 123, L"浅灰");
        AppendMenuW(textSub, MF_STRING | (g_textColor == 0xF9E2AF ? MF_CHECKED : 0), 124, L"黄色");
        AppendMenuW(textSub, MF_STRING | (g_textColor == 0xA6E3A1 ? MF_CHECKED : 0), 125, L"浅绿");
        AppendMenuW(textSub, MF_STRING | (g_textColor == 0xF38BA8 ? MF_CHECKED : 0), 126, L"浅红");
        HMENU opSub = CreatePopupMenu();
        AppendMenuW(opSub, MF_STRING | (g_opacity == 255 ? MF_CHECKED : 0), 111, L"100%（不透明）");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 230 ? MF_CHECKED : 0), 112, L"90%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 204 ? MF_CHECKED : 0), 113, L"80%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 179 ? MF_CHECKED : 0), 114, L"70%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 153 ? MF_CHECKED : 0), 115, L"60%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 128 ? MF_CHECKED : 0), 116, L"50%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 102 ? MF_CHECKED : 0), 117, L"40%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 77  ? MF_CHECKED : 0), 118, L"30%");
        AppendMenuW(opSub, MF_STRING | (g_opacity == 51  ? MF_CHECKED : 0), 119, L"20%");
        HMENU menu = CreatePopupMenu();
        AppendMenuW(menu, MF_POPUP, (UINT_PTR)bgSub, L"背景颜色");
        AppendMenuW(menu, MF_POPUP, (UINT_PTR)textSub, L"字体颜色");
        AppendMenuW(menu, MF_POPUP, (UINT_PTR)opSub, L"透明度");
        SetForegroundWindow(hwnd);
        int cmd = (int)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, sp.x, sp.y, 0, hwnd, nullptr);
        PostMessageW(hwnd, WM_NULL, 0, 0);
        DestroyMenu(menu);
        switch (cmd) {
        case 101: g_bgColor = 0x1E1E2E; Render(); break;
        case 102: g_bgColor = 0x000000; Render(); break;
        case 103: g_bgColor = 0x16213E; Render(); break;
        case 104: g_bgColor = 0x2A2A2A; Render(); break;
        case 111: g_opacity = 255; break;
        case 112: g_opacity = 230; break;
        case 113: g_opacity = 204; break;
        case 114: g_opacity = 179; break;
        case 115: g_opacity = 153; break;
        case 116: g_opacity = 128; break;
        case 117: g_opacity = 102; break;
        case 118: g_opacity = 77; break;
        case 119: g_opacity = 51; break;
        case 121: g_textColor = 0xCDD6F4; break;
        case 122: g_textColor = 0xFFFFFF; break;
        case 123: g_textColor = 0xB0B0C0; break;
        case 124: g_textColor = 0xF9E2AF; break;
        case 125: g_textColor = 0xA6E3A1; break;
        case 126: g_textColor = 0xF38BA8; break;
        }
        if (cmd >= 111 && cmd <= 119)
            SetLayeredWindowAttributes(hwnd, 0, (BYTE)g_opacity, LWA_ALPHA);
        if (cmd >= 121 && cmd <= 126) {
            if (g_whiteBrush)
                g_whiteBrush->SetColor(D2D1::ColorF(g_textColor, 1.0f));
            Render();
        }
        if (cmd >= 101 && cmd <= 126)
            SaveGeometry(hwnd);
        return 0;
    }

    case WM_SYSCOMMAND:
        if ((wp & 0xFFF0) == SC_MINIMIZE) return 0; // survive Win+D
        break;
    case WM_WINDOWPOSCHANGING: {
        WINDOWPOS* pos = reinterpret_cast<WINDOWPOS*>(lp);
        pos->flags &= ~SWP_HIDEWINDOW;
        return 0;
    }

    case WM_ERASEBKGND:
        return 1;
    case WM_SIZE:
        if (g_rt && LOWORD(lp) > 0 && HIWORD(lp) > 0) {
            g_rt->Resize(D2D1::SizeU(LOWORD(lp), HIWORD(lp)));
            Render();
        }
        return 0;
    case WM_EXITSIZEMOVE:
        SaveGeometry(hwnd); // remember position + size after a move/resize
        return 0;
    case WM_PAINT:
    case WM_DISPLAYCHANGE:
        Render();
        ValidateRect(hwnd, nullptr);
        return 0;

    case WM_TIMER:
        if (wp == TIMER_PIN) {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            PinToDesktop(hwnd);
        } else if (wp == TIMER_RELOAD) {
            ReloadIfChanged();
        }
        return 0;

    case WM_DESTROY:
        SaveGeometry(hwnd);
        KillTimer(hwnd, TIMER_PIN);
        KillTimer(hwnd, TIMER_RELOAD);
        if (hwnd == g_hwnd) g_hwnd = nullptr;
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static void PinToDesktop(HWND hwnd) {
    HWND progman = FindWindowW(L"Progman", nullptr);
    if (!progman) { Log("Progman not found - floating window"); return; }
    SetWindowPos(hwnd, progman, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    static HWND last = nullptr;
    if (progman != last) { last = progman; Log("pinned above Progman=0x%p", progman); }
}

static void KillOtherInstances() {
    wchar_t selfPath[MAX_PATH];
    GetModuleFileNameW(nullptr, selfPath, MAX_PATH);
    wchar_t* selfName = wcsrchr(selfPath, L'\\');
    selfName = selfName ? selfName + 1 : selfPath;
    DWORD myPid = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    PROCESSENTRY32W pe = {}; pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe)) {
        do {
            if (pe.th32ProcessID != myPid && lstrcmpiW(pe.szExeFile, selfName) == 0) {
                HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, pe.th32ProcessID);
                if (h) { TerminateProcess(h, 0); CloseHandle(h); }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
}

static void LoadGeometry(int& x, int& y, int& w, int& h) {
    RECT wa; SystemParametersInfoW(SPI_GETWORKAREA, 0, &wa, 0);
    x = wa.right - DEFAULT_W - 40; y = wa.top + 120; w = DEFAULT_W; h = TITLE_H + 4 * ROW_H + 8;
    std::ifstream f(g_exeDir + "\\keybox.cfg");
    if (!f.is_open()) return;
    std::string line;
    while (std::getline(f, line)) {
        if (line.rfind("x=", 0) == 0) x = atoi(line.c_str() + 2);
        else if (line.rfind("y=", 0) == 0) y = atoi(line.c_str() + 2);
        else if (line.rfind("w=", 0) == 0) w = atoi(line.c_str() + 2);
        else if (line.rfind("h=", 0) == 0) h = atoi(line.c_str() + 2);
        else if (line.rfind("font=", 0) == 0) g_fontSize = atoi(line.c_str() + 5);
        else if (line.rfind("bg=", 0) == 0) g_bgColor = (unsigned int)strtoul(line.c_str() + 3, nullptr, 16);
        else if (line.rfind("opacity=", 0) == 0) g_opacity = atoi(line.c_str() + 8);
        else if (line.rfind("text=", 0) == 0) g_textColor = (unsigned int)strtoul(line.c_str() + 5, nullptr, 16);
    }
    if (w < 120) w = 120;
    if (h < TITLE_H + ROW_H) h = TITLE_H + ROW_H;
    if (g_fontSize < 12) g_fontSize = 12;
    if (g_fontSize > 40) g_fontSize = 40;
    if (g_opacity < 20) g_opacity = 20;
    if (g_opacity > 255) g_opacity = 255;
}

static void SaveGeometry(HWND hwnd) {
    RECT rc; GetWindowRect(hwnd, &rc);
    std::ofstream f(g_exeDir + "\\keybox.cfg");
    if (!f.is_open()) return;
    char bg[16]; snprintf(bg, sizeof(bg), "%06X", g_bgColor);
    char tc[16]; snprintf(tc, sizeof(tc), "%06X", g_textColor);
    f << "x=" << rc.left << "\ny=" << rc.top << "\nw=" << (rc.right - rc.left)
      << "\nh=" << (rc.bottom - rc.top) << "\nfont=" << g_fontSize
      << "\nbg=" << bg << "\nopacity=" << g_opacity << "\ntext=" << tc << "\n";
}

HWND ShortcutBox_Create() {
    if (g_hwnd) return g_hwnd;
    g_exeDirW = ExeDirW();
    g_exeDir = WideToUtf8(g_exeDirW);
    LoadEntries();
    g_fileMtime = FileMtime();

    int x, y, w, h;
    LoadGeometry(x, y, w, h);

    WNDCLASSW wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"KeyBox";
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr;
    RegisterClassW(&wc);

    g_hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW, L"KeyBox", L"KeyBox", WS_POPUP,
        x, y, w, h, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!g_hwnd) { Log("CreateWindowExW failed"); return nullptr; }

    int corner = 2; // DWMWCP_ROUND
    DwmSetWindowAttribute(g_hwnd, 33, &corner, sizeof(corner));
    SetLayeredWindowAttributes(g_hwnd, 0, (BYTE)g_opacity, LWA_ALPHA);

    if (!InitD2D()) { DestroyWindow(g_hwnd); return nullptr; }
    PinToDesktop(g_hwnd);
    ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(g_hwnd);

    SetTimer(g_hwnd, TIMER_PIN, PIN_MS, nullptr);
    SetTimer(g_hwnd, TIMER_RELOAD, RELOAD_MS, nullptr);
    Render();
    Log("shortcut box created at (%d,%d) size %dx%d", x, y, w, h);
    return g_hwnd;
}

void ShortcutBox_Destroy() {
    if (g_hwnd) DestroyWindow(g_hwnd); // WM_DESTROY clears g_hwnd
}
