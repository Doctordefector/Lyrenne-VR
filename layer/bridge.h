// Shared memory contract between LyrenneVR.exe (the host) and the OpenXR API layer.
// Mirrored in host/Bridge.cs. Change both together and bump kBridgeVersion.
#pragma once
#include <cstdint>

namespace lyrenne {

constexpr wchar_t kBridgeName[] = L"Local\\LyrenneVR.Bridge.v1";
constexpr uint32_t kBridgeMagic = 0x3152564C;  // "LVR1"
constexpr uint32_t kBridgeVersion = 1;
constexpr uint32_t kMaxWidth = 1024;
constexpr uint32_t kMaxHeight = 512;
constexpr uint32_t kPixelOffset = 512;
constexpr uint32_t kBridgeSize = kPixelOffset + kMaxWidth * kMaxHeight * 4;

// Same field order as XrPosef so it can be memcpy'd.
struct Pose {
    float qx, qy, qz, qw;
    float px, py, pz;
};

enum Anchor : uint32_t { AnchorLeftGrip = 0, AnchorRightGrip = 1, AnchorWorld = 2 };

enum PoseFlag : uint32_t {
    PoseHead = 1u << 0,
    PoseLeftGrip = 1u << 1,
    PoseRightGrip = 1u << 2,
    PoseLeftAim = 1u << 3,
    PoseRightAim = 1u << 4,
};

#pragma pack(push, 4)
struct Bridge {
    // Written by the host.
    uint32_t magic;            // 0
    uint32_t version;          // 4
    int64_t hostHeartbeatMs;   // 8   GetTickCount64 of the last host tick
    uint32_t frameSeq;         // 16  seqlock over the pixels: odd while the host is writing
    uint32_t width;            // 20
    uint32_t height;           // 24
    uint32_t visible;          // 28
    uint32_t anchor;           // 32  Anchor
    float panelWidthM;         // 36
    Pose panelPose;            // 40  relative to the anchor space
    uint32_t hapticSeq;        // 68
    uint32_t hapticHand;       // 72  0 left, 1 right
    float hapticAmplitude;     // 76
    float hapticDurationS;     // 80
    uint32_t reserved0;        // 84

    // Written by the layer. All poses are in the layer's LOCAL reference space.
    int64_t clientHeartbeatMs; // 88
    uint32_t clientPid;        // 96
    uint32_t poseFlags;        // 100 PoseFlag bits
    Pose head;                 // 104
    Pose leftGrip;             // 132
    Pose rightGrip;            // 160
    Pose leftAim;              // 188
    Pose rightAim;             // 216
    uint32_t poseSeq;          // 244
    char runtimeName[64];      // 248
    char appName[64];          // 312
    uint8_t reserved1[kPixelOffset - 376];
    // kPixelOffset: width * height BGRA, premultiplied alpha, tightly packed.
};
#pragma pack(pop)

static_assert(sizeof(Pose) == 28, "pose layout");
static_assert(sizeof(Bridge) == kPixelOffset, "bridge header layout");

}  // namespace lyrenne
