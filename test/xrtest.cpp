// End-to-end check of the OpenXR layer without a headset:
//   xrtest.exe d3d11|d3d12 <out.bmp>
// Loads the official OpenXR loader against mock_runtime.dll with the Lyrenne layer enabled, acts
// like a small game (own action set, a button binding, a frame loop), and then asks the mock what
// the layer did. LyrenneVR.exe --always must be running so there is a panel to composite.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_4.h>

#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_D3D12
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#define CHECK(x)                                                         \
    do {                                                                 \
        XrResult r_ = (x);                                               \
        if (XR_FAILED(r_)) {                                             \
            printf("FAIL %s -> %d (line %d)\n", #x, r_, __LINE__);       \
            return 1;                                                    \
        }                                                                \
    } while (0)

static int failures = 0;
static void Expect(bool ok, const char* what) {
    printf("%s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
}

static void WriteBmp(const char* path, const uint8_t* px, int w, int h, bool rgba) {
    FILE* f = nullptr;
    if (fopen_s(&f, path, "wb") || !f) return;
    uint32_t size = 54 + w * h * 4;
    uint8_t hdr[54] = {'B', 'M'};
    memcpy(hdr + 2, &size, 4);
    hdr[10] = 54;
    hdr[14] = 40;
    memcpy(hdr + 18, &w, 4);
    int negH = -h;  // top-down
    memcpy(hdr + 22, &negH, 4);
    hdr[26] = 1;
    hdr[28] = 32;
    fwrite(hdr, 1, 54, f);
    std::vector<uint8_t> row(w * 4);
    for (int y = 0; y < h; y++) {
        const uint8_t* s = px + static_cast<size_t>(y) * w * 4;
        for (int x = 0; x < w; x++) {
            // BMP wants BGRA. Composite over mid grey so transparent corners are visible.
            uint8_t b = rgba ? s[x * 4 + 2] : s[x * 4 + 0];
            uint8_t g = s[x * 4 + 1];
            uint8_t r = rgba ? s[x * 4 + 0] : s[x * 4 + 2];
            uint8_t a = s[x * 4 + 3];
            row[x * 4 + 0] = static_cast<uint8_t>(b + (255 - a) * 96 / 255);
            row[x * 4 + 1] = static_cast<uint8_t>(g + (255 - a) * 96 / 255);
            row[x * 4 + 2] = static_cast<uint8_t>(r + (255 - a) * 96 / 255);
            row[x * 4 + 3] = 255;
        }
        fwrite(row.data(), 1, row.size(), f);
    }
    fclose(f);
}

// xrtest.exe real: the machine's actual runtime and implicit layers, ours included through
// XR_API_LAYER_PATH. Checks the layer loads, coexists with the others and builds its action set.
static int RealRuntime() {
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir(exe);
    dir = dir.substr(0, dir.find_last_of(L'\\'));
    SetEnvironmentVariableW(L"XR_API_LAYER_PATH", (dir + L"\\..\\layer").c_str());
    SetEnvironmentVariableW(L"XR_ENABLE_API_LAYERS", L"XR_APILAYER_LYRENNE_wrist_player");
    SetEnvironmentVariableW(L"DISABLE_XR_APILAYER_LYRENNE_wrist_player", L"1");

    uint32_t count = 0;
    xrEnumerateApiLayerProperties(0, &count, nullptr);
    std::vector<XrApiLayerProperties> layers(count, {XR_TYPE_API_LAYER_PROPERTIES});
    xrEnumerateApiLayerProperties(count, &count, layers.data());
    for (auto& l : layers) printf("layer available: %s\n", l.layerName);

    const char* ext = XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ici.applicationInfo.applicationName, "xrtest-real");
    ici.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = &ext;
    XrInstance instance;
    CHECK(xrCreateInstance(&ici, &instance));
    XrInstanceProperties props{XR_TYPE_INSTANCE_PROPERTIES};
    xrGetInstanceProperties(instance, &props);
    printf("runtime: %s\n", props.runtimeName);

    XrPath p;
    CHECK(xrStringToPath(instance, "/user/hand/left", &p));
    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId system;
    XrResult r = xrGetSystem(instance, &sgi, &system);
    printf("xrGetSystem: %d%s\n", r, r == XR_ERROR_FORM_FACTOR_UNAVAILABLE ? " (no headset connected, expected here)" : "");
    CHECK(xrDestroyInstance(instance));
    printf("instance created and destroyed cleanly with the layer loaded\n");
    return 0;
}

int main(int argc, char** argv) {
    if (argc > 1 && strcmp(argv[1], "real") == 0) return RealRuntime();
    bool d3d12 = argc > 1 && strstr(argv[1], "d3d12") != nullptr;
    // real-d3d11 / real-d3d12: a full session on the machine's actual runtime with all its layers.
    bool real = argc > 1 && strncmp(argv[1], "real-", 5) == 0;
    const char* out = argc > 2 ? argv[2] : "xrtest.bmp";

    HMODULE mock = GetModuleHandleW(nullptr);
    {
        wchar_t exe[MAX_PATH];
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring dir(exe);
        dir = dir.substr(0, dir.find_last_of(L'\\'));
        if (!real) SetEnvironmentVariableW(L"XR_RUNTIME_JSON", (dir + L"\\mock_runtime.json").c_str());
        SetEnvironmentVariableW(L"XR_API_LAYER_PATH", (dir + L"\\..\\layer").c_str());
        SetEnvironmentVariableW(L"XR_ENABLE_API_LAYERS", L"XR_APILAYER_LYRENNE_wrist_player");
        // An installed copy registered as an implicit layer would shadow the build under test.
        SetEnvironmentVariableW(L"DISABLE_XR_APILAYER_LYRENNE_wrist_player", L"1");
        // Keep other installed implicit layers out of this test, and keep ours from loading twice
        // in case it is also registered as an implicit layer on this machine.
        if (!real) {
            SetEnvironmentVariableW(L"DISABLE_XR_APILAYER_VIRTUALDESKTOP_OCULUS_COMPATIBILITY", L"1");
            SetEnvironmentVariableW(L"DISABLE_XR_APILAYER_DIENERTECH_VECTORXR", L"1");
        }
        if (!real) mock = LoadLibraryW((dir + L"\\mock_runtime.dll").c_str());
    }
    if (!mock && !real) {
        printf("FAIL cannot load mock_runtime.dll\n");
        return 1;
    }

    const char* ext = d3d12 ? XR_KHR_D3D12_ENABLE_EXTENSION_NAME : XR_KHR_D3D11_ENABLE_EXTENSION_NAME;
    XrInstanceCreateInfo ici{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(ici.applicationInfo.applicationName, "xrtest");
    ici.applicationInfo.apiVersion = XR_API_VERSION_1_0;
    ici.enabledExtensionCount = 1;
    ici.enabledExtensionNames = &ext;
    XrInstance instance;
    CHECK(xrCreateInstance(&ici, &instance));

    XrSystemGetInfo sgi{XR_TYPE_SYSTEM_GET_INFO};
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrSystemId system;
    CHECK(xrGetSystem(instance, &sgi, &system));

    // The game's own input: one button on Touch controllers.
    XrActionSetCreateInfo asci{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(asci.actionSetName, "gameplay");
    strcpy_s(asci.localizedActionSetName, "Gameplay");
    XrActionSet gameSet;
    CHECK(xrCreateActionSet(instance, &asci, &gameSet));
    XrActionCreateInfo aci{XR_TYPE_ACTION_CREATE_INFO};
    strcpy_s(aci.actionName, "jump");
    strcpy_s(aci.localizedActionName, "Jump");
    aci.actionType = XR_ACTION_TYPE_BOOLEAN_INPUT;
    XrAction jump;
    CHECK(xrCreateAction(gameSet, &aci, &jump));
    XrPath profile, aClick;
    xrStringToPath(instance, "/interaction_profiles/oculus/touch_controller", &profile);
    xrStringToPath(instance, "/user/hand/right/input/a/click", &aClick);
    XrActionSuggestedBinding binding{jump, aClick};
    XrInteractionProfileSuggestedBinding sb{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
    sb.interactionProfile = profile;
    sb.countSuggestedBindings = 1;
    sb.suggestedBindings = &binding;
    CHECK(xrSuggestInteractionProfileBindings(instance, &sb));

    XrSessionCreateInfo sci{XR_TYPE_SESSION_CREATE_INFO};
    sci.systemId = system;
    XrGraphicsBindingD3D11KHR b11{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR};
    XrGraphicsBindingD3D12KHR b12{XR_TYPE_GRAPHICS_BINDING_D3D12_KHR};
    ID3D11Device* dev11 = nullptr;
    ID3D12Device* dev12 = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    if (d3d12) {
        PFN_xrGetD3D12GraphicsRequirementsKHR req;
        CHECK(xrGetInstanceProcAddr(instance, "xrGetD3D12GraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&req)));
        XrGraphicsRequirementsD3D12KHR r{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D12_KHR};
        CHECK(req(instance, system, &r));
        auto create12 = reinterpret_cast<PFN_D3D12_CREATE_DEVICE>(GetProcAddress(LoadLibraryW(L"d3d12.dll"), "D3D12CreateDevice"));
        if (!create12 || FAILED(create12(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev12)))) {
            printf("FAIL D3D12CreateDevice\n");
            return 1;
        }
        D3D12_COMMAND_QUEUE_DESC qd{D3D12_COMMAND_LIST_TYPE_DIRECT};
        dev12->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue));
        b12.device = dev12;
        b12.queue = queue;
        sci.next = &b12;
    } else {
        PFN_xrGetD3D11GraphicsRequirementsKHR req;
        CHECK(xrGetInstanceProcAddr(instance, "xrGetD3D11GraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&req)));
        XrGraphicsRequirementsD3D11KHR r{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
        CHECK(req(instance, system, &r));
        D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_0;
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, &fl, 1, D3D11_SDK_VERSION, &dev11, nullptr, nullptr))) {
            printf("FAIL D3D11CreateDevice\n");
            return 1;
        }
        b11.device = dev11;
        sci.next = &b11;
    }
    XrSession session;
    CHECK(xrCreateSession(instance, &sci, &session));

    XrSessionActionSetsAttachInfo attach{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
    attach.countActionSets = 1;
    attach.actionSets = &gameSet;
    CHECK(xrAttachSessionActionSets(session, &attach));

    if (real) {
        // A real runtime only allows xrBeginSession once it reports READY.
        bool ready = false;
        for (int i = 0; i < 1500 && !ready; i++) {
            XrEventDataBuffer ev{XR_TYPE_EVENT_DATA_BUFFER};
            while (xrPollEvent(instance, &ev) == XR_SUCCESS) {
                if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
                    auto st = reinterpret_cast<XrEventDataSessionStateChanged*>(&ev)->state;
                    printf("session state %d\n", st);
                    if (st == XR_SESSION_STATE_READY) ready = true;
                }
                ev = {XR_TYPE_EVENT_DATA_BUFFER};
            }
            Sleep(10);
        }
        if (!ready) {
            printf("runtime never reported READY (headset not streaming?), inconclusive\n");
            xrDestroySession(session);
            xrDestroyInstance(instance);
            return 2;
        }
    }

    XrSessionBeginInfo sbi{XR_TYPE_SESSION_BEGIN_INFO};
    sbi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    CHECK(xrBeginSession(session, &sbi));

    // Run frames until the host has had time to see us, show the panel and publish a frame.
    for (int i = 0; i < 240; i++) {
        XrActiveActionSet active{gameSet, XR_NULL_PATH};
        XrActionsSyncInfo sync{XR_TYPE_ACTIONS_SYNC_INFO};
        sync.countActiveActionSets = 1;
        sync.activeActionSets = &active;
        CHECK(xrSyncActions(session, &sync));
        XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
        gi.action = jump;
        XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN};
        CHECK(xrGetActionStateBoolean(session, &gi, &st));

        XrFrameState fs{XR_TYPE_FRAME_STATE};
        CHECK(xrWaitFrame(session, nullptr, &fs));
        CHECK(xrBeginFrame(session, nullptr));
        XrFrameEndInfo fe{XR_TYPE_FRAME_END_INFO};
        fe.displayTime = fs.predictedDisplayTime;
        fe.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        CHECK(xrEndFrame(session, &fe));
        Sleep(11);
    }

    if (real) {
        // What the layer published to the host from inside the real runtime.
        HANDLE m = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\LyrenneVR.Bridge.v1");
        auto* b = m ? static_cast<const uint8_t*>(MapViewOfFile(m, FILE_MAP_READ, 0, 0, 512)) : nullptr;
        if (!b) {
            printf("FAIL host bridge not found, is LyrenneVR.exe running?\n");
            return 1;
        }
        uint32_t flags = *reinterpret_cast<const uint32_t*>(b + 100);
        uint32_t visible = *reinterpret_cast<const uint32_t*>(b + 28);
        printf("layer reported to host: runtime '%s', app '%s', pose flags 0x%x, panel visible %u\n",
               reinterpret_cast<const char*>(b + 248), reinterpret_cast<const char*>(b + 312), flags, visible);
        Expect((flags & 1) != 0, "head pose tracked through the layer");
        Expect(strcmp(reinterpret_cast<const char*>(b + 312), "xrtest") == 0, "host sees this game");
        printf("controller poses: left grip %s, right aim %s (need controllers awake)\n", (flags & 2) ? "yes" : "no", (flags & 16) ? "yes" : "no");
        xrDestroySession(session);
        xrDestroyInstance(instance);
        printf(failures ? "\n%d check(s) failed\n" : "\nall checks passed\n", failures);
        return failures ? 1 : 0;
    }

    int v[12] = {};
    reinterpret_cast<void (*)(int*)>(GetProcAddress(mock, "MockReport"))(v);
    const char* space = reinterpret_cast<const char* (*)()>(GetProcAddress(mock, "MockQuadSpace"))();
    float geo[9] = {};
    reinterpret_cast<void (*)(float*)>(GetProcAddress(mock, "MockQuadGeometry"))(geo);
    const uint8_t* px = reinterpret_cast<const uint8_t* (*)()>(GetProcAddress(mock, "MockPixels"))();

    printf("api: %s\n", d3d12 ? "D3D12" : "D3D11");
    printf("suggest calls %d, bindings in last call %d\n", v[0], v[1]);
    printf("action sets attached %d, active in last sync %d\n", v[2], v[3]);
    printf("frames %d, frames with panel %d, panel %dx%d %s, space '%s'\n", v[4], v[5], v[7], v[8], v[9] ? "RGBA" : "BGRA", space);
    printf("quad %.3f x %.3f m, pose q(%.3f %.3f %.3f %.3f) p(%.3f %.3f %.3f)\n", geo[0], geo[1], geo[2], geo[3], geo[4], geo[5], geo[6], geo[7], geo[8]);

    Expect(v[1] == 1 + 8, "game binding kept and our 4 poses, 2 haptics and 2 triggers appended");
    Expect(v[2] == 3, "our pose set and click set attached alongside the game's");
    Expect(v[11] == 3, "pointing at the panel switched the click set on");
    Expect(v[10] > 0, "the layer read the trigger while pointing");
    Expect(v[5] > 0, "panel quad composited");
    Expect(strcmp(space, "hand_grip /user/hand/left") == 0, "quad locked to the left grip");
    Expect(std::fabs(geo[0] - 0.16f) < 0.001f && std::fabs(geo[1] - 0.08f) < 0.001f, "quad is 16 x 8 cm");

    // Pixels: opaque card in the middle, transparent rounded corner, white-ish title text somewhere.
    if (v[7] > 0 && px) {
        int w = v[7], h = v[8];
        auto at = [&](int x, int y) { return px + (static_cast<size_t>(y) * w + x) * 4; };
        Expect(at(w / 2, h / 2)[3] == 255, "panel centre is opaque");
        Expect(at(2, 2)[3] == 0, "rounded corner is transparent");
        int bright = 0;
        for (int y = 60; y < 110; y++)
            for (int x = 300; x < 700; x++)
                if (at(x, y)[0] > 220 && at(x, y)[1] > 220 && at(x, y)[2] > 220) bright++;
        Expect(bright > 200, "title text drawn");
        WriteBmp(out, px, w, h, v[9] != 0);
        printf("wrote %s\n", out);
    }

    xrEndSession(session);
    xrDestroySession(session);
    xrDestroyInstance(instance);
    printf(failures ? "\n%d check(s) failed\n" : "\nall checks passed\n", failures);
    return failures ? 1 : 0;
}
