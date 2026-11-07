// XR_APILAYER_LYRENNE_wrist_player
//
// An implicit OpenXR API layer that puts the Lyrenne wrist player into any OpenXR game, on any
// runtime (Virtual Desktop's VDXR, Oculus, WMR, Pimax...). It does three things:
//   1. Rides its own action set along with the game's so it can track both controllers' poses.
//      Only pose and haptic actions, so it never steals a button from the game.
//   2. Publishes head and hand poses to LyrenneVR.exe through shared memory.
//   3. Composites the panel the host renders as a quad layer locked to the wrist.
// Under SteamVR it stays out of the way: the host draws there with a regular OpenVR overlay.
// When the host is not running the layer costs one GetTickCount64 per frame.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3d12.h>

#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#define XR_USE_GRAPHICS_API_D3D12
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <openxr/openxr_loader_negotiation.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "bridge.h"

#define LAYER_NAME "XR_APILAYER_LYRENNE_wrist_player"

namespace {

// ---------------------------------------------------------------------------------------------
// Logging to %LOCALAPPDATA%\LyrenneVR\layer.log

std::mutex gLogLock;

void Log(const char* fmt, ...) {
    std::lock_guard<std::mutex> lock(gLogLock);
    static FILE* file = nullptr;
    static bool tried = false;
    if (!tried) {
        tried = true;
        char base[MAX_PATH];
        DWORD n = GetEnvironmentVariableA("LOCALAPPDATA", base, MAX_PATH);
        if (n > 0 && n < MAX_PATH) {
            std::string dir = std::string(base) + "\\LyrenneVR";
            CreateDirectoryA(dir.c_str(), nullptr);
            std::string path = dir + "\\layer.log";
            // Keep the log small: start over once it passes 1 MB.
            WIN32_FILE_ATTRIBUTE_DATA attr{};
            bool big = GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &attr) && attr.nFileSizeLow > (1u << 20);
            file = _fsopen(path.c_str(), big ? "w" : "a", _SH_DENYNO);
        }
    }
    if (!file) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(file, "%02d:%02d:%02d.%03d [%lu] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId());
    va_list args;
    va_start(args, fmt);
    vfprintf(file, fmt, args);
    va_end(args);
    fputc('\n', file);
    fflush(file);
}

// ---------------------------------------------------------------------------------------------
// Next-in-chain dispatch

struct Dispatch {
    PFN_xrGetInstanceProcAddr GetInstanceProcAddr = nullptr;
    PFN_xrDestroyInstance DestroyInstance = nullptr;
    PFN_xrGetInstanceProperties GetInstanceProperties = nullptr;
    PFN_xrGetSystemProperties GetSystemProperties = nullptr;
    PFN_xrStringToPath StringToPath = nullptr;
    PFN_xrCreateActionSet CreateActionSet = nullptr;
    PFN_xrDestroyActionSet DestroyActionSet = nullptr;
    PFN_xrCreateAction CreateAction = nullptr;
    PFN_xrPathToString PathToString = nullptr;
    PFN_xrGetActionStateBoolean GetActionStateBoolean = nullptr;
    PFN_xrSuggestInteractionProfileBindings SuggestInteractionProfileBindings = nullptr;
    PFN_xrAttachSessionActionSets AttachSessionActionSets = nullptr;
    PFN_xrSyncActions SyncActions = nullptr;
    PFN_xrGetActionStateFloat GetActionStateFloat = nullptr;
    PFN_xrApplyHapticFeedback ApplyHapticFeedback = nullptr;
    PFN_xrCreateSession CreateSession = nullptr;
    PFN_xrDestroySession DestroySession = nullptr;
    PFN_xrCreateReferenceSpace CreateReferenceSpace = nullptr;
    PFN_xrCreateActionSpace CreateActionSpace = nullptr;
    PFN_xrLocateSpace LocateSpace = nullptr;
    PFN_xrDestroySpace DestroySpace = nullptr;
    PFN_xrEnumerateSwapchainFormats EnumerateSwapchainFormats = nullptr;
    PFN_xrCreateSwapchain CreateSwapchain = nullptr;
    PFN_xrDestroySwapchain DestroySwapchain = nullptr;
    PFN_xrEnumerateSwapchainImages EnumerateSwapchainImages = nullptr;
    PFN_xrAcquireSwapchainImage AcquireSwapchainImage = nullptr;
    PFN_xrWaitSwapchainImage WaitSwapchainImage = nullptr;
    PFN_xrReleaseSwapchainImage ReleaseSwapchainImage = nullptr;
    PFN_xrEndFrame EndFrame = nullptr;
};

Dispatch next;

// ---------------------------------------------------------------------------------------------
// Instance and session state. OpenXR apps create one instance and one session at a time.

enum class Gfx { None, D3D11, D3D12 };

// One swapchain the layer composites as a quad, with what it takes to upload pixels into it.
struct Tex {
    XrSwapchain swapchain = XR_NULL_HANDLE;
    uint32_t width = 0, height = 0;
    bool rgba = false;  // swapchain is RGBA, the pixels are BGRA
    std::vector<ID3D11Texture2D*> images11;
    std::vector<ID3D12Resource*> images12;
    ID3D12Resource* upload = nullptr;
    uint8_t* uploadPtr = nullptr;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    bool hasContent = false;
    uint32_t uploadedSeq = 0xFFFFFFFF;
};

struct State {
    XrInstance instance = XR_NULL_HANDLE;
    bool enabled = false;  // false under SteamVR or when anything essential failed
    std::string runtimeName;
    std::string appName;

    XrPath leftHand = XR_NULL_PATH, rightHand = XR_NULL_PATH;
    XrPath leftGripPath = XR_NULL_PATH, rightGripPath = XR_NULL_PATH;
    XrPath leftAimPath = XR_NULL_PATH, rightAimPath = XR_NULL_PATH;
    XrPath leftHapticPath = XR_NULL_PATH, rightHapticPath = XR_NULL_PATH;
    XrActionSet actionSet = XR_NULL_HANDLE;
    XrAction gripAction = XR_NULL_HANDLE, aimAction = XR_NULL_HANDLE, hapticAction = XR_NULL_HANDLE;
    bool bindingsAccepted = false;
    // Second set, high priority, active only while the user points at the panel: while it is
    // active the runtime routes the trigger to us instead of the game.
    XrActionSet captureSet = XR_NULL_HANDLE;
    XrAction clickAction = XR_NULL_HANDLE;
    // Holds the trigger's touch sensor in the same set, purely so the game does not see a finger
    // resting on the trigger while the panel has it (Hard Bullet binds one, 'triggertouched').
    XrAction touchAction = XR_NULL_HANDLE;
    XrPath leftTouchPath = XR_NULL_PATH, rightTouchPath = XR_NULL_PATH;
    XrPath leftTriggerPath = XR_NULL_PATH, rightTriggerPath = XR_NULL_PATH;
    XrPath leftSelectPath = XR_NULL_PATH, rightSelectPath = XR_NULL_PATH;
    volatile bool captureOn = false;

    // The game's own actions that sit on a trigger, so the log can show whether the runtime
    // really takes them away from the game while the panel holds the trigger.
    struct GameAction {
        std::string name;
        XrActionType type;
        bool left = false, right = false;  // bound on that hand
        bool pull = false;                 // to a value or click, not just a touch sensor
    };
    std::map<XrAction, GameAction> gameActions;     // every action the game created
    std::map<XrAction, GameAction> gameTriggers;    // the ones bound to a trigger or select
    int captureFrames = 0;
    // Seen in Hard Bullet on VDXR: the click set took the trigger from the game, yet its own action
    // read inactive, so the trigger reached nobody. When that happens the layer stops taking the
    // trigger for the rest of the session and reads the game's own trigger actions instead.
    bool clickSeen = false;
    bool captureBroken = false;

    XrSession session = XR_NULL_HANDLE;
    bool attached = false;
    uint32_t maxLayers = 16;
    XrSpace localSpace = XR_NULL_HANDLE, viewSpace = XR_NULL_HANDLE;
    XrSpace leftGrip = XR_NULL_HANDLE, rightGrip = XR_NULL_HANDLE;
    XrSpace leftAim = XR_NULL_HANDLE, rightAim = XR_NULL_HANDLE;

    Gfx gfx = Gfx::None;
    ID3D11Device* d3d11 = nullptr;
    ID3D11DeviceContext* d3d11Context = nullptr;
    ID3D12Device* d3d12 = nullptr;
    ID3D12CommandQueue* d3d12Queue = nullptr;
    ID3D12CommandAllocator* d3d12Allocator = nullptr;
    ID3D12GraphicsCommandList* d3d12List = nullptr;
    ID3D12Fence* d3d12Fence = nullptr;
    HANDLE d3d12FenceEvent = nullptr;
    uint64_t d3d12FenceValue = 0;

    Tex panel;   // the player, re-uploaded only when the host draws a new frame
    Tex cursor;  // a small ring, uploaded once and only moved after that
    bool texFailed = false;

    HANDLE mapping = nullptr;
    lyrenne::Bridge* bridge = nullptr;
    ULONGLONG lastOpenAttempt = 0;
    uint32_t lastHapticSeq = 0;
    std::vector<uint8_t> pixels;
};

State gs;
std::mutex gStateLock;

bool Ok(XrResult r) { return XR_SUCCEEDED(r); }

// ---------------------------------------------------------------------------------------------
// Bridge

void CloseBridge() {
    if (gs.bridge) UnmapViewOfFile(gs.bridge);
    if (gs.mapping) CloseHandle(gs.mapping);
    gs.bridge = nullptr;
    gs.mapping = nullptr;
}

// Returns the bridge only when the host is alive. Retries opening at most once a second.
lyrenne::Bridge* LiveBridge() {
    ULONGLONG now = GetTickCount64();
    if (!gs.bridge) {
        if (now - gs.lastOpenAttempt < 1000) return nullptr;
        gs.lastOpenAttempt = now;
        HANDLE m = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, lyrenne::kBridgeName);
        if (!m) return nullptr;
        void* view = MapViewOfFile(m, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, lyrenne::kBridgeSize);
        if (!view) {
            CloseHandle(m);
            return nullptr;
        }
        auto* b = static_cast<lyrenne::Bridge*>(view);
        if (b->magic != lyrenne::kBridgeMagic || b->version != lyrenne::kBridgeVersion) {
            Log("bridge has magic %08x version %u, ignoring", b->magic, b->version);
            UnmapViewOfFile(view);
            CloseHandle(m);
            return nullptr;
        }
        gs.mapping = m;
        gs.bridge = b;
        Log("connected to LyrenneVR host");
    }
    // A host that stopped ticking for 3 s is gone. Let go so a restarted host gets a fresh mapping.
    int64_t beat = *reinterpret_cast<volatile int64_t*>(&gs.bridge->hostHeartbeatMs);
    if (static_cast<int64_t>(now) - beat > 3000) {
        Log("host went quiet, disconnecting");
        CloseBridge();
        gs.lastOpenAttempt = now;
        return nullptr;
    }
    return gs.bridge;
}

void CopyName(char (&dst)[64], const std::string& src) {
    size_t n = std::min(src.size(), sizeof(dst) - 1);
    memcpy(dst, src.data(), n);
    dst[n] = 0;
}

// ---------------------------------------------------------------------------------------------
// Graphics

template <typename T>
void SafeRelease(T*& p);

void ReleaseTex(Tex& t) {
    if (t.swapchain != XR_NULL_HANDLE) next.DestroySwapchain(t.swapchain);
    if (t.upload && t.uploadPtr) t.upload->Unmap(0, nullptr);
    SafeRelease(t.upload);
    t = Tex{};
}

template <typename T>
void SafeRelease(T*& p) {
    if (p) p->Release();
    p = nullptr;
}

void ReleaseGraphics() {
    if (gs.d3d12Fence && gs.d3d12Queue && gs.d3d12FenceEvent && gs.d3d12Fence->GetCompletedValue() < gs.d3d12FenceValue) {
        gs.d3d12Fence->SetEventOnCompletion(gs.d3d12FenceValue, gs.d3d12FenceEvent);
        WaitForSingleObject(gs.d3d12FenceEvent, 1000);
    }
    ReleaseTex(gs.panel);
    ReleaseTex(gs.cursor);
    SafeRelease(gs.d3d12List);
    SafeRelease(gs.d3d12Allocator);
    SafeRelease(gs.d3d12Fence);
    if (gs.d3d12FenceEvent) CloseHandle(gs.d3d12FenceEvent);
    gs.d3d12FenceEvent = nullptr;
    SafeRelease(gs.d3d12Queue);
    SafeRelease(gs.d3d12);
    SafeRelease(gs.d3d11Context);
    SafeRelease(gs.d3d11);
    gs.gfx = Gfx::None;
    gs.texFailed = false;
}

bool CreateTex(Tex& t, uint32_t width, uint32_t height) {
    uint32_t count = 0;
    if (!Ok(next.EnumerateSwapchainFormats(gs.session, 0, &count, nullptr)) || count == 0) return false;
    std::vector<int64_t> formats(count);
    next.EnumerateSwapchainFormats(gs.session, count, &count, formats.data());

    // sRGB first: the host writes sRGB-encoded bytes, so an sRGB view gives correct colors.
    const int64_t preferred[] = {DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
                                 DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R8G8B8A8_UNORM};
    int64_t chosen = -1;
    for (int64_t want : preferred) {
        if (std::find(formats.begin(), formats.end(), want) != formats.end()) {
            chosen = want;
            break;
        }
    }
    if (chosen < 0) {
        Log("no 8-bit color swapchain format available");
        return false;
    }

    XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    info.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    info.format = chosen;
    info.sampleCount = 1;
    info.width = width;
    info.height = height;
    info.faceCount = 1;
    info.arraySize = 1;
    info.mipCount = 1;
    XrResult r = next.CreateSwapchain(gs.session, &info, &t.swapchain);
    if (!Ok(r)) {
        Log("xrCreateSwapchain failed: %d", r);
        t.swapchain = XR_NULL_HANDLE;
        return false;
    }

    uint32_t imageCount = 0;
    next.EnumerateSwapchainImages(t.swapchain, 0, &imageCount, nullptr);
    if (gs.gfx == Gfx::D3D11) {
        std::vector<XrSwapchainImageD3D11KHR> images(imageCount, {XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
        next.EnumerateSwapchainImages(t.swapchain, imageCount, &imageCount,
                                      reinterpret_cast<XrSwapchainImageBaseHeader*>(images.data()));
        for (auto& i : images) t.images11.push_back(i.texture);
    } else {
        std::vector<XrSwapchainImageD3D12KHR> images(imageCount, {XR_TYPE_SWAPCHAIN_IMAGE_D3D12_KHR});
        next.EnumerateSwapchainImages(t.swapchain, imageCount, &imageCount,
                                      reinterpret_cast<XrSwapchainImageBaseHeader*>(images.data()));
        for (auto& i : images) t.images12.push_back(i.texture);

        // Upload buffer laid out for a buffer-to-texture copy into these images.
        D3D12_RESOURCE_DESC desc = t.images12[0]->GetDesc();
        UINT64 total = 0;
        gs.d3d12->GetCopyableFootprints(&desc, 0, 1, 0, &t.footprint, nullptr, nullptr, &total);
        D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_UPLOAD};
        D3D12_RESOURCE_DESC buf{};
        buf.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        buf.Width = total;
        buf.Height = 1;
        buf.DepthOrArraySize = 1;
        buf.MipLevels = 1;
        buf.SampleDesc.Count = 1;
        buf.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        if (FAILED(gs.d3d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buf, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                                     IID_PPV_ARGS(&t.upload))) ||
            FAILED(t.upload->Map(0, nullptr, reinterpret_cast<void**>(&t.uploadPtr)))) {
            Log("D3D12 upload buffer failed");
            ReleaseTex(t);
            return false;
        }
    }
    t.width = width;
    t.height = height;
    t.rgba = chosen == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || chosen == DXGI_FORMAT_R8G8B8A8_UNORM;
    Log("swapchain %ux%u format %lld, %u images", width, height, chosen, imageCount);
    return true;
}

bool InitD3D12Commands() {
    D3D12_COMMAND_LIST_TYPE type = gs.d3d12Queue->GetDesc().Type;
    if (FAILED(gs.d3d12->CreateCommandAllocator(type, IID_PPV_ARGS(&gs.d3d12Allocator))) ||
        FAILED(gs.d3d12->CreateCommandList(0, type, gs.d3d12Allocator, nullptr, IID_PPV_ARGS(&gs.d3d12List))) ||
        FAILED(gs.d3d12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&gs.d3d12Fence)))) {
        return false;
    }
    gs.d3d12List->Close();
    gs.d3d12FenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    return gs.d3d12FenceEvent != nullptr;
}

void SwizzleRows(const uint8_t* src, uint8_t* dst, uint32_t width, uint32_t height, uint32_t dstPitch, bool rgba) {
    const uint32_t rowBytes = width * 4;
    for (uint32_t y = 0; y < height; y++) {
        const uint8_t* s = src + static_cast<size_t>(y) * rowBytes;
        uint8_t* d = dst + static_cast<size_t>(y) * dstPitch;
        if (!rgba) {
            memcpy(d, s, rowBytes);
            continue;
        }
        for (uint32_t x = 0; x < width; x++) {
            d[x * 4 + 0] = s[x * 4 + 2];
            d[x * 4 + 1] = s[x * 4 + 1];
            d[x * 4 + 2] = s[x * 4 + 0];
            d[x * 4 + 3] = s[x * 4 + 3];
        }
    }
}

// Uploads tightly packed BGRA pixels into the next image of a swapchain.
bool Upload(Tex& t, const uint8_t* pixels) {
    uint32_t index = 0;
    if (!Ok(next.AcquireSwapchainImage(t.swapchain, nullptr, &index))) return false;
    XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
    wait.timeout = 100000000;  // 100 ms
    if (!Ok(next.WaitSwapchainImage(t.swapchain, &wait))) {
        next.ReleaseSwapchainImage(t.swapchain, nullptr);
        return false;
    }

    bool uploaded = false;
    size_t bytes = static_cast<size_t>(t.width) * t.height * 4;
    if (gs.gfx == Gfx::D3D11) {
        const uint8_t* src = pixels;
        std::vector<uint8_t> swizzled;
        if (t.rgba) {
            swizzled.resize(bytes);
            SwizzleRows(src, swizzled.data(), t.width, t.height, t.width * 4, true);
            src = swizzled.data();
        }
        gs.d3d11Context->UpdateSubresource(t.images11[index], 0, nullptr, src, t.width * 4, 0);
        uploaded = true;
    } else if (gs.gfx == Gfx::D3D12 && t.uploadPtr) {
        // The command allocator and upload buffers are reused, so the previous copy has to be done.
        if (gs.d3d12Fence->GetCompletedValue() < gs.d3d12FenceValue) {
            gs.d3d12Fence->SetEventOnCompletion(gs.d3d12FenceValue, gs.d3d12FenceEvent);
            WaitForSingleObject(gs.d3d12FenceEvent, 100);
        }
        SwizzleRows(pixels, t.uploadPtr + t.footprint.Offset, t.width, t.height, t.footprint.Footprint.RowPitch, t.rgba);

        ID3D12Resource* image = t.images12[index];
        gs.d3d12Allocator->Reset();
        gs.d3d12List->Reset(gs.d3d12Allocator, nullptr);
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = image;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
        gs.d3d12List->ResourceBarrier(1, &barrier);
        D3D12_TEXTURE_COPY_LOCATION dst{image, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
        dst.SubresourceIndex = 0;
        D3D12_TEXTURE_COPY_LOCATION src{t.upload, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
        src.PlacedFootprint = t.footprint;
        gs.d3d12List->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        std::swap(barrier.Transition.StateBefore, barrier.Transition.StateAfter);
        gs.d3d12List->ResourceBarrier(1, &barrier);
        gs.d3d12List->Close();
        ID3D12CommandList* lists[] = {gs.d3d12List};
        gs.d3d12Queue->ExecuteCommandLists(1, lists);
        gs.d3d12Queue->Signal(gs.d3d12Fence, ++gs.d3d12FenceValue);
        uploaded = true;
    }
    next.ReleaseSwapchainImage(t.swapchain, nullptr);
    if (uploaded) t.hasContent = true;
    return uploaded;
}

// Copies a new panel frame out of the bridge, if there is one, and uploads it.
void UploadPanelIfChanged(lyrenne::Bridge* b) {
    uint32_t seq = *reinterpret_cast<volatile uint32_t*>(&b->frameSeq);
    if (seq == gs.panel.uploadedSeq || (seq & 1)) return;
    uint32_t width = b->width, height = b->height;
    if (width == 0 || height == 0 || width > lyrenne::kMaxWidth || height > lyrenne::kMaxHeight) return;

    if (gs.panel.swapchain != XR_NULL_HANDLE && (gs.panel.width != width || gs.panel.height != height)) ReleaseTex(gs.panel);
    if (gs.panel.swapchain == XR_NULL_HANDLE) {
        if (gs.texFailed) return;
        if (!CreateTex(gs.panel, width, height)) {
            gs.texFailed = true;
            return;
        }
    }

    // Seqlock read: take the pixels, then make sure the host did not start a new frame meanwhile.
    size_t bytes = static_cast<size_t>(width) * height * 4;
    gs.pixels.resize(bytes);
    MemoryBarrier();
    memcpy(gs.pixels.data(), reinterpret_cast<uint8_t*>(b) + lyrenne::kPixelOffset, bytes);
    MemoryBarrier();
    if (*reinterpret_cast<volatile uint32_t*>(&b->frameSeq) != seq) return;
    if (Upload(gs.panel, gs.pixels.data())) gs.panel.uploadedSeq = seq;
}

// The cursor: a white ring with a dot, premultiplied BGRA, drawn once.
constexpr uint32_t kCursorSize = 64;

void EnsureCursor() {
    if (gs.cursor.hasContent || gs.texFailed) return;
    if (gs.cursor.swapchain == XR_NULL_HANDLE && !CreateTex(gs.cursor, kCursorSize, kCursorSize)) {
        gs.texFailed = true;
        return;
    }
    std::vector<uint8_t> px(kCursorSize * kCursorSize * 4);
    const float c = (kCursorSize - 1) / 2.0f;
    for (uint32_t y = 0; y < kCursorSize; y++) {
        for (uint32_t x = 0; x < kCursorSize; x++) {
            float d = std::sqrt((x - c) * (x - c) + (y - c) * (y - c));
            // Ring between radius 22 and 29 with a soft dark halo outside it, dot inside 6.
            float ring = std::clamp(1.0f - std::fabs(d - 25.5f) / 3.5f, 0.0f, 1.0f) * 1.6f;
            float dot = std::clamp(6.5f - d, 0.0f, 1.0f);
            float white = std::clamp(std::max(ring, dot), 0.0f, 1.0f);
            float shadow = std::clamp(1.0f - std::fabs(d - 25.5f) / 6.0f, 0.0f, 1.0f) * 0.45f;
            float a = std::clamp(white + shadow * (1 - white), 0.0f, 1.0f);
            uint8_t v = static_cast<uint8_t>(white * 255.0f + 0.5f);  // premultiplied: color = white amount
            uint8_t* p = &px[(y * kCursorSize + x) * 4];
            p[0] = p[1] = p[2] = v;
            p[3] = static_cast<uint8_t>(a * 255.0f + 0.5f);
        }
    }
    Upload(gs.cursor, px.data());
}

XrVector3f Rotate(const XrQuaternionf& q, XrVector3f v) {
    XrVector3f u{q.x, q.y, q.z};
    XrVector3f t{2 * (u.y * v.z - u.z * v.y), 2 * (u.z * v.x - u.x * v.z), 2 * (u.x * v.y - u.y * v.x)};
    return {v.x + q.w * t.x + (u.y * t.z - u.z * t.y), v.y + q.w * t.y + (u.z * t.x - u.x * t.z),
            v.z + q.w * t.z + (u.x * t.y - u.y * t.x)};
}

// ---------------------------------------------------------------------------------------------
// Poses

bool Locate(XrSpace space, XrTime time, lyrenne::Pose& out) {
    if (space == XR_NULL_HANDLE) return false;
    XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
    if (!Ok(next.LocateSpace(space, gs.localSpace, time, &loc))) return false;
    const XrSpaceLocationFlags need = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    if ((loc.locationFlags & need) != need) return false;
    static_assert(sizeof(XrPosef) == sizeof(lyrenne::Pose), "pose layout");
    memcpy(&out, &loc.pose, sizeof(out));
    return true;
}

float ReadFloat(XrAction action, XrPath subaction, XrActionType type, bool* active) {
    XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
    gi.action = action;
    gi.subactionPath = subaction;
    *active = false;
    if (type == XR_ACTION_TYPE_BOOLEAN_INPUT) {
        XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN};
        if (!Ok(next.GetActionStateBoolean(gs.session, &gi, &st))) return 0;
        *active = st.isActive;
        return st.isActive && st.currentState ? 1.0f : 0.0f;
    }
    XrActionStateFloat st{XR_TYPE_ACTION_STATE_FLOAT};
    if (!Ok(next.GetActionStateFloat(gs.session, &gi, &st))) return 0;
    *active = st.isActive;
    return st.isActive ? st.currentState : 0.0f;
}

// The trigger for each hand while the user points at the panel.
void ReadTrigger(float trig[2]) {
    if (!gs.attached || !gs.captureOn) return;
    XrPath hands[2] = {gs.leftHand, gs.rightHand};

    if (!gs.captureBroken && gs.clickAction != XR_NULL_HANDLE) {
        bool any = false, act[2] = {false, false}, nullAct = false;
        for (int i = 0; i < 2; i++) {
            trig[i] = ReadFloat(gs.clickAction, hands[i], XR_ACTION_TYPE_FLOAT_INPUT, &act[i]);
            any |= act[i];
        }
        float both = 0;
        if (!any) {
            // Some runtimes only answer for the whole action rather than per hand.
            both = ReadFloat(gs.clickAction, XR_NULL_PATH, XR_ACTION_TYPE_FLOAT_INPUT, &nullAct);
            if (nullAct) trig[0] = trig[1] = both;
        }
        if (gs.captureFrames == 5)
            Log("click action: left active %d (%.2f), right active %d (%.2f), whole action active %d (%.2f)",
                act[0], trig[0], act[1], trig[1], nullAct, both);
        if (any || nullAct) {
            gs.clickSeen = true;
        } else if (gs.captureFrames > 15 && !gs.clickSeen) {
            gs.captureBroken = true;
            Log("click action never became active on this runtime and game; falling back to the game's own trigger "
                "actions (the game will also see panel clicks)");
        }
        return;
    }

    // Fallback: the game's own trigger actions, which are active again now that nothing takes them.
    for (auto& [action, info] : gs.gameTriggers) {
        if (!info.pull) continue;
        for (int i = 0; i < 2; i++) {
            if (!(i ? info.right : info.left)) continue;
            bool active = false;
            float v = ReadFloat(action, hands[i], info.type, &active);
            if (!active) v = ReadFloat(action, XR_NULL_PATH, info.type, &active);  // action without per-hand paths
            if (active) trig[i] = std::max(trig[i], v);
        }
    }
}

// A few frames into each stretch where the panel holds the trigger, log whether the game's own
// trigger actions went inactive. That is the runtime's answer to "does the game still fire".
void ReportGameTriggers() {
    if (!gs.captureOn) {
        gs.captureFrames = 0;
        return;
    }
    if (++gs.captureFrames != 5 || gs.gameTriggers.empty() || gs.captureBroken) return;
    for (auto& [action, info] : gs.gameTriggers) {
        XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
        gi.action = action;
        XrBool32 active = XR_FALSE;
        XrResult r = XR_ERROR_ACTION_TYPE_MISMATCH;
        if (info.type == XR_ACTION_TYPE_BOOLEAN_INPUT) {
            XrActionStateBoolean st{XR_TYPE_ACTION_STATE_BOOLEAN};
            r = next.GetActionStateBoolean(gs.session, &gi, &st);
            active = st.isActive;
        } else if (info.type == XR_ACTION_TYPE_FLOAT_INPUT) {
            XrActionStateFloat st{XR_TYPE_ACTION_STATE_FLOAT};
            r = next.GetActionStateFloat(gs.session, &gi, &st);
            active = st.isActive;
        } else {
            continue;
        }
        Log("panel holds the trigger: game action '%s' %s (result %d)", info.name.c_str(),
            active ? "STILL ACTIVE, the game sees the trigger" : "inactive, the game is blocked", r);
    }
}

void PublishPoses(lyrenne::Bridge* b, XrTime time) {
    uint32_t flags = 0;
    lyrenne::Pose head{}, lg{}, rg{}, la{}, ra{};
    if (Locate(gs.viewSpace, time, head)) flags |= lyrenne::PoseHead;
    if (Locate(gs.leftGrip, time, lg)) flags |= lyrenne::PoseLeftGrip;
    if (Locate(gs.rightGrip, time, rg)) flags |= lyrenne::PoseRightGrip;
    if (Locate(gs.leftAim, time, la)) flags |= lyrenne::PoseLeftAim;
    if (Locate(gs.rightAim, time, ra)) flags |= lyrenne::PoseRightAim;
    b->head = head;
    b->leftGrip = lg;
    b->rightGrip = rg;
    b->leftAim = la;
    b->rightAim = ra;
    b->poseFlags = flags;
    b->layerFeatures = lyrenne::FeatureCursorQuad;
    float trig[2] = {0, 0};
    ReadTrigger(trig);
    b->triggerLeft = trig[0];
    b->triggerRight = trig[1];
    ReportGameTriggers();
    b->clientPid = GetCurrentProcessId();
    CopyName(b->runtimeName, gs.runtimeName);
    CopyName(b->appName, gs.appName);
    MemoryBarrier();
    b->poseSeq++;
    b->clientHeartbeatMs = static_cast<int64_t>(GetTickCount64());
}

void ApplyHaptics(lyrenne::Bridge* b) {
    uint32_t seq = b->hapticSeq;
    if (seq == gs.lastHapticSeq) return;
    gs.lastHapticSeq = seq;
    if (!gs.attached || gs.hapticAction == XR_NULL_HANDLE) return;
    XrHapticActionInfo info{XR_TYPE_HAPTIC_ACTION_INFO};
    info.action = gs.hapticAction;
    info.subactionPath = b->hapticHand == 0 ? gs.leftHand : gs.rightHand;
    XrHapticVibration vib{XR_TYPE_HAPTIC_VIBRATION};
    vib.amplitude = std::clamp(b->hapticAmplitude, 0.0f, 1.0f);
    vib.duration = static_cast<XrDuration>(std::clamp(b->hapticDurationS, 0.0f, 0.5f) * 1e9f);
    vib.frequency = XR_FREQUENCY_UNSPECIFIED;
    next.ApplyHapticFeedback(gs.session, &info, reinterpret_cast<XrHapticBaseHeader*>(&vib));
}

void DestroySessionObjects() {
    for (XrSpace* s : {&gs.leftGrip, &gs.rightGrip, &gs.leftAim, &gs.rightAim, &gs.viewSpace, &gs.localSpace}) {
        if (*s != XR_NULL_HANDLE) next.DestroySpace(*s);
        *s = XR_NULL_HANDLE;
    }
    ReleaseGraphics();
    gs.session = XR_NULL_HANDLE;
    gs.attached = false;
}

// ---------------------------------------------------------------------------------------------
// Intercepted entry points

XrResult XRAPI_CALL Hook_xrCreateAction(XrActionSet set, const XrActionCreateInfo* info, XrAction* action) {
    XrResult r = next.CreateAction(set, info, action);
    if (Ok(r) && gs.enabled && info && set != gs.actionSet && set != gs.captureSet) {
        std::lock_guard<std::mutex> lock(gStateLock);
        gs.gameActions[*action] = {info->actionName, info->actionType};
    }
    return r;
}

XrResult XRAPI_CALL Hook_xrSuggestInteractionProfileBindings(XrInstance instance, const XrInteractionProfileSuggestedBinding* suggested) {
    if (!gs.enabled || gs.actionSet == XR_NULL_HANDLE || !suggested) return next.SuggestInteractionProfileBindings(instance, suggested);

    // Add our pose (and, where the profile has one, haptic) bindings to whatever the game suggests,
    // so our action set works on exactly the controllers the game supports. Never suggest a profile
    // on our own: that could make the runtime pick a profile the game has no bindings for.
    for (uint32_t i = 0; i < suggested->countSuggestedBindings; i++) {
        const auto& sb = suggested->suggestedBindings[i];
        char path[XR_MAX_PATH_LENGTH];
        uint32_t len = 0;
        if (!Ok(next.PathToString(instance, sb.binding, sizeof(path), &len, path))) continue;
        if (!strstr(path, "/input/trigger") && !strstr(path, "/input/select")) continue;
        auto it = gs.gameActions.find(sb.action);
        if (it == gs.gameActions.end()) continue;
        auto& g = gs.gameTriggers.emplace(sb.action, it->second).first->second;
        if (strstr(path, "/user/hand/left")) g.left = true;
        if (strstr(path, "/user/hand/right")) g.right = true;
        if (!strstr(path, "/touch")) g.pull = true;
    }

    // Most controllers have an analog trigger; the simple controller and some others only "select".
    std::vector<XrActionSuggestedBinding> poses(suggested->suggestedBindings,
                                               suggested->suggestedBindings + suggested->countSuggestedBindings);
    poses.push_back({gs.gripAction, gs.leftGripPath});
    poses.push_back({gs.gripAction, gs.rightGripPath});
    poses.push_back({gs.aimAction, gs.leftAimPath});
    poses.push_back({gs.aimAction, gs.rightAimPath});
    auto plus = [](std::vector<XrActionSuggestedBinding> v, std::initializer_list<XrActionSuggestedBinding> more) {
        v.insert(v.end(), more);
        return v;
    };
    auto haptics = plus(poses, {{gs.hapticAction, gs.leftHapticPath}, {gs.hapticAction, gs.rightHapticPath}});
    std::vector<std::vector<XrActionSuggestedBinding>> candidates;
    if (gs.clickAction != XR_NULL_HANDLE) {
        // Trigger plus its touch sensor where the controller has one (Touch, Index), then without.
        if (gs.touchAction != XR_NULL_HANDLE)
            candidates.push_back(plus(haptics, {{gs.clickAction, gs.leftTriggerPath}, {gs.clickAction, gs.rightTriggerPath},
                                                {gs.touchAction, gs.leftTouchPath}, {gs.touchAction, gs.rightTouchPath}}));
        candidates.push_back(plus(haptics, {{gs.clickAction, gs.leftTriggerPath}, {gs.clickAction, gs.rightTriggerPath}}));
        candidates.push_back(plus(haptics, {{gs.clickAction, gs.leftSelectPath}, {gs.clickAction, gs.rightSelectPath}}));
    }
    candidates.push_back(haptics);
    if (gs.clickAction != XR_NULL_HANDLE)
        candidates.push_back(plus(poses, {{gs.clickAction, gs.leftTriggerPath}, {gs.clickAction, gs.rightTriggerPath}}));
    candidates.push_back(poses);

    for (auto& list : candidates) {
        XrInteractionProfileSuggestedBinding copy = *suggested;
        copy.countSuggestedBindings = static_cast<uint32_t>(list.size());
        copy.suggestedBindings = list.data();
        XrResult r = next.SuggestInteractionProfileBindings(instance, &copy);
        if (Ok(r)) {
            gs.bindingsAccepted = true;
            Log("bindings for profile %llu: game %u + ours %zu", static_cast<unsigned long long>(suggested->interactionProfile),
                suggested->countSuggestedBindings, list.size() - suggested->countSuggestedBindings);
            return r;
        }
    }
    return next.SuggestInteractionProfileBindings(instance, suggested);
}

XrResult XRAPI_CALL Hook_xrAttachSessionActionSets(XrSession session, const XrSessionActionSetsAttachInfo* info) {
    if (!gs.enabled || session != gs.session || gs.actionSet == XR_NULL_HANDLE || !gs.bindingsAccepted || !info)
        return next.AttachSessionActionSets(session, info);

    std::vector<XrActionSet> sets(info->actionSets, info->actionSets + info->countActionSets);
    sets.push_back(gs.actionSet);
    if (gs.captureSet != XR_NULL_HANDLE) sets.push_back(gs.captureSet);
    XrSessionActionSetsAttachInfo copy = *info;
    copy.countActionSets = static_cast<uint32_t>(sets.size());
    copy.actionSets = sets.data();
    XrResult r = next.AttachSessionActionSets(session, &copy);
    if (!Ok(r)) {
        Log("attaching with our action set failed (%d), passing the game's through alone", r);
        return next.AttachSessionActionSets(session, info);
    }
    gs.attached = true;

    auto makeSpace = [&](XrAction action, XrPath hand, XrSpace& out) {
        XrActionSpaceCreateInfo ci{XR_TYPE_ACTION_SPACE_CREATE_INFO};
        ci.action = action;
        ci.subactionPath = hand;
        ci.poseInActionSpace.orientation.w = 1.0f;
        if (!Ok(next.CreateActionSpace(session, &ci, &out))) out = XR_NULL_HANDLE;
    };
    makeSpace(gs.gripAction, gs.leftHand, gs.leftGrip);
    makeSpace(gs.gripAction, gs.rightHand, gs.rightGrip);
    makeSpace(gs.aimAction, gs.leftHand, gs.leftAim);
    makeSpace(gs.aimAction, gs.rightHand, gs.rightAim);
    Log("action set attached, hand tracking ready");
    return r;
}

XrResult XRAPI_CALL Hook_xrSyncActions(XrSession session, const XrActionsSyncInfo* info) {
    if (!gs.attached || session != gs.session || !info) return next.SyncActions(session, info);
    std::vector<XrActiveActionSet> active(info->activeActionSets, info->activeActionSets + info->countActiveActionSets);
    active.push_back({gs.actionSet, XR_NULL_PATH});
    if (gs.captureOn && !gs.captureBroken && gs.captureSet != XR_NULL_HANDLE) active.push_back({gs.captureSet, XR_NULL_PATH});
    XrActionsSyncInfo copy = *info;
    copy.countActiveActionSets = static_cast<uint32_t>(active.size());
    copy.activeActionSets = active.data();
    return next.SyncActions(session, &copy);
}

XrResult XRAPI_CALL Hook_xrCreateSession(XrInstance instance, const XrSessionCreateInfo* info, XrSession* session) {
    XrResult r = next.CreateSession(instance, info, session);
    if (!Ok(r) || !gs.enabled) return r;

    std::lock_guard<std::mutex> lock(gStateLock);
    if (gs.session != XR_NULL_HANDLE) DestroySessionObjects();
    gs.session = *session;

    for (auto* h = static_cast<const XrBaseInStructure*>(info->next); h; h = h->next) {
        if (h->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR) {
            auto* b = reinterpret_cast<const XrGraphicsBindingD3D11KHR*>(h);
            gs.gfx = Gfx::D3D11;
            gs.d3d11 = b->device;
            gs.d3d11->AddRef();
            gs.d3d11->GetImmediateContext(&gs.d3d11Context);
        } else if (h->type == XR_TYPE_GRAPHICS_BINDING_D3D12_KHR) {
            auto* b = reinterpret_cast<const XrGraphicsBindingD3D12KHR*>(h);
            gs.gfx = Gfx::D3D12;
            gs.d3d12 = b->device;
            gs.d3d12Queue = b->queue;
            gs.d3d12->AddRef();
            gs.d3d12Queue->AddRef();
            if (!InitD3D12Commands()) {
                Log("D3D12 command setup failed, panel disabled for this session");
                ReleaseGraphics();
            }
        }
    }
    if (gs.gfx == Gfx::None) Log("session uses a graphics API the panel does not support yet (Vulkan/OpenGL); poses only");

    XrSystemProperties props{XR_TYPE_SYSTEM_PROPERTIES};
    if (Ok(next.GetSystemProperties(instance, info->systemId, &props))) gs.maxLayers = props.graphicsProperties.maxLayerCount;

    XrReferenceSpaceCreateInfo rs{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    rs.poseInReferenceSpace.orientation.w = 1.0f;
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    next.CreateReferenceSpace(*session, &rs, &gs.localSpace);
    rs.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    next.CreateReferenceSpace(*session, &rs, &gs.viewSpace);
    Log("session created (%s), max layers %u", gs.gfx == Gfx::D3D11 ? "D3D11" : gs.gfx == Gfx::D3D12 ? "D3D12" : "other", gs.maxLayers);
    return r;
}

XrResult XRAPI_CALL Hook_xrDestroySession(XrSession session) {
    {
        std::lock_guard<std::mutex> lock(gStateLock);
        if (session == gs.session) DestroySessionObjects();
    }
    return next.DestroySession(session);
}

XrResult XRAPI_CALL Hook_xrEndFrame(XrSession session, const XrFrameEndInfo* info) {
    if (!gs.enabled || session != gs.session || !info || gs.localSpace == XR_NULL_HANDLE) return next.EndFrame(session, info);

    std::unique_lock<std::mutex> lock(gStateLock);
    lyrenne::Bridge* b = LiveBridge();
    if (!b) {
        lock.unlock();
        return next.EndFrame(session, info);
    }

    gs.captureOn = b->captureInput != 0;
    PublishPoses(b, info->displayTime);
    ApplyHaptics(b);

    XrCompositionLayerQuad quad{XR_TYPE_COMPOSITION_LAYER_QUAD};
    XrCompositionLayerQuad cursor{XR_TYPE_COMPOSITION_LAYER_QUAD};
    bool show = false, showCursor = false;
    if (b->visible && gs.gfx != Gfx::None) {
        UploadPanelIfChanged(b);
        XrSpace anchor = b->anchor == lyrenne::AnchorLeftGrip ? gs.leftGrip
                         : b->anchor == lyrenne::AnchorRightGrip ? gs.rightGrip
                                                                 : gs.localSpace;
        if (gs.panel.hasContent && anchor != XR_NULL_HANDLE && info->layerCount < gs.maxLayers) {
            quad.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
            quad.space = anchor;
            quad.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
            quad.subImage.swapchain = gs.panel.swapchain;
            quad.subImage.imageRect = {{0, 0}, {static_cast<int32_t>(gs.panel.width), static_cast<int32_t>(gs.panel.height)}};
            memcpy(&quad.pose, &b->panelPose, sizeof(quad.pose));
            quad.size.width = b->panelWidthM;
            quad.size.height = b->panelWidthM * gs.panel.height / gs.panel.width;
            show = true;

            // The cursor rides on its own small quad just in front of the panel, so moving it costs
            // a pose change instead of a redraw and a 2 MB upload of the whole panel.
            if (b->cursorVisible && info->layerCount + 1 < gs.maxLayers) {
                EnsureCursor();
                if (gs.cursor.hasContent) {
                    XrVector3f offset{(b->cursorU - 0.5f) * quad.size.width, (0.5f - b->cursorV) * quad.size.height, 0.002f};
                    XrVector3f r = Rotate(quad.pose.orientation, offset);
                    cursor = quad;
                    cursor.subImage.swapchain = gs.cursor.swapchain;
                    cursor.subImage.imageRect = {{0, 0}, {static_cast<int32_t>(kCursorSize), static_cast<int32_t>(kCursorSize)}};
                    cursor.pose.position = {quad.pose.position.x + r.x, quad.pose.position.y + r.y, quad.pose.position.z + r.z};
                    cursor.size = {b->cursorSizeM, b->cursorSizeM};
                    showCursor = true;
                }
            }
        }
    }
    lock.unlock();

    if (!show) return next.EndFrame(session, info);
    std::vector<const XrCompositionLayerBaseHeader*> layers(info->layers, info->layers + info->layerCount);
    layers.push_back(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&quad));
    if (showCursor) layers.push_back(reinterpret_cast<const XrCompositionLayerBaseHeader*>(&cursor));
    XrFrameEndInfo copy = *info;
    copy.layerCount = static_cast<uint32_t>(layers.size());
    copy.layers = layers.data();
    return next.EndFrame(session, &copy);
}

XrResult XRAPI_CALL Hook_xrDestroyInstance(XrInstance instance) {
    {
        std::lock_guard<std::mutex> lock(gStateLock);
        if (instance == gs.instance) {
            if (gs.session != XR_NULL_HANDLE) DestroySessionObjects();
            if (gs.actionSet != XR_NULL_HANDLE) next.DestroyActionSet(gs.actionSet);
            if (gs.captureSet != XR_NULL_HANDLE) next.DestroyActionSet(gs.captureSet);
            CloseBridge();
            gs = State{};
        }
    }
    PFN_xrDestroyInstance destroy = next.DestroyInstance;
    return destroy(instance);
}

XrResult XRAPI_CALL Hook_xrGetInstanceProcAddr(XrInstance instance, const char* name, PFN_xrVoidFunction* function) {
#define HOOK(fn)                                                       \
    if (strcmp(name, #fn) == 0) {                                      \
        *function = reinterpret_cast<PFN_xrVoidFunction>(Hook_##fn);   \
        return XR_SUCCESS;                                             \
    }
    if (name && function) {
        HOOK(xrGetInstanceProcAddr)
        HOOK(xrDestroyInstance)
        HOOK(xrSuggestInteractionProfileBindings)
        HOOK(xrAttachSessionActionSets)
        HOOK(xrSyncActions)
        HOOK(xrCreateAction)
        HOOK(xrCreateSession)
        HOOK(xrDestroySession)
        HOOK(xrEndFrame)
    }
#undef HOOK
    if (!next.GetInstanceProcAddr) return XR_ERROR_HANDLE_INVALID;
    return next.GetInstanceProcAddr(instance, name, function);
}

void SetUpActions() {
    auto path = [](const char* s) {
        XrPath p = XR_NULL_PATH;
        next.StringToPath(gs.instance, s, &p);
        return p;
    };
    gs.leftHand = path("/user/hand/left");
    gs.rightHand = path("/user/hand/right");
    gs.leftGripPath = path("/user/hand/left/input/grip/pose");
    gs.rightGripPath = path("/user/hand/right/input/grip/pose");
    gs.leftAimPath = path("/user/hand/left/input/aim/pose");
    gs.rightAimPath = path("/user/hand/right/input/aim/pose");
    gs.leftHapticPath = path("/user/hand/left/output/haptic");
    gs.rightHapticPath = path("/user/hand/right/output/haptic");
    gs.leftTriggerPath = path("/user/hand/left/input/trigger/value");
    gs.rightTriggerPath = path("/user/hand/right/input/trigger/value");
    gs.leftSelectPath = path("/user/hand/left/input/select/click");
    gs.leftTouchPath = path("/user/hand/left/input/trigger/touch");
    gs.rightTouchPath = path("/user/hand/right/input/trigger/touch");
    gs.rightSelectPath = path("/user/hand/right/input/select/click");

    XrActionSetCreateInfo setInfo{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(setInfo.actionSetName, "lyrenne_wrist_player");
    strcpy_s(setInfo.localizedActionSetName, "Lyrenne Wrist Player");
    setInfo.priority = 0;
    XrResult r = next.CreateActionSet(gs.instance, &setInfo, &gs.actionSet);
    if (!Ok(r)) {
        Log("xrCreateActionSet failed: %d", r);
        gs.actionSet = XR_NULL_HANDLE;
        return;
    }
    XrPath hands[] = {gs.leftHand, gs.rightHand};
    auto make = [&](const char* id, const char* label, XrActionType type, XrAction& out) {
        XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
        strcpy_s(ai.actionName, id);
        strcpy_s(ai.localizedActionName, label);
        ai.actionType = type;
        ai.countSubactionPaths = 2;
        ai.subactionPaths = hands;
        return Ok(next.CreateAction(gs.actionSet, &ai, &out));
    };
    if (!make("hand_grip", "Hand grip pose", XR_ACTION_TYPE_POSE_INPUT, gs.gripAction) ||
        !make("hand_aim", "Hand aim pose", XR_ACTION_TYPE_POSE_INPUT, gs.aimAction) ||
        !make("hand_haptic", "Tap feedback", XR_ACTION_TYPE_VIBRATION_OUTPUT, gs.hapticAction)) {
        Log("xrCreateAction failed");
        next.DestroyActionSet(gs.actionSet);
        gs.actionSet = XR_NULL_HANDLE;
        return;
    }

    XrActionSetCreateInfo captureInfo{XR_TYPE_ACTION_SET_CREATE_INFO};
    strcpy_s(captureInfo.actionSetName, "lyrenne_wrist_player_click");
    strcpy_s(captureInfo.localizedActionSetName, "Lyrenne Wrist Player clicks");
    captureInfo.priority = 10000;
    XrAction click = XR_NULL_HANDLE;
    if (Ok(next.CreateActionSet(gs.instance, &captureInfo, &gs.captureSet))) {
        XrActionCreateInfo ai{XR_TYPE_ACTION_CREATE_INFO};
        strcpy_s(ai.actionName, "panel_click");
        strcpy_s(ai.localizedActionName, "Click the wrist player");
        ai.actionType = XR_ACTION_TYPE_FLOAT_INPUT;
        ai.countSubactionPaths = 2;
        ai.subactionPaths = hands;
        if (Ok(next.CreateAction(gs.captureSet, &ai, &click))) {
            gs.clickAction = click;
            XrAction touch = XR_NULL_HANDLE;
            strcpy_s(ai.actionName, "panel_touch");
            strcpy_s(ai.localizedActionName, "Finger on the trigger while using the wrist player");
            ai.actionType = XR_ACTION_TYPE_BOOLEAN_INPUT;
            if (Ok(next.CreateAction(gs.captureSet, &ai, &touch))) gs.touchAction = touch;
        } else {
            next.DestroyActionSet(gs.captureSet);
            gs.captureSet = XR_NULL_HANDLE;
        }
    }
    if (gs.captureSet == XR_NULL_HANDLE) Log("trigger clicks unavailable, finger touch only");
}

bool LoadDispatch(XrInstance instance) {
    bool ok = true;
#define LOAD(fn)                                                                                          \
    if (!Ok(next.GetInstanceProcAddr(instance, "xr" #fn, reinterpret_cast<PFN_xrVoidFunction*>(&next.fn))) || !next.fn) { \
        Log("missing xr" #fn);                                                                            \
        ok = false;                                                                                       \
    }
    LOAD(DestroyInstance)
    LOAD(GetInstanceProperties)
    LOAD(GetSystemProperties)
    LOAD(StringToPath)
    LOAD(CreateActionSet)
    LOAD(DestroyActionSet)
    LOAD(CreateAction)
    LOAD(SuggestInteractionProfileBindings)
    LOAD(AttachSessionActionSets)
    LOAD(SyncActions)
    LOAD(GetActionStateFloat)
    LOAD(GetActionStateBoolean)
    LOAD(PathToString)
    LOAD(ApplyHapticFeedback)
    LOAD(CreateSession)
    LOAD(DestroySession)
    LOAD(CreateReferenceSpace)
    LOAD(CreateActionSpace)
    LOAD(LocateSpace)
    LOAD(DestroySpace)
    LOAD(EnumerateSwapchainFormats)
    LOAD(CreateSwapchain)
    LOAD(DestroySwapchain)
    LOAD(EnumerateSwapchainImages)
    LOAD(AcquireSwapchainImage)
    LOAD(WaitSwapchainImage)
    LOAD(ReleaseSwapchainImage)
    LOAD(EndFrame)
#undef LOAD
    return ok;
}

bool EnvSet(const char* name) {
    char buf[8];
    return GetEnvironmentVariableA(name, buf, sizeof(buf)) > 0;
}

XrResult XRAPI_CALL CreateApiLayerInstance(const XrInstanceCreateInfo* info, const XrApiLayerCreateInfo* layerInfo, XrInstance* instance) {
    if (!layerInfo || !layerInfo->nextInfo || strcmp(layerInfo->nextInfo->layerName, LAYER_NAME) != 0) return XR_ERROR_INITIALIZATION_FAILED;

    next.GetInstanceProcAddr = layerInfo->nextInfo->nextGetInstanceProcAddr;
    XrApiLayerCreateInfo chained = *layerInfo;
    chained.nextInfo = layerInfo->nextInfo->next;
    XrResult r = layerInfo->nextInfo->nextCreateApiLayerInstance(info, &chained, instance);
    if (!Ok(r)) return r;

    std::lock_guard<std::mutex> lock(gStateLock);
    gs = State{};
    gs.instance = *instance;
    if (!LoadDispatch(*instance)) {
        Log("runtime is missing core functions, staying passive");
        return r;
    }
    XrInstanceProperties props{XR_TYPE_INSTANCE_PROPERTIES};
    if (Ok(next.GetInstanceProperties(*instance, &props))) gs.runtimeName = props.runtimeName;
    gs.appName = info->applicationInfo.applicationName;

    // SteamVR shows OpenVR overlays over its OpenXR games, and the host draws one there.
    // Drawing here as well would show the panel twice.
    bool steamVr = gs.runtimeName.find("SteamVR") != std::string::npos;
    gs.enabled = !steamVr || EnvSet("LYRENNE_VR_FORCE_LAYER");
    Log("instance for '%s' on runtime '%s': %s", gs.appName.c_str(), gs.runtimeName.c_str(),
        gs.enabled ? "active" : "passive (SteamVR overlay handles it)");
    if (gs.enabled) SetUpActions();
    if (gs.actionSet == XR_NULL_HANDLE) gs.enabled = false;
    return r;
}

}  // namespace

extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderApiLayerInterface(const XrNegotiateLoaderInfo* loaderInfo,
                                                                                         const char* layerName,
                                                                                         XrNegotiateApiLayerRequest* request) {
    if (!loaderInfo || !request || !layerName || strcmp(layerName, LAYER_NAME) != 0) return XR_ERROR_INITIALIZATION_FAILED;
    if (loaderInfo->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        request->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST ||
        loaderInfo->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION) {
        return XR_ERROR_INITIALIZATION_FAILED;
    }
    request->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    request->layerApiVersion = std::min<XrVersion>(XR_CURRENT_API_VERSION, loaderInfo->maxApiVersion);
    request->getInstanceProcAddr = Hook_xrGetInstanceProcAddr;
    request->createApiLayerInstance = CreateApiLayerInstance;
    return XR_SUCCESS;
}
