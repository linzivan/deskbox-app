// DeskBox — a native Windows desktop box (reference: Coodesker's tech stack).
//
// Tech: C++ (MSVC) + Win32 + Direct2D + DirectWrite. The box is a layered,
// captionless top-level window pinned just above the desktop window (Progman),
// exactly how Coodesker's desktop boxes sit on the desktop layer. Rendering is
// hardware-accelerated Direct2D (D2D1CreateFactory + HWND render target), the
// same family Coodesker uses (it additionally wraps Direct3D11 for its overlay;
// the HWND render target uses D3D under the hood).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <winhttp.h>
#include <dwmapi.h>
#include <wrl/client.h>

#include <string>
#include <vector>
#include <fstream>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <atomic>
#include <mutex>
#include <ctime>
#include <tlhelp32.h>

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

using Microsoft::WRL::ComPtr;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------
static const int    DEFAULT_W = 200;
static const int    DEFAULT_H = 120;
static const double BALANCE_THRESHOLD = 10.0; // green >= this, else red

static const UINT   WM_APP_REFRESH = WM_APP + 1; // balance fetch finished
static const UINT   TIMER_REFRESH  = 1;
static const UINT   REFRESH_MS     = 60000;
static const UINT   TIMER_PIN      = 2;    // desktop-pinning watchdog
static const UINT   PIN_MS         = 2000; // re-assert pin + visibility every 2s

// ---------------------------------------------------------------------------
// Global state
// ---------------------------------------------------------------------------
static HWND                     g_hwnd = nullptr;
static ComPtr<ID2D1Factory>     g_d2dFactory;
static ComPtr<ID2D1HwndRenderTarget> g_rt;
static ComPtr<IDWriteFactory>   g_dwriteFactory;

static ComPtr<ID2D1SolidColorBrush> g_bgBrush, g_mutedBrush, g_dimBrush,
                                      g_greenBrush, g_redBrush, g_cyanBrush,
                                      g_areaBrush;
static ComPtr<IDWriteTextFormat> g_titleFmt, g_balanceFmt, g_rateFmt;

static std::atomic<double> g_balance(-1.0);
static std::atomic<bool>   g_balanceOk(false);

static std::mutex  g_histMu;
static std::vector<std::pair<time_t, double>> g_history; // 24h balance history

static int g_fontSize = 26; // balance font size (adjustable via right-click menu)

static std::string g_apiKey;
static std::string g_exeDir;

// ---------------------------------------------------------------------------
// Minimal file logger (a GUI app has no console)
// ---------------------------------------------------------------------------
static void Log(const char* fmt, ...) {
    char buf[512];
    va_list ap; va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    std::string p = g_exeDir + "\\deskbox.log";
    std::ofstream f(p, std::ios::app);
    if (f.is_open()) {
        SYSTEMTIME st; GetLocalTime(&st);
        char ts[64];
        snprintf(ts, sizeof(ts), "%04d-%02d-%02d %02d:%02d:%02d",
                 st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
        f << "[" << ts << "] " << buf << "\n";
    }
}

// ---------------------------------------------------------------------------
// Config / data files
// ---------------------------------------------------------------------------
static std::wstring ExeDir() {
    wchar_t buf[MAX_PATH];
    GetModuleFileNameW(nullptr, buf, MAX_PATH);
    std::wstring p(buf);
    size_t i = p.find_last_of(L"\\/");
    return i == std::wstring::npos ? L"." : p.substr(0, i);
}

static std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

static std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

static void LoadConfig() {
    // API key: env var DEEPSEEK_API_KEY, else apikey.txt in exe dir (first line).
    char* env = nullptr; size_t len = 0;
    _dupenv_s(&env, &len, "DEEPSEEK_API_KEY");
    if (env && *env) { g_apiKey = env; free(env); }
    else { if (env) free(env); }

    if (g_apiKey.empty()) {
        std::ifstream f(g_exeDir + "\\apikey.txt");
        if (f.is_open()) { std::getline(f, g_apiKey); }
    }
    // trim trailing \r
    while (!g_apiKey.empty() && (g_apiKey.back() == '\r' || g_apiKey.back() == '\n'))
        g_apiKey.pop_back();

    if (g_apiKey.empty())
        Log("no API key configured (set DEEPSEEK_API_KEY or apikey.txt)");
}

// ---------------------------------------------------------------------------
// Balance history (24h trend)
// ---------------------------------------------------------------------------
static void AppendHistory(double bal) {
    time_t now = time(nullptr);
    {
        std::lock_guard<std::mutex> lk(g_histMu);
        g_history.push_back({ now, bal });
        time_t cutoff = now - 32LL * 86400;
        while (!g_history.empty() && g_history.front().first < cutoff)
            g_history.erase(g_history.begin());
    }
    std::ofstream f(g_exeDir + "\\history.csv", std::ios::app);
    if (f.is_open()) f << (long long)now << "," << bal << "\n";
}

static void LoadHistory() {
    std::lock_guard<std::mutex> lk(g_histMu);
    g_history.clear();
    time_t now = time(nullptr);
    time_t cutoff = now - 32LL * 86400;
    std::ifstream f(g_exeDir + "\\history.csv");
    if (!f.is_open()) return;
    std::string line;
    while (std::getline(f, line)) {
        size_t comma = line.find(',');
        if (comma == std::string::npos) continue;
        long long ts = atoll(line.substr(0, comma).c_str());
        double bal = atof(line.substr(comma + 1).c_str());
        if (ts >= cutoff) g_history.push_back({ (time_t)ts, bal });
    }
}

// ---------------------------------------------------------------------------
// Direct2D resources
// ---------------------------------------------------------------------------
static void CreateTextFormats() {
    g_titleFmt.Reset();
    g_balanceFmt.Reset();
    g_rateFmt.Reset();
    g_dwriteFactory->CreateTextFormat(
        L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL, (float)(g_fontSize * 0.6f), L"zh-cn",
        g_titleFmt.GetAddressOf());
    g_dwriteFactory->CreateTextFormat(
        L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL, (float)g_fontSize, L"zh-cn",
        g_balanceFmt.GetAddressOf());
    g_dwriteFactory->CreateTextFormat(
        L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL, (float)(g_fontSize * 0.45f), L"zh-cn",
        g_rateFmt.GetAddressOf());
    for (auto* f : { g_titleFmt.Get(), g_balanceFmt.Get(), g_rateFmt.Get() }) {
        if (f) {
            f->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
            f->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        }
    }
}

static bool InitD2D() {
    g_d2dFactory.Reset(); g_rt.Reset(); g_dwriteFactory.Reset();
    g_bgBrush.Reset(); g_mutedBrush.Reset(); g_dimBrush.Reset();
    g_greenBrush.Reset(); g_redBrush.Reset(); g_cyanBrush.Reset(); g_areaBrush.Reset();
    HRESULT hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, g_d2dFactory.GetAddressOf());
    if (FAILED(hr)) { Log("D2D1CreateFactory failed: 0x%08X", hr); return false; }

    RECT rc; GetClientRect(g_hwnd, &rc);
    hr = g_d2dFactory->CreateHwndRenderTarget(
        D2D1::RenderTargetProperties(),
        D2D1::HwndRenderTargetProperties(g_hwnd, D2D1::SizeU(rc.right, rc.bottom)),
        g_rt.GetAddressOf());
    if (FAILED(hr)) { Log("CreateHwndRenderTarget failed: 0x%08X", hr); return false; }

    hr = DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                             reinterpret_cast<IUnknown**>(g_dwriteFactory.GetAddressOf()));
    if (FAILED(hr)) { Log("DWriteCreateFactory failed: 0x%08X", hr); return false; }

    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x1E1E2E, 1.0f), g_bgBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x9399B2, 1.0f), g_mutedBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x6C7086, 1.0f), g_dimBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0xA6E3A1, 1.0f), g_greenBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0xF38BA8, 1.0f), g_redBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x89DCEB, 1.0f), g_cyanBrush.GetAddressOf());
    g_rt->CreateSolidColorBrush(D2D1::ColorF(0x89DCEB, 0.18f), g_areaBrush.GetAddressOf());

    CreateTextFormats();
    return true;
}

static std::wstring BalanceText() {
    if (!g_balanceOk || g_balance < 0) return L"--.--";
    wchar_t buf[64];
    swprintf_s(buf, L"\u00a5%.2f", (double)g_balance);
    return buf;
}

// Sum of balance decreases since `since` (unix ts), ignoring top-ups (increases).
static double ConsumptionSince(time_t since) {
    std::vector<std::pair<time_t, double>> copy;
    {
        std::lock_guard<std::mutex> lk(g_histMu);
        copy = g_history;
    }
    double total = 0.0;
    for (size_t i = 1; i < copy.size(); i++) {
        if (copy[i - 1].first < since || copy[i].first < since) continue;
        double d = copy[i - 1].second - copy[i].second;
        if (d > 0) total += d;
    }
    return total;
}

static time_t TodayStart() {
    time_t now = time(nullptr);
    struct tm tmv;
    localtime_s(&tmv, &now);
    tmv.tm_hour = 0; tmv.tm_min = 0; tmv.tm_sec = 0;
    return mktime(&tmv);
}

static time_t MonthStart() {
    time_t now = time(nullptr);
    struct tm tmv;
    localtime_s(&tmv, &now);
    tmv.tm_hour = 0; tmv.tm_min = 0; tmv.tm_sec = 0;
    tmv.tm_mday = 1;
    return mktime(&tmv);
}

static std::wstring ConsumptionText() {
    double today = ConsumptionSince(TodayStart());
    double month = ConsumptionSince(MonthStart());
    if (today < 0.001 && month < 0.001) return L"";
    wchar_t buf[128];
    swprintf_s(buf, L"\u4eca\u65e5 \u00a5%.2f \u00b7 \u672c\u6708 \u00a5%.2f", today, month);
    // 今日 ¥2.04 · 本月 ¥5.20
    return buf;
}

static void DrawTrend(float left, float top, float right, float bottom) {
    std::vector<std::pair<time_t, double>> win;
    {
        std::lock_guard<std::mutex> lk(g_histMu);
        time_t cutoff = time(nullptr) - 86400; // trend shows the last 24h
        size_t start = 0;
        while (start < g_history.size() && g_history[start].first < cutoff) start++;
        if (start > 0) start--; // include one point before the window for continuity
        win.assign(g_history.begin() + start, g_history.end());
    }
    if (win.size() < 2) return;

    double minv = 1e18, maxv = -1e18;
    for (auto& p : win) {
        if (p.second < minv) minv = p.second;
        if (p.second > maxv) maxv = p.second;
    }
    if (maxv - minv < 1e-9) { minv -= 1.0; maxv += 1.0; }

    time_t t0 = win.front().first;
    time_t t1 = win.back().first;
    if (t1 <= t0) return;
    float dt = (float)(t1 - t0);
    float dr = (float)(maxv - minv);

    size_t n = win.size();
    std::vector<D2D1_POINT_2F> pts(n);
    for (size_t i = 0; i < n; i++) {
        float x = left + (right - left) * (float)(win[i].first - t0) / dt;
        float y = bottom - (bottom - top) * (float)(win[i].second - minv) / dr;
        pts[i] = D2D1::Point2F(x, y);
    }

    // Area chart: fill the region under the curve, then stroke its top edge.
    ComPtr<ID2D1PathGeometry> geom;
    if (SUCCEEDED(g_d2dFactory->CreatePathGeometry(geom.GetAddressOf()))) {
        ComPtr<ID2D1GeometrySink> sink;
        if (SUCCEEDED(geom->Open(sink.GetAddressOf()))) {
            sink->BeginFigure(D2D1::Point2F(pts[0].x, bottom), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLines(pts.data(), (UINT32)n);
            sink->AddLine(D2D1::Point2F(pts[n - 1].x, bottom));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            sink->Close();
        }
        g_rt->FillGeometry(geom.Get(), g_areaBrush.Get());
        g_rt->DrawGeometry(geom.Get(), g_cyanBrush.Get(), 1.5f);
    }
}

static void Render() {
    if (!g_rt) return;
    g_rt->BeginDraw();
    g_rt->Clear(D2D1::ColorF(0x1E1E2E, 1.0f));

    D2D1_SIZE_F sz = g_rt->GetSize();
    float w = sz.width, h = sz.height;

    float titleH   = (float)(g_fontSize * 0.6f) + 6;
    float balanceH = (float)g_fontSize + 6;
    float rateH    = (float)(g_fontSize * 0.45f) + 4;

    // Title (top)
    g_rt->DrawTextW(L"DeepSeek", 8, g_titleFmt.Get(),
                    D2D1::RectF(0, 4, w, 4 + titleH), g_mutedBrush.Get());

    // Balance (directly below title; green/red/gray)
    ID2D1SolidColorBrush* bb = g_dimBrush.Get();
    if (g_balanceOk && g_balance >= 0)
        bb = (g_balance >= BALANCE_THRESHOLD) ? g_greenBrush.Get() : g_redBrush.Get();
    std::wstring bal = BalanceText();
    g_rt->DrawTextW(bal.c_str(), (UINT32)bal.size(), g_balanceFmt.Get(),
                    D2D1::RectF(0, 4 + titleH, w, 4 + titleH + balanceH), bb);

    // Today / month consumption (below balance)
    std::wstring rate = ConsumptionText();
    if (!rate.empty()) {
        g_rt->DrawTextW(rate.c_str(), (UINT32)rate.size(), g_rateFmt.Get(),
                        D2D1::RectF(0, 4 + titleH + balanceH, w, 4 + titleH + balanceH + rateH),
                        g_dimBrush.Get());
    }

    // Area trend directly below the text, filling the remaining space
    float trendTop = 4 + titleH + balanceH + (rate.empty() ? 0 : rateH) + 2;
    float trendBottom = h - 4;
    if (trendBottom - trendTop > 8)
        DrawTrend(8.0f, trendTop, w - 8.0f, trendBottom);

    g_rt->EndDraw();
}

// ---------------------------------------------------------------------------
// DeepSeek balance via WinHTTP
// ---------------------------------------------------------------------------
static void FetchBalanceThread() {
    std::string key = g_apiKey;
    double bal = -1.0;
    bool ok = false;

    if (key.empty()) { goto done; }

    {
        HINTERNET session = WinHttpOpen(L"DeskBox/1.0",
            WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME,
            WINHTTP_NO_PROXY_BYPASS, 0);
        if (!session) goto done;

        URL_COMPONENTS uc = {};
        uc.dwStructSize = sizeof(uc);
        wchar_t host[256] = {}, path[1024] = {};
        uc.lpszHostName = host; uc.dwHostNameLength = 256;
        uc.lpszUrlPath  = path; uc.dwUrlPathLength  = 1024;

        if (!WinHttpCrackUrl(L"https://api.deepseek.com/user/balance", 0, 0, &uc)) {
            WinHttpCloseHandle(session); goto done;
        }
        HINTERNET conn = WinHttpConnect(session, uc.lpszHostName, uc.nPort, 0);
        if (!conn) { WinHttpCloseHandle(session); goto done; }

        HINTERNET req = WinHttpOpenRequest(conn, L"GET", uc.lpszUrlPath, nullptr,
            WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
        if (req) {
            std::wstring auth = L"Authorization: Bearer " + Utf8ToWide(key) + L"\r\n";
            WinHttpAddRequestHeaders(req, auth.c_str(), (DWORD)-1L, WINHTTP_ADDREQ_FLAG_ADD);
            if (WinHttpSendRequest(req, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                                   WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
                WinHttpReceiveResponse(req, nullptr)) {
                std::string body;
                DWORD avail = 0;
                do {
                    avail = 0;
                    if (!WinHttpQueryDataAvailable(req, &avail)) break;
                    if (avail == 0) break;
                    std::vector<char> buf(avail + 1);
                    DWORD read = 0;
                    if (!WinHttpReadData(req, buf.data(), avail, &read)) break;
                    body.append(buf.data(), read);
                } while (avail > 0);

                // Parse "total_balance":"110.00" (best-effort, no JSON dep)
                size_t p = body.find("\"total_balance\":\"");
                if (p != std::string::npos) {
                    size_t q = p + 17;
                    size_t e = body.find('"', q);
                    if (e != std::string::npos) {
                        bal = atof(body.substr(q, e - q).c_str());
                        ok = true;
                    }
                }
            }
            WinHttpCloseHandle(req);
        }
        WinHttpCloseHandle(conn);
        WinHttpCloseHandle(session);
    }

done:
    g_balance = bal;
    g_balanceOk = ok;
    if (ok) AppendHistory(bal);
    Log("balance fetch ok=%d val=%.2f", ok, bal);
    if (g_hwnd) PostMessageW(g_hwnd, WM_APP_REFRESH, 0, 0);
}

static void RefreshBalanceAsync() {
    std::thread(FetchBalanceThread).detach();
}

// ---------------------------------------------------------------------------
// Window geometry persistence (position + size survive restarts)
// ---------------------------------------------------------------------------
static void LoadGeometry(int& x, int& y, int& w, int& h) {
    // Default: top-right area of the work area.
    RECT wa; SystemParametersInfoW(SPI_GETWORKAREA, 0, &wa, 0);
    x = wa.right - DEFAULT_W - 40;
    y = wa.top + 40;
    w = DEFAULT_W;
    h = DEFAULT_H;

    std::ifstream f(g_exeDir + "\\deskbox.cfg");
    if (!f.is_open()) return;
    std::string line;
    while (std::getline(f, line)) {
        if      (line.rfind("x=", 0) == 0) x = atoi(line.c_str() + 2);
        else if (line.rfind("y=", 0) == 0) y = atoi(line.c_str() + 2);
        else if (line.rfind("w=", 0) == 0) w = atoi(line.c_str() + 2);
        else if (line.rfind("h=", 0) == 0) h = atoi(line.c_str() + 2);
        else if (line.rfind("font=", 0) == 0) g_fontSize = atoi(line.c_str() + 5);
    }
    if (w < 80) w = 80;   // sane minimums
    if (h < 48) h = 48;
    if (g_fontSize < 12) g_fontSize = 12;
    if (g_fontSize > 60) g_fontSize = 60;
}

static void SaveGeometry(HWND hwnd) {
    RECT rc; GetWindowRect(hwnd, &rc);
    std::ofstream f(g_exeDir + "\\deskbox.cfg");
    if (!f.is_open()) return;
    f << "x=" << rc.left << "\n";
    f << "y=" << rc.top  << "\n";
    f << "w=" << (rc.right  - rc.left) << "\n";
    f << "h=" << (rc.bottom - rc.top)  << "\n";
    f << "font=" << g_fontSize << "\n";
}

static void SetFontSize(int size) {
    if (size < 12) size = 12;
    if (size > 60) size = 60;
    g_fontSize = size;
    CreateTextFormats();
    Log("font size -> %d", size);
}

// ---------------------------------------------------------------------------
// Window
// ---------------------------------------------------------------------------
static void PinToDesktop(HWND hwnd); // forward decl (used by the pin watchdog)

static LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_NCHITTEST: {
        // Resize from the bottom/right edges + corner; drag anywhere else.
        POINT p = { (short)LOWORD(lp), (short)HIWORD(lp) };
        ScreenToClient(hwnd, &p);
        RECT rc; GetClientRect(hwnd, &rc);
        const int edge = 10;
        bool right  = p.x >= rc.right  - edge;
        bool bottom = p.y >= rc.bottom - edge;
        if (right && bottom) return HTBOTTOMRIGHT;
        if (right)  return HTRIGHT;
        if (bottom) return HTBOTTOM;
        return HTCAPTION;
    }
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE; // don't steal focus from the user's app
    case WM_SYSCOMMAND:
        // Refuse to minimize: Win+D ("Show Desktop") and "Minimize All" send
        // SC_MINIMIZE; this box must stay visible on the desktop layer.
        if ((wp & 0xFFF0) == SC_MINIMIZE)
            return 0;
        break;
    case WM_WINDOWPOSCHANGING: {
        // Some "show desktop" paths hide windows via SetWindowPos(SWP_HIDEWINDOW)
        // rather than SC_MINIMIZE. Strip that flag so the box is never hidden.
        WINDOWPOS* pos = reinterpret_cast<WINDOWPOS*>(lp);
        pos->flags &= ~SWP_HIDEWINDOW;
        return 0;
    }
    case WM_NCRBUTTONUP: {
        // Right-click context menu (settings). WM_NCHITTEST returns HTCAPTION for
        // the body, so the right-click arrives as a non-client message — handle
        // WM_NCRBUTTONUP, not WM_RBUTTONUP.
        POINT pt; GetCursorPos(&pt);
        HMENU menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, 1, L"字号 +");
        AppendMenuW(menu, MF_STRING, 2, L"字号 -");
        AppendMenuW(menu, MF_STRING, 3, L"重置字号");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, 4, L"立即刷新");
        AppendMenuW(menu, MF_STRING, 5, L"退出");
        SetForegroundWindow(hwnd);
        int cmd = (int)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                      pt.x, pt.y, 0, hwnd, nullptr);
        PostMessageW(hwnd, WM_NULL, 0, 0); // help the menu dismiss on a non-activating window
        DestroyMenu(menu);
        switch (cmd) {
        case 1: SetFontSize(g_fontSize + 2); SaveGeometry(hwnd); Render(); break;
        case 2: SetFontSize(g_fontSize - 2); SaveGeometry(hwnd); Render(); break;
        case 3: SetFontSize(26);             SaveGeometry(hwnd); Render(); break;
        case 4: RefreshBalanceAsync(); break;
        case 5: DestroyWindow(hwnd); break;
        }
        return 0;
    }
    case WM_ERASEBKGND:
        return 1; // Direct2D paints everything
    case WM_SIZE:
        if (g_rt && LOWORD(lp) > 0 && HIWORD(lp) > 0) {
            g_rt->Resize(D2D1::SizeU(LOWORD(lp), HIWORD(lp)));
            Render();
        }
        return 0;
    case WM_EXITSIZEMOVE:
        SaveGeometry(hwnd); // remember position + size after move/resize
        return 0;
    case WM_PAINT:
    case WM_DISPLAYCHANGE:
        Render();
        ValidateRect(hwnd, nullptr);
        return 0;
    case WM_APP_REFRESH:
        Render();
        return 0;
    case WM_TIMER:
        if (wp == TIMER_REFRESH) {
            RefreshBalanceAsync();
        } else if (wp == TIMER_PIN) {
            // Watchdog: keep the box on the desktop even if an Explorer restart
            // or "Show Desktop" moved/hid/minimized it.
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            PinToDesktop(hwnd);
        }
        return 0;
    case WM_DESTROY:
        SaveGeometry(hwnd);
        KillTimer(hwnd, TIMER_REFRESH);
        KillTimer(hwnd, TIMER_PIN);
        if (hwnd == g_hwnd) g_hwnd = nullptr;
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static void PinToDesktop(HWND hwnd) {
    HWND progman = FindWindowW(L"Progman", nullptr);
    if (!progman) { Log("Progman not found - floating window"); return; }
    SetWindowPos(hwnd, progman, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    // Log only when the desktop window handle actually changes (startup or an
    // Explorer restart) — the watchdog calls this every 2s, so logging every
    // call would flood the log.
    static HWND lastProgman = nullptr;
    if (progman != lastProgman) {
        lastProgman = progman;
        Log("pinned above Progman=0x%p", progman);
    }
}

// Kill any other running instance of this exe, so a fresh launch always results
// in exactly one box (no duplicate windows).
static void KillOtherInstances() {
    wchar_t selfPath[MAX_PATH];
    GetModuleFileNameW(nullptr, selfPath, MAX_PATH);
    wchar_t* selfName = wcsrchr(selfPath, L'\\');
    selfName = selfName ? selfName + 1 : selfPath;

    DWORD myPid = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    PROCESSENTRY32W pe = {};
    pe.dwSize = sizeof(pe);
    if (Process32FirstW(snap, &pe)) {
        do {
            if (pe.th32ProcessID != myPid && lstrcmpiW(pe.szExeFile, selfName) == 0) {
                HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, pe.th32ProcessID);
                if (h) {
                    TerminateProcess(h, 0);
                    CloseHandle(h);
                    Log("terminated previous instance pid=%u", pe.th32ProcessID);
                }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
}

HWND BalanceBox_Create() {
    if (g_hwnd) return g_hwnd;
    g_exeDir = WideToUtf8(ExeDir());
    LoadConfig();
    LoadHistory();

    int x, y, w, h;
    LoadGeometry(x, y, w, h);

    WNDCLASSW wc = {};
    wc.lpfnWndProc   = WndProc;
    wc.hInstance     = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"DeskBox";
    wc.hCursor       = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr; // Direct2D paints
    RegisterClassW(&wc);

    g_hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW,
        L"DeskBox", L"DeskBox",
        WS_POPUP,
        x, y, w, h,
        nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!g_hwnd) { Log("CreateWindowExW failed"); return nullptr; }

    int corner = 2; // DWMWCP_ROUND
    DwmSetWindowAttribute(g_hwnd, 33 /*DWMWA_WINDOW_CORNER*/, &corner, sizeof(corner));

    SetLayeredWindowAttributes(g_hwnd, 0, 235, LWA_ALPHA);

    if (!InitD2D()) { DestroyWindow(g_hwnd); return nullptr; }

    PinToDesktop(g_hwnd);
    ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
    UpdateWindow(g_hwnd);

    SetTimer(g_hwnd, TIMER_REFRESH, REFRESH_MS, nullptr);
    SetTimer(g_hwnd, TIMER_PIN, PIN_MS, nullptr);
    RefreshBalanceAsync(); // first fetch
    Render();

    Log("balance box created at (%d,%d) size %dx%d", x, y, w, h);
    return g_hwnd;
}

void BalanceBox_Destroy() {
    if (g_hwnd) DestroyWindow(g_hwnd); // WM_DESTROY clears g_hwnd
}
