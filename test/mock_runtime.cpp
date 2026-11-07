// A minimal OpenXR runtime for testing the layer without a headset. It implements just enough of
// the API for xrtest.exe, with real D3D11/D3D12 swapchain textures, and records what the layer did
// (bindings, attached action sets, submitted layers) so the test can check it. Poses are synthetic.

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
#include <cstring>
#include <map>
#include <string>
#include <vector>

namespace {

uint64_t gNextHandle = 1;
template <typename T>
T NewHandle() {
    return reinterpret_cast<T>(gNextHandle++);
}

std::map<std::string, XrPath> gPaths;
std::map<XrPath, std::string> gPathNames;

struct ActionInfo {
    std::string name;
};
std::map<XrAction, ActionInfo> gActions;

enum class SpaceKind { Local, View, Action };
struct SpaceInfo {
    SpaceKind kind;
    XrAction action;
    XrPath subaction;
};
std::map<XrSpace, SpaceInfo> gSpaces;

struct Swapchain {
    int64_t format;
    uint32_t width, height;
    std::vector<ID3D11Texture2D*> tex11;
    std::vector<ID3D12Resource*> tex12;
    uint32_t next = 0;
    int32_t lastReleased = -1;
    int32_t acquired = -1;
};
std::map<XrSwapchain, Swapchain> gSwapchains;

ID3D11Device* gD3D11 = nullptr;
ID3D12Device* gD3D12 = nullptr;
ID3D12CommandQueue* gQueue = nullptr;

// What the test inspects afterwards.
struct Record {
    int suggestCalls = 0;
    int lastSuggestCount = 0;
    int attachSetCount = 0;
    int attachCalls = 0;
    int lastSyncSetCount = 0;
    int hapticCalls = 0;
    int clickReads = 0;      // xrGetActionStateFloat on the layer's panel_click action
    int maxSyncSetCount = 0;
    int framesEnded = 0;
    int framesWithQuad = 0;
    std::string quadSpace;
    float quadWidth = 0, quadHeight = 0;
    XrPosef quadPose{};
    uint32_t pixelWidth = 0, pixelHeight = 0;
    std::vector<uint8_t> pixels;  // BGRA
    bool rgba = false;
} gRecord;

XrPath Path(const char* s) {
    auto it = gPaths.find(s);
    if (it != gPaths.end()) return it->second;
    XrPath p = static_cast<XrPath>(gPaths.size() + 1);
    gPaths[s] = p;
    gPathNames[p] = s;
    return p;
}

XrResult XRAPI_CALL EnumerateInstanceExtensionProperties(const char*, uint32_t cap, uint32_t* count, XrExtensionProperties* props) {
    const char* exts[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME, XR_KHR_D3D12_ENABLE_EXTENSION_NAME};
    *count = 2;
    if (cap == 0) return XR_SUCCESS;
    if (cap < 2) return XR_ERROR_SIZE_INSUFFICIENT;
    for (int i = 0; i < 2; i++) {
        strcpy_s(props[i].extensionName, exts[i]);
        props[i].extensionVersion = 1;
    }
    return XR_SUCCESS;
}

XrResult XRAPI_CALL CreateInstance(const XrInstanceCreateInfo*, XrInstance* instance) {
    *instance = NewHandle<XrInstance>();
    return XR_SUCCESS;
}
XrResult XRAPI_CALL DestroyInstance(XrInstance) { return XR_SUCCESS; }
XrResult XRAPI_CALL GetInstanceProperties(XrInstance, XrInstanceProperties* p) {
    strcpy_s(p->runtimeName, "Lyrenne Mock Runtime");
    p->runtimeVersion = XR_MAKE_VERSION(1, 0, 0);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL GetSystem(XrInstance, const XrSystemGetInfo*, XrSystemId* id) {
    *id = 1;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL GetSystemProperties(XrInstance, XrSystemId, XrSystemProperties* p) {
    strcpy_s(p->systemName, "Mock HMD");
    p->graphicsProperties.maxLayerCount = 16;
    p->graphicsProperties.maxSwapchainImageWidth = 4096;
    p->graphicsProperties.maxSwapchainImageHeight = 4096;
    p->trackingProperties.orientationTracking = XR_TRUE;
    p->trackingProperties.positionTracking = XR_TRUE;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL GetD3D11Req(XrInstance, XrSystemId, XrGraphicsRequirementsD3D11KHR* r) {
    r->adapterLuid = {};
    r->minFeatureLevel = D3D_FEATURE_LEVEL_11_0;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL GetD3D12Req(XrInstance, XrSystemId, XrGraphicsRequirementsD3D12KHR* r) {
    r->adapterLuid = {};
    r->minFeatureLevel = D3D_FEATURE_LEVEL_11_0;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL StringToPath(XrInstance, const char* s, XrPath* p) {
    *p = Path(s);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL PathToString(XrInstance, XrPath p, uint32_t cap, uint32_t* count, char* buf) {
    auto& s = gPathNames[p];
    *count = static_cast<uint32_t>(s.size() + 1);
    if (cap >= *count) strcpy_s(buf, cap, s.c_str());
    return XR_SUCCESS;
}
XrResult XRAPI_CALL CreateActionSet(XrInstance, const XrActionSetCreateInfo*, XrActionSet* s) {
    *s = NewHandle<XrActionSet>();
    return XR_SUCCESS;
}
XrResult XRAPI_CALL DestroyActionSet(XrActionSet) { return XR_SUCCESS; }
XrResult XRAPI_CALL CreateAction(XrActionSet, const XrActionCreateInfo* info, XrAction* a) {
    *a = NewHandle<XrAction>();
    gActions[*a] = {info->actionName};
    return XR_SUCCESS;
}
XrResult XRAPI_CALL DestroyAction(XrAction) { return XR_SUCCESS; }
XrResult XRAPI_CALL SuggestBindings(XrInstance, const XrInteractionProfileSuggestedBinding* s) {
    gRecord.suggestCalls++;
    gRecord.lastSuggestCount = static_cast<int>(s->countSuggestedBindings);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL AttachSets(XrSession, const XrSessionActionSetsAttachInfo* info) {
    if (gRecord.attachCalls++ > 0) return XR_ERROR_ACTIONSETS_ALREADY_ATTACHED;
    gRecord.attachSetCount = static_cast<int>(info->countActionSets);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL SyncActions(XrSession, const XrActionsSyncInfo* info) {
    gRecord.lastSyncSetCount = static_cast<int>(info->countActiveActionSets);
    gRecord.maxSyncSetCount = std::max(gRecord.maxSyncSetCount, gRecord.lastSyncSetCount);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL GetActionStateBoolean(XrSession, const XrActionStateGetInfo*, XrActionStateBoolean* s) {
    s->currentState = XR_FALSE;
    s->isActive = XR_TRUE;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL GetActionStateFloat(XrSession, const XrActionStateGetInfo* gi, XrActionStateFloat* s) {
    if (gActions[gi->action].name == "panel_click") gRecord.clickReads++;
    s->currentState = 0;  // never actually click: that would toggle the real media player
    s->isActive = XR_TRUE;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL ApplyHaptic(XrSession, const XrHapticActionInfo*, const XrHapticBaseHeader*) {
    gRecord.hapticCalls++;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL CreateSession(XrInstance, const XrSessionCreateInfo* info, XrSession* s) {
    for (auto* h = static_cast<const XrBaseInStructure*>(info->next); h; h = h->next) {
        if (h->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR) gD3D11 = reinterpret_cast<const XrGraphicsBindingD3D11KHR*>(h)->device;
        if (h->type == XR_TYPE_GRAPHICS_BINDING_D3D12_KHR) {
            gD3D12 = reinterpret_cast<const XrGraphicsBindingD3D12KHR*>(h)->device;
            gQueue = reinterpret_cast<const XrGraphicsBindingD3D12KHR*>(h)->queue;
        }
    }
    *s = NewHandle<XrSession>();
    return XR_SUCCESS;
}
XrResult XRAPI_CALL DestroySession(XrSession) { return XR_SUCCESS; }
XrResult XRAPI_CALL BeginSession(XrSession, const XrSessionBeginInfo*) { return XR_SUCCESS; }
XrResult XRAPI_CALL EndSession(XrSession) { return XR_SUCCESS; }
XrResult XRAPI_CALL PollEvent(XrInstance, XrEventDataBuffer*) { return XR_EVENT_UNAVAILABLE; }
XrResult XRAPI_CALL WaitFrame(XrSession, const XrFrameWaitInfo*, XrFrameState* st) {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    st->predictedDisplayTime = t.QuadPart;
    st->predictedDisplayPeriod = 11111111;
    st->shouldRender = XR_TRUE;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL BeginFrame(XrSession, const XrFrameBeginInfo*) { return XR_SUCCESS; }

XrResult XRAPI_CALL CreateReferenceSpace(XrSession, const XrReferenceSpaceCreateInfo* info, XrSpace* s) {
    *s = NewHandle<XrSpace>();
    gSpaces[*s] = {info->referenceSpaceType == XR_REFERENCE_SPACE_TYPE_VIEW ? SpaceKind::View : SpaceKind::Local, XR_NULL_HANDLE, XR_NULL_PATH};
    return XR_SUCCESS;
}
XrResult XRAPI_CALL CreateActionSpace(XrSession, const XrActionSpaceCreateInfo* info, XrSpace* s) {
    *s = NewHandle<XrSpace>();
    gSpaces[*s] = {SpaceKind::Action, info->action, info->subactionPath};
    return XR_SUCCESS;
}
XrResult XRAPI_CALL DestroySpace(XrSpace s) {
    gSpaces.erase(s);
    return XR_SUCCESS;
}
const XrVector3f kLeftHand{-0.15f, 1.25f, -0.35f};
XrVector3f Add(XrVector3f a, XrVector3f b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
XrVector3f Rotate(const XrQuaternionf& q, XrVector3f v) {
    XrVector3f u{q.x, q.y, q.z};
    XrVector3f t{2 * (u.y * v.z - u.z * v.y), 2 * (u.z * v.x - u.x * v.z), 2 * (u.x * v.y - u.y * v.x)};
    return {v.x + q.w * t.x + (u.y * t.z - u.z * t.y), v.y + q.w * t.y + (u.z * t.x - u.x * t.z),
            v.z + q.w * t.z + (u.x * t.y - u.y * t.x)};
}

XrResult XRAPI_CALL LocateSpace(XrSpace space, XrSpace, XrTime, XrSpaceLocation* loc) {
    auto it = gSpaces.find(space);
    if (it == gSpaces.end()) return XR_ERROR_HANDLE_INVALID;
    loc->locationFlags = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT |
                         XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    loc->pose = {{0, 0, 0, 1}, {0, 0, 0}};
    const SpaceInfo& info = it->second;
    if (info.kind == SpaceKind::View) loc->pose.position = {0, 1.6f, 0};
    if (info.kind == SpaceKind::Action) {
        bool left = gPathNames[info.subaction] == "/user/hand/left";
        loc->pose.position = left ? kLeftHand : XrVector3f{0.25f, 1.1f, -0.3f};
        // Once the layer has drawn the panel, aim the right controller straight at its centre from
        // 30 cm in front, the way a user would to click it with the trigger.
        if (!left && gActions[info.action].name == "hand_aim" && gRecord.framesWithQuad > 0) {
            const XrQuaternionf& q = gRecord.quadPose.orientation;
            XrVector3f n = Rotate(q, {0, 0, 1});
            XrVector3f c = Add(kLeftHand, gRecord.quadPose.position);  // left grip has no rotation
            loc->pose.position = Add(c, {n.x * 0.3f, n.y * 0.3f, n.z * 0.3f});
            loc->pose.orientation = q;  // aim -Z then points along -n, into the panel face
        }
    }
    return XR_SUCCESS;
}

XrResult XRAPI_CALL EnumerateSwapchainFormats(XrSession, uint32_t cap, uint32_t* count, int64_t* formats) {
    // RGBA first and no BGRA sRGB, so the layer's swizzle path gets exercised.
    const int64_t f[] = {DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R8G8B8A8_UNORM};
    *count = 2;
    if (cap >= 2) memcpy(formats, f, sizeof(f));
    return XR_SUCCESS;
}

XrResult XRAPI_CALL CreateSwapchain(XrSession, const XrSwapchainCreateInfo* info, XrSwapchain* out) {
    Swapchain sc;
    sc.format = info->format;
    sc.width = info->width;
    sc.height = info->height;
    for (int i = 0; i < 3; i++) {
        if (gD3D11) {
            D3D11_TEXTURE2D_DESC d{};
            d.Width = info->width;
            d.Height = info->height;
            d.MipLevels = 1;
            d.ArraySize = 1;
            d.Format = static_cast<DXGI_FORMAT>(info->format);
            d.SampleDesc.Count = 1;
            d.Usage = D3D11_USAGE_DEFAULT;
            d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            ID3D11Texture2D* t = nullptr;
            if (FAILED(gD3D11->CreateTexture2D(&d, nullptr, &t))) return XR_ERROR_RUNTIME_FAILURE;
            sc.tex11.push_back(t);
        } else {
            D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_DEFAULT};
            D3D12_RESOURCE_DESC d{};
            d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            d.Width = info->width;
            d.Height = info->height;
            d.DepthOrArraySize = 1;
            d.MipLevels = 1;
            d.Format = static_cast<DXGI_FORMAT>(info->format);
            d.SampleDesc.Count = 1;
            d.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
            ID3D12Resource* t = nullptr;
            if (FAILED(gD3D12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &d, D3D12_RESOURCE_STATE_RENDER_TARGET, nullptr,
                                                       IID_PPV_ARGS(&t))))
                return XR_ERROR_RUNTIME_FAILURE;
            sc.tex12.push_back(t);
        }
    }
    *out = NewHandle<XrSwapchain>();
    gSwapchains[*out] = sc;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL DestroySwapchain(XrSwapchain s) {
    auto& sc = gSwapchains[s];
    for (auto* t : sc.tex11) t->Release();
    for (auto* t : sc.tex12) t->Release();
    gSwapchains.erase(s);
    return XR_SUCCESS;
}
XrResult XRAPI_CALL EnumerateSwapchainImages(XrSwapchain s, uint32_t cap, uint32_t* count, XrSwapchainImageBaseHeader* images) {
    auto& sc = gSwapchains[s];
    *count = 3;
    if (cap < 3) return cap == 0 ? XR_SUCCESS : XR_ERROR_SIZE_INSUFFICIENT;
    for (uint32_t i = 0; i < 3; i++) {
        if (gD3D11) reinterpret_cast<XrSwapchainImageD3D11KHR*>(images)[i].texture = sc.tex11[i];
        else reinterpret_cast<XrSwapchainImageD3D12KHR*>(images)[i].texture = sc.tex12[i];
    }
    return XR_SUCCESS;
}
XrResult XRAPI_CALL AcquireImage(XrSwapchain s, const XrSwapchainImageAcquireInfo*, uint32_t* index) {
    auto& sc = gSwapchains[s];
    if (sc.acquired >= 0) return XR_ERROR_CALL_ORDER_INVALID;
    sc.acquired = static_cast<int32_t>(sc.next);
    *index = sc.next;
    sc.next = (sc.next + 1) % 3;
    return XR_SUCCESS;
}
XrResult XRAPI_CALL WaitImage(XrSwapchain, const XrSwapchainImageWaitInfo*) { return XR_SUCCESS; }
XrResult XRAPI_CALL ReleaseImage(XrSwapchain s, const XrSwapchainImageReleaseInfo*) {
    auto& sc = gSwapchains[s];
    if (sc.acquired < 0) return XR_ERROR_CALL_ORDER_INVALID;
    sc.lastReleased = sc.acquired;
    sc.acquired = -1;
    return XR_SUCCESS;
}

void ReadBack(Swapchain& sc) {
    if (sc.lastReleased < 0) return;
    uint32_t w = sc.width, h = sc.height;
    gRecord.pixelWidth = w;
    gRecord.pixelHeight = h;
    gRecord.pixels.assign(static_cast<size_t>(w) * h * 4, 0);
    gRecord.rgba = sc.format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || sc.format == DXGI_FORMAT_R8G8B8A8_UNORM;
    if (gD3D11) {
        D3D11_TEXTURE2D_DESC d;
        sc.tex11[sc.lastReleased]->GetDesc(&d);
        d.Usage = D3D11_USAGE_STAGING;
        d.BindFlags = 0;
        d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging = nullptr;
        gD3D11->CreateTexture2D(&d, nullptr, &staging);
        ID3D11DeviceContext* ctx = nullptr;
        gD3D11->GetImmediateContext(&ctx);
        ctx->CopyResource(staging, sc.tex11[sc.lastReleased]);
        D3D11_MAPPED_SUBRESOURCE m;
        if (SUCCEEDED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m))) {
            for (uint32_t y = 0; y < h; y++) memcpy(&gRecord.pixels[y * w * 4], static_cast<uint8_t*>(m.pData) + y * m.RowPitch, w * 4);
            ctx->Unmap(staging, 0);
        }
        staging->Release();
        ctx->Release();
        return;
    }
    // D3D12: copy into a readback buffer on the app's queue and wait for it.
    ID3D12Resource* tex = sc.tex12[sc.lastReleased];
    D3D12_RESOURCE_DESC desc = tex->GetDesc();
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp;
    UINT64 total = 0;
    gD3D12->GetCopyableFootprints(&desc, 0, 1, 0, &fp, nullptr, nullptr, &total);
    D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_READBACK};
    D3D12_RESOURCE_DESC buf{};
    buf.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    buf.Width = total;
    buf.Height = 1;
    buf.DepthOrArraySize = 1;
    buf.MipLevels = 1;
    buf.SampleDesc.Count = 1;
    buf.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    ID3D12Resource* rb = nullptr;
    ID3D12CommandAllocator* alloc = nullptr;
    ID3D12GraphicsCommandList* list = nullptr;
    ID3D12Fence* fence = nullptr;
    gD3D12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buf, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&rb));
    gD3D12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc));
    gD3D12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, nullptr, IID_PPV_ARGS(&list));
    gD3D12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
    D3D12_RESOURCE_BARRIER b{};
    b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource = tex;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    list->ResourceBarrier(1, &b);
    D3D12_TEXTURE_COPY_LOCATION src{tex, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX};
    src.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION dst{rb, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT};
    dst.PlacedFootprint = fp;
    list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
    std::swap(b.Transition.StateBefore, b.Transition.StateAfter);
    list->ResourceBarrier(1, &b);
    list->Close();
    ID3D12CommandList* lists[] = {list};
    gQueue->ExecuteCommandLists(1, lists);
    gQueue->Signal(fence, 1);
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    fence->SetEventOnCompletion(1, ev);
    WaitForSingleObject(ev, 2000);
    CloseHandle(ev);
    uint8_t* p = nullptr;
    if (SUCCEEDED(rb->Map(0, nullptr, reinterpret_cast<void**>(&p)))) {
        for (uint32_t y = 0; y < h; y++) memcpy(&gRecord.pixels[y * w * 4], p + fp.Offset + y * fp.Footprint.RowPitch, w * 4);
        rb->Unmap(0, nullptr);
    }
    fence->Release();
    list->Release();
    alloc->Release();
    rb->Release();
}

XrResult XRAPI_CALL EndFrame(XrSession, const XrFrameEndInfo* info) {
    gRecord.framesEnded++;
    for (uint32_t i = 0; i < info->layerCount; i++) {
        if (info->layers[i]->type != XR_TYPE_COMPOSITION_LAYER_QUAD) continue;
        auto* q = reinterpret_cast<const XrCompositionLayerQuad*>(info->layers[i]);
        gRecord.framesWithQuad++;
        auto it = gSpaces.find(q->space);
        if (it == gSpaces.end()) gRecord.quadSpace = "unknown";
        else if (it->second.kind != SpaceKind::Action) gRecord.quadSpace = it->second.kind == SpaceKind::Local ? "local" : "view";
        else gRecord.quadSpace = gActions[it->second.action].name + " " + gPathNames[it->second.subaction];
        gRecord.quadWidth = q->size.width;
        gRecord.quadHeight = q->size.height;
        gRecord.quadPose = q->pose;
        auto sc = gSwapchains.find(q->subImage.swapchain);
        if (sc != gSwapchains.end()) ReadBack(sc->second);
    }
    return XR_SUCCESS;
}

XrResult XRAPI_CALL GetInstanceProcAddr(XrInstance, const char* name, PFN_xrVoidFunction* fn) {
    static const std::map<std::string, PFN_xrVoidFunction> table = {
#define F(n, f) {n, reinterpret_cast<PFN_xrVoidFunction>(f)}
        F("xrGetInstanceProcAddr", GetInstanceProcAddr),
        F("xrEnumerateInstanceExtensionProperties", EnumerateInstanceExtensionProperties),
        F("xrCreateInstance", CreateInstance),
        F("xrDestroyInstance", DestroyInstance),
        F("xrGetInstanceProperties", GetInstanceProperties),
        F("xrGetSystem", GetSystem),
        F("xrGetSystemProperties", GetSystemProperties),
        F("xrGetD3D11GraphicsRequirementsKHR", GetD3D11Req),
        F("xrGetD3D12GraphicsRequirementsKHR", GetD3D12Req),
        F("xrStringToPath", StringToPath),
        F("xrPathToString", PathToString),
        F("xrCreateActionSet", CreateActionSet),
        F("xrDestroyActionSet", DestroyActionSet),
        F("xrCreateAction", CreateAction),
        F("xrDestroyAction", DestroyAction),
        F("xrSuggestInteractionProfileBindings", SuggestBindings),
        F("xrAttachSessionActionSets", AttachSets),
        F("xrSyncActions", SyncActions),
        F("xrGetActionStateBoolean", GetActionStateBoolean),
        F("xrApplyHapticFeedback", ApplyHaptic),
        F("xrGetActionStateFloat", GetActionStateFloat),
        F("xrCreateSession", CreateSession),
        F("xrDestroySession", DestroySession),
        F("xrBeginSession", BeginSession),
        F("xrEndSession", EndSession),
        F("xrPollEvent", PollEvent),
        F("xrWaitFrame", WaitFrame),
        F("xrBeginFrame", BeginFrame),
        F("xrEndFrame", EndFrame),
        F("xrCreateReferenceSpace", CreateReferenceSpace),
        F("xrCreateActionSpace", CreateActionSpace),
        F("xrDestroySpace", DestroySpace),
        F("xrLocateSpace", LocateSpace),
        F("xrEnumerateSwapchainFormats", EnumerateSwapchainFormats),
        F("xrCreateSwapchain", CreateSwapchain),
        F("xrDestroySwapchain", DestroySwapchain),
        F("xrEnumerateSwapchainImages", EnumerateSwapchainImages),
        F("xrAcquireSwapchainImage", AcquireImage),
        F("xrWaitSwapchainImage", WaitImage),
        F("xrReleaseSwapchainImage", ReleaseImage),
#undef F
    };
    auto it = table.find(name);
    if (it == table.end()) {
        *fn = nullptr;
        return XR_ERROR_FUNCTION_UNSUPPORTED;
    }
    *fn = it->second;
    return XR_SUCCESS;
}

}  // namespace

extern "C" __declspec(dllexport) XrResult XRAPI_CALL xrNegotiateLoaderRuntimeInterface(const XrNegotiateLoaderInfo*,
                                                                                        XrNegotiateRuntimeRequest* req) {
    req->runtimeInterfaceVersion = XR_CURRENT_LOADER_RUNTIME_VERSION;
    req->runtimeApiVersion = XR_CURRENT_API_VERSION;
    req->getInstanceProcAddr = GetInstanceProcAddr;
    return XR_SUCCESS;
}

// Test hooks, looked up by xrtest.exe with GetProcAddress.
extern "C" __declspec(dllexport) void MockReport(int* values) {
    values[0] = gRecord.suggestCalls;
    values[1] = gRecord.lastSuggestCount;
    values[2] = gRecord.attachSetCount;
    values[3] = gRecord.lastSyncSetCount;
    values[4] = gRecord.framesEnded;
    values[5] = gRecord.framesWithQuad;
    values[6] = gRecord.hapticCalls;
    values[7] = static_cast<int>(gRecord.pixelWidth);
    values[8] = static_cast<int>(gRecord.pixelHeight);
    values[9] = gRecord.rgba ? 1 : 0;
    values[10] = gRecord.clickReads;
    values[11] = gRecord.maxSyncSetCount;
}
extern "C" __declspec(dllexport) const char* MockQuadSpace() { return gRecord.quadSpace.c_str(); }
extern "C" __declspec(dllexport) void MockQuadGeometry(float* out) {
    out[0] = gRecord.quadWidth;
    out[1] = gRecord.quadHeight;
    memcpy(out + 2, &gRecord.quadPose, sizeof(XrPosef));
}
extern "C" __declspec(dllexport) const uint8_t* MockPixels() { return gRecord.pixels.data(); }
