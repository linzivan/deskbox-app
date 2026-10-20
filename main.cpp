// =============================================================================
// DeskBoxApp - unified desktop boxes host.
//
// One process, one message loop, multiple desktop boxes (windows):
//   - balance  : DeepSeek balance + today/month consumption + 24h trend
//   - shortcut : keyboard shortcut cheat sheet (add / edit / delete)
//
// Management: tray icon -> toggle each box on/off, autostart, quit.
// Each box keeps its own data/config files and its own geometry persistence.
// Adding a new box = add a box_xxx.cpp exposing XxxBox_Create()/_Destroy(),
// then register it in the BoxKind table below.
// =============================================================================
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <cstdio>
#include <cstring>
#include <cstdarg>
#include <cwchar>
#include <string>
#include <vector>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")

// --- box module interface ----------------------------------------------------
HWND BalanceBox_Create();
void BalanceBox_Destroy();
HWND ShortcutBox_Create();
void ShortcutBox_Destroy();

// --- box registry ------------------------------------------------------------
struct BoxKind {
    const wchar_t* name;   // menu label
    const char*    cfgKey; // key in boxes.cfg
    HWND (*create)();
    void (*destroy)();
};

static const BoxKind g_kinds[] = {
    { L"余额盒子",   "balance",  BalanceBox_Create,  BalanceBox_Destroy  },
    { L"快捷键盒子", "shortcut", ShortcutBox_Create, ShortcutBox_Destroy },
};
static const int BOX_COUNT = (int)(sizeof(g_kinds) / sizeof(g_kinds[0]));

static std::string  g_exeDir;
static std::wstring g_exeDirW;
static bool         g_enabled[BOX_COUNT];
static bool         g_alive[BOX_COUNT];

static const UINT WM_TRAY  = WM_APP + 1;
static const UINT TRAY_ID  = 1;
static const wchar_t* RUN_KEY = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
static const wchar_t* RUN_VAL = L"DeskBoxApp";

static HWND g_trayHwnd = nullptr;
static HICON g_trayIcon = nullptr;

// ---------------------------------------------------------------------------
// logging
// ---------------------------------------------------------------------------
static void Log(const char* fmt, ...) {
    if (g_exeDir.empty()) return;
    std::string file = g_exeDir + "\\app.log";
    FILE* f = fopen(file.c_str(), "a");
    if (!f) return;
    SYSTEMTIME st; GetLocalTime(&st);
    fprintf(f, "%04d-%02d-%02d %02d:%02d:%02d  ", st.wYear, st.wMonth, st.wDay,
            st.wHour, st.wMinute, st.wSecond);
    va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap);
    fprintf(f, "\n");
    fclose(f);
}

// ---------------------------------------------------------------------------
// paths / config
// ---------------------------------------------------------------------------
static std::wstring ExeDirW() {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    wchar_t* p = wcsrchr(path, L'\\');
    if (p) *p = 0;
    return path;
}

static std::string WideToUtf8(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, &s[0], n, nullptr, nullptr);
    return s;
}

static void LoadConfig() {
    for (int i = 0; i < BOX_COUNT; i++) g_enabled[i] = true;
    std::string file = g_exeDir + "\\boxes.cfg";
    FILE* f = fopen(file.c_str(), "r");
    if (!f) return;
    char line[256];
    while (fgets(line, sizeof(line), f)) {
        char* eq = strchr(line, '=');
        if (!eq) continue;
        *eq = 0;
        std::string key(line);
        while (!key.empty() && (key.back() == '\r' || key.back() == '\n' || key.back() == ' ')) key.pop_back();
        int val = atoi(eq + 1);
        for (int i = 0; i < BOX_COUNT; i++)
            if (key == g_kinds[i].cfgKey) g_enabled[i] = (val != 0);
    }
    fclose(f);
}

static void SaveConfig() {
    std::string file = g_exeDir + "\\boxes.cfg";
    FILE* f = fopen(file.c_str(), "w");
    if (!f) return;
    for (int i = 0; i < BOX_COUNT; i++)
        fprintf(f, "%s=%d\n", g_kinds[i].cfgKey, g_enabled[i] ? 1 : 0);
    fclose(f);
}

// ---------------------------------------------------------------------------
// autostart
// ---------------------------------------------------------------------------
static bool IsAutoStart() {
    HKEY k;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, RUN_KEY, 0, KEY_READ, &k) != ERROR_SUCCESS) return false;
    bool exists = (RegQueryValueExW(k, RUN_VAL, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS);
    RegCloseKey(k);
    return exists;
}

static void SetAutoStart(bool on) {
    HKEY k;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, RUN_KEY, 0, KEY_SET_VALUE, &k) != ERROR_SUCCESS) {
        Log("autostart: open key failed");
        return;
    }
    if (on) {
        std::wstring exe = g_exeDirW + L"\\DeskBoxApp.exe";
        LONG r = RegSetValueExW(k, RUN_VAL, 0, REG_SZ, (const BYTE*)exe.c_str(),
                                (DWORD)((exe.size() + 1) * sizeof(wchar_t)));
        Log("autostart ON -> %s (%s)", WideToUtf8(exe).c_str(), r == ERROR_SUCCESS ? "ok" : "fail");
    } else {
        RegDeleteValueW(k, RUN_VAL);
        Log("autostart OFF");
    }
    RegCloseKey(k);
}

// ---------------------------------------------------------------------------
// box lifecycle (idempotent)
// ---------------------------------------------------------------------------
static void SyncBoxes() {
    for (int i = 0; i < BOX_COUNT; i++) {
        if (g_enabled[i] && !g_alive[i]) {
            g_alive[i] = (g_kinds[i].create() != nullptr);
            Log("box %s -> created (%s)", g_kinds[i].cfgKey, g_alive[i] ? "ok" : "FAILED");
        } else if (!g_enabled[i] && g_alive[i]) {
            g_kinds[i].destroy();
            g_alive[i] = false;
            Log("box %s -> destroyed", g_kinds[i].cfgKey);
        }
    }
}

// ---------------------------------------------------------------------------
// single instance
// ---------------------------------------------------------------------------
static void KillOtherInstances() {
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    std::wstring name = self;
    size_t p = name.find_last_of(L'\\');
    if (p != std::wstring::npos) name = name.substr(p + 1);

    DWORD me = GetCurrentProcessId();
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    PROCESSENTRY32W pe = { sizeof(pe) };
    if (Process32FirstW(snap, &pe)) {
        do {
            if (pe.th32ProcessID != me && _wcsicmp(pe.szExeFile, name.c_str()) == 0) {
                HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, pe.th32ProcessID);
                if (h) { TerminateProcess(h, 0); CloseHandle(h); }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
}

// ---------------------------------------------------------------------------
// tray
// ---------------------------------------------------------------------------
static void ShowTrayMenu() {
    HMENU menu = CreatePopupMenu();
    for (int i = 0; i < BOX_COUNT; i++)
        AppendMenuW(menu, MF_STRING | (g_enabled[i] ? MF_CHECKED : 0),
                    3000 + i, g_kinds[i].name);
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING | (IsAutoStart() ? MF_CHECKED : 0), 3100, L"开机自启");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, 3101, L"退出");

    POINT pt; GetCursorPos(&pt);
    SetForegroundWindow(g_trayHwnd);
    int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                             pt.x, pt.y, 0, g_trayHwnd, nullptr);
    DestroyMenu(menu);

    if (cmd == 0) return;
    if (cmd >= 3000 && cmd < 3000 + BOX_COUNT) {
        int i = cmd - 3000;
        g_enabled[i] = !g_enabled[i];
        SyncBoxes();
        SaveConfig();
    } else if (cmd == 3100) {
        SetAutoStart(!IsAutoStart());
    } else if (cmd == 3101) {
        PostQuitMessage(0);
    }
}

static LRESULT CALLBACK TrayWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    if (msg == WM_TRAY) {
        UINT ev = (UINT)LOWORD(lp);
        if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU || ev == WM_LBUTTONUP) {
            ShowTrayMenu();
            return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

static void AddTrayIcon() {
    NOTIFYICONDATAW nid = {};
    nid.cbSize = sizeof(nid);
    nid.hWnd = g_trayHwnd;
    nid.uID = TRAY_ID;
    nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    nid.uCallbackMessage = WM_TRAY;

    // custom blue DeskBox icon from beside the exe, else the system default
    std::wstring icoPath = g_exeDirW + L"\\deskbox.ico";
    int cx = GetSystemMetrics(SM_CXSMICON);
    int cy = GetSystemMetrics(SM_CYSMICON);
    g_trayIcon = (HICON)LoadImageW(nullptr, icoPath.c_str(), IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
    if (!g_trayIcon) {
        g_trayIcon = (HICON)LoadImageW(nullptr, icoPath.c_str(), IMAGE_ICON, 0, 0,
                                       LR_LOADFROMFILE | LR_DEFAULTSIZE);
        Log("tray icon: fell back to ico file size 0/0 (LoadImage error %lu)", GetLastError());
    }
    if (!g_trayIcon) {
        g_trayIcon = LoadIconW(nullptr, IDI_APPLICATION);
        Log("tray icon: deskbox.ico not found, using system icon");
    }
    nid.hIcon = g_trayIcon;
    wcscpy_s(nid.szTip, L"桌面盒子");
    BOOL ok = Shell_NotifyIconW(NIM_ADD, &nid);
    Log("tray icon add -> %s (hwnd=%p, size=%dx%d)", ok ? "ok" : "FAILED", (void*)g_trayHwnd, cx, cy);
}

static void RemoveTrayIcon() {
    NOTIFYICONDATAW nid = {};
    nid.cbSize = sizeof(nid);
    nid.hWnd = g_trayHwnd;
    nid.uID = TRAY_ID;
    Shell_NotifyIconW(NIM_DELETE, &nid);
    if (g_trayIcon) { DestroyIcon(g_trayIcon); g_trayIcon = nullptr; }
}

// ---------------------------------------------------------------------------
// entry
// ---------------------------------------------------------------------------
int WINAPI WinMain(HINSTANCE hInst, HINSTANCE, LPSTR lpCmdLine, int) {
    g_exeDirW = ExeDirW();
    g_exeDir = WideToUtf8(g_exeDirW);

    // ---- self test: create -> destroy -> recreate -> destroy for every box ----
    if (lpCmdLine && strstr(lpCmdLine, "--selftest")) {
        Log("=== SELFTEST start ===");
        for (int i = 0; i < BOX_COUNT; i++) {
            HWND h1 = g_kinds[i].create();
            Log("selftest %s create#1 = %p", g_kinds[i].cfgKey, (void*)h1);
            g_kinds[i].destroy();
            Log("selftest %s destroy#1 done", g_kinds[i].cfgKey);
            HWND h2 = g_kinds[i].create();
            Log("selftest %s create#2 = %p", g_kinds[i].cfgKey, (void*)h2);
            g_kinds[i].destroy();
            Log("selftest %s destroy#2 done", g_kinds[i].cfgKey);
        }
        Log("=== SELFTEST end ===");
        return 0;
    }

    Log("=== DeskBoxApp start ===");

    KillOtherInstances();
    LoadConfig();

    // hidden tray host window
    WNDCLASSW wc = {};
    wc.lpfnWndProc = TrayWndProc;
    wc.hInstance = hInst;
    wc.lpszClassName = L"DeskBoxTrayHost";
    RegisterClassW(&wc);
    g_trayHwnd = CreateWindowExW(0, L"DeskBoxTrayHost", L"", WS_POPUP,
                                 0, 0, 0, 0, nullptr, nullptr, hInst, nullptr);
    AddTrayIcon();

    SyncBoxes();

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    RemoveTrayIcon();
    for (int i = 0; i < BOX_COUNT; i++)
        if (g_alive[i]) g_kinds[i].destroy();

    Log("=== DeskBoxApp exit ===");
    return 0;
}
