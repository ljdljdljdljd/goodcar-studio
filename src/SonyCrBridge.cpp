#include "SonyCrBridge.h"

#include <CameraRemote_SDK.h>
#include <IDeviceCallback.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iomanip>
#include <memory>
#include <mutex>
#include <new>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

// Windows-only: SetDllDirectory for loading Cr_Core.dll from sdkRoot
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

namespace
{
using namespace SCRSDK;

constexpr uint32_t kDefaultConnectTimeoutMs = 15000;
constexpr int32_t  kDefaultTakePictureTimeoutMs = 30000;
constexpr uint32_t kDefaultShutterHoldMs = 100;

class BridgeState;

class CameraCallback final : public IDeviceCallback
{
public:
    explicit CameraCallback(BridgeState& state) : state_(state) {}

    void OnConnected(DeviceConnectionVersioin version) override;
    void OnDisconnected(CrInt32u error) override;
    void OnPropertyChanged() override;
    void OnLvPropertyChanged() override;
    void OnError(CrInt32u error) override;
    void OnCompleteDownload(CrChar* filename, CrInt32u type = 0xFFFFFFFF) override;
    void OnCompleteOperation(CrInt32u code, CrOperationResultData* resultData) override;
    void OnWarning(CrInt32u warning) override;
    void OnPropertyChangedCodes(CrInt32u num, CrInt32u* codes) override;
    void OnLvPropertyChangedCodes(CrInt32u num, CrInt32u* codes) override;
    void OnNotifyContentsTransfer(CrInt32u notify, CrContentHandle handle, CrChar* filename = nullptr) override;
    void OnWarningExt(CrInt32u warning, CrInt32 param1, CrInt32 param2, CrInt32 param3) override;
    void OnNotifyFTPTransferResult(CrInt32u notify, CrInt32u numOfSuccess, CrInt32u numOfFail) override;
    void OnNotifyRemoteTransferResult(CrInt32u notify, CrInt32u per, CrChar* filename) override;
    void OnNotifyRemoteTransferResult(CrInt32u notify, CrInt32u per, CrInt8u* data, CrInt64u size) override;
    void OnNotifyRemoteTransferContentsListChanged(CrInt32u notify, CrInt32u slotNumber, CrInt32u addSize) override;
    void OnNotifyRemoteFirmwareUpdateResult(CrInt32u notify, const void* param) override;
    void OnReceivePlaybackTimeCode(CrInt32u timeCode) override;
    void OnReceivePlaybackData(CrInt8u mediaType, CrInt32 dataSize, CrInt8u* data,
                              CrInt64 pts, CrInt64 dts, CrInt32 param1, CrInt32 param2) override;
    void OnNotifyMonitorUpdated(CrInt32u type, CrInt32u frameNo) override;
    void OnNotifyPostViewImage(CrChar* filename, CrInt32u size) override;

private:
    BridgeState& state_;
};

class BridgeState
{
public:
    BridgeState() : callback(*this) {}

    std::mutex apiMutex;
    std::mutex eventMutex;
    std::condition_variable eventChanged;
    std::mutex errorMutex;
    bool initialized = false;
    bool connectPending = false;
    bool connected = false;
    bool disconnected = false;
    CrInt32u callbackError = 0;
    CrDeviceHandle deviceHandle = 0;
    ICrEnumCameraObjectInfo* devices = nullptr;

    // Live view internal buffer (bridge-owned, returned by pointer to caller — no free needed)
    std::vector<uint8_t> liveViewBuffer;

    // Download notification: last downloaded filename
    std::string lastDownloadedFile;
    bool downloadReady = false;

    CameraCallback callback;
    std::string lastError;

    void SetError(const std::string& message)
    {
        std::lock_guard<std::mutex> lock(errorMutex);
        lastError = message;
    }

    void ClearError()
    {
        SetError({});
    }

    std::string GetError()
    {
        std::lock_guard<std::mutex> lock(errorMutex);
        return lastError;
    }

    bool RefreshDevices()
    {
        if (deviceHandle != 0)
        {
            return devices != nullptr;
        }

        if (devices != nullptr)
        {
            devices->Release();
            devices = nullptr;
        }

        const CrError result = EnumCameraObjects(&devices, 3);
        if (result != CrError_None)
        {
            SetSdkError("EnumCameraObjects", result);
            return false;
        }
        return true;
    }

    void SetSdkError(const char* operation, CrError result)
    {
        std::ostringstream message;
        message << operation << " failed (CrError=0x" << std::hex
                << static_cast<uint32_t>(result) << ").";
        SetError(message.str());
    }

    bool IsConnected()
    {
        std::lock_guard<std::mutex> lock(eventMutex);
        return connected;
    }

    void NotifyConnected()
    {
        {
            std::lock_guard<std::mutex> lock(eventMutex);
            connected = true;
            connectPending = false;
            callbackError = 0;
            disconnected = false;
        }
        eventChanged.notify_all();
    }

    void NotifyDisconnected(CrInt32u error)
    {
        {
            std::lock_guard<std::mutex> lock(eventMutex);
            connected = false;
            connectPending = false;
            disconnected = true;
            callbackError = error;
        }
        if (error != 0)
        {
            std::ostringstream message;
            message << "Sony SDK disconnected with error 0x" << std::hex << error << '.';
            SetError(message.str());
        }
        eventChanged.notify_all();
    }

    void NotifyError(CrInt32u error)
    {
        {
            std::lock_guard<std::mutex> lock(eventMutex);
            callbackError = error;
            connectPending = false;
        }
        std::ostringstream message;
        message << "Sony SDK callback reported error 0x" << std::hex << error << '.';
        SetError(message.str());
        eventChanged.notify_all();
    }

    void NotifyDownload(CrChar* filename, CrInt32u type)
    {
        static_cast<void>(type);
        {
            std::lock_guard<std::mutex> lock(eventMutex);
            if (filename != nullptr)
            {
                lastDownloadedFile = filename;
            }
            downloadReady = true;
        }
        eventChanged.notify_all();
    }

    void NotifyEvent()
    {
        eventChanged.notify_all();
    }
};

BridgeState& State()
{
    static BridgeState state;
    return state;
}

void CameraCallback::OnConnected(DeviceConnectionVersioin)
{
    try { state_.NotifyConnected(); } catch (...) {}
}

void CameraCallback::OnDisconnected(CrInt32u error)
{
    try { state_.NotifyDisconnected(error); } catch (...) {}
}

void CameraCallback::OnPropertyChanged()
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnLvPropertyChanged()
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnError(CrInt32u error)
{
    try { state_.NotifyError(error); } catch (...) {}
}

void CameraCallback::OnCompleteDownload(CrChar* filename, CrInt32u type)
{
    try { state_.NotifyDownload(filename, type); } catch (...) {}
}

void CameraCallback::OnCompleteOperation(CrInt32u, CrOperationResultData* resultData)
{
    try
    {
        CrDeviceHandle handle = 0;
        {
            std::lock_guard<std::mutex> lock(state_.eventMutex);
            handle = state_.deviceHandle;
        }
        if (resultData != nullptr && handle != 0)
        {
            SCRSDK::ReleaseOperationResultObject(handle, resultData);
        }
    }
    catch (...) {}
}

void CameraCallback::OnWarning(CrInt32u) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnPropertyChangedCodes(CrInt32u, CrInt32u*) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnLvPropertyChangedCodes(CrInt32u, CrInt32u*) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyContentsTransfer(CrInt32u, CrContentHandle, CrChar*) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnWarningExt(CrInt32u, CrInt32, CrInt32, CrInt32) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyFTPTransferResult(CrInt32u, CrInt32u, CrInt32u) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyRemoteTransferResult(CrInt32u, CrInt32u, CrChar*) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyRemoteTransferResult(CrInt32u, CrInt32u, CrInt8u*, CrInt64u) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyRemoteTransferContentsListChanged(CrInt32u, CrInt32u, CrInt32u) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyRemoteFirmwareUpdateResult(CrInt32u, const void*) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnReceivePlaybackTimeCode(CrInt32u) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnReceivePlaybackData(CrInt8u, CrInt32, CrInt8u*, CrInt64, CrInt64, CrInt32, CrInt32) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyMonitorUpdated(CrInt32u, CrInt32u) { try { state_.NotifyEvent(); } catch (...) {} }
void CameraCallback::OnNotifyPostViewImage(CrChar*, CrInt32u) { try { state_.NotifyEvent(); } catch (...) {} }

// ── helpers ──────────────────────────────────────────────────────────────────

std::string JsonEscape(const char* value)
{
    if (value == nullptr) return {};
    std::ostringstream escaped;
    for (const unsigned char c : std::string(value))
    {
        switch (c)
        {
        case '"':  escaped << "\\\""; break;
        case '\\': escaped << "\\\\"; break;
        case '\b': escaped << "\\b";  break;
        case '\f': escaped << "\\f";  break;
        case '\n': escaped << "\\n";  break;
        case '\r': escaped << "\\r";  break;
        case '\t': escaped << "\\t";  break;
        default:
            if (c < 0x20)
                escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                        << static_cast<unsigned int>(c);
            else
                escaped << static_cast<char>(c);
            break;
        }
    }
    return escaped.str();
}

std::string HexId(const ICrCameraObjectInfo* device)
{
    if (device == nullptr || device->GetId() == nullptr) return {};
    const CrInt8u* id = device->GetId();
    const CrInt32u idSize = device->GetIdSize();
    std::ostringstream output;
    output << std::hex << std::setfill('0');
    for (CrInt32u i = 0; i < idSize; ++i)
        output << std::setw(2) << static_cast<unsigned int>(id[i]);
    return output.str();
}

std::string DeviceDescriptor(const ICrCameraObjectInfo* device)
{
    std::ostringstream d;
    d << "{\"name\":\"" << JsonEscape(device->GetName())
      << "\",\"model\":\"" << JsonEscape(device->GetModel())
      << "\",\"connectionType\":\"" << JsonEscape(device->GetConnectionTypeName())
      << "\",\"adapter\":\"" << JsonEscape(device->GetAdaptorName())
      << "\",\"idHex\":\"" << HexId(device)
      << "\",\"ipAddress\":\"" << JsonEscape(device->GetIPAddressChar())
      << "\",\"macAddress\":\"" << JsonEscape(device->GetMACAddressChar())
      << "\"}";
    return d.str();
}

int32_t CopyUtf8String(const std::string& value, char* buffer, int32_t capacity)
{
    if (capacity < 0 || (buffer == nullptr && capacity != 0))
        return CrBridge_Error_InvalidArgument;
    if (value.size() >= static_cast<size_t>(INT32_MAX))
        return CrBridge_Error_Internal;
    const auto required = static_cast<int32_t>(value.size() + 1);
    if (buffer != nullptr && capacity >= required)
        std::memcpy(buffer, value.c_str(), static_cast<size_t>(required));
    return required;
}

int32_t CatchException(const char* operation)
{
    try { throw; }
    catch (const std::bad_alloc&)
    {
        State().SetError(std::string(operation) + " failed: out of memory.");
        return CrBridge_Error_OutOfMemory;
    }
    catch (const std::exception& ex)
    {
        State().SetError(std::string(operation) + " failed: " + ex.what());
        return CrBridge_Error_Internal;
    }
    catch (...)
    {
        State().SetError(std::string(operation) + " failed with an unknown exception.");
        return CrBridge_Error_Internal;
    }
}

// Find a camera by matching idHex in the descriptor JSON.
// Returns the index into state.devices, or -1 if not found.
int32_t FindDeviceByDescriptor(BridgeState& state, const char* descriptorJson)
{
    if (state.devices == nullptr) return -1;
    if (descriptorJson == nullptr || descriptorJson[0] == '\0') return -1;

    // Extract "idHex":"..." from the descriptor
    std::string needle(descriptorJson);
    const std::string key = "\"idHex\":\"";
    size_t pos = needle.find(key);
    std::string searchId;
    if (pos != std::string::npos)
    {
        pos += key.size();
        size_t end = needle.find('"', pos);
        if (end != std::string::npos)
            searchId = needle.substr(pos, end - pos);
    }

    for (CrInt32u i = 0; i < state.devices->GetCount(); ++i)
    {
        const auto* dev = state.devices->GetCameraObjectInfo(i);
        if (dev == nullptr) continue;
        if (!searchId.empty() && HexId(dev) == searchId) return static_cast<int32_t>(i);
    }

    // Fallback: first device (single-camera setup)
    return state.devices->GetCount() > 0 ? 0 : -1;
}

} // anonymous namespace

// ── exported ABI (matches Huateng.Turntable.Sdk.Internal.SonyDevice) ─────────

extern "C"
{

// CrBridge_Initialize(string sdkRoot)
// sdkRoot: directory containing Cr_Core.dll.  NULL/empty → use current working directory.
int32_t SONY_CR_BRIDGE_CALL CrBridge_Initialize(const char* sdkRoot)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (state.initialized)
    {
        state.ClearError();
        return CrBridge_Ok;
    }

    try
    {
#ifdef _WIN32
        // Point the DLL loader at sdkRoot so it can find Cr_Core.dll and CrAdapter.
        if (sdkRoot != nullptr && sdkRoot[0] != '\0')
        {
            // Convert UTF-8 to wide string for SetDllDirectoryW
            const int wLen = MultiByteToWideChar(CP_UTF8, 0, sdkRoot, -1, nullptr, 0);
            if (wLen > 0)
            {
                std::vector<wchar_t> wPath(static_cast<size_t>(wLen));
                MultiByteToWideChar(CP_UTF8, 0, sdkRoot, -1, wPath.data(), wLen);
                SetDllDirectoryW(wPath.data());
            }
        }
#endif

        if (!SCRSDK::Init(0))
        {
            state.SetError("SCRSDK::Init(0) failed.");
            return CrBridge_Error_Sdk;
        }
        state.initialized = true;
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...)
    {
        return CatchException("CrBridge_Initialize");
    }
}

// CrBridge_GetDeviceDescriptor(int index, char* buffer, int bufferSize)
// Two-pass: call with buffer=NULL,bufferSize=0 to get required size; then call again with buffer.
int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceDescriptor(int32_t index, char* buffer, int32_t bufferSize)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }

    try
    {
        if (!state.RefreshDevices())
            return CrBridge_Error_Sdk;
        if (state.devices == nullptr || static_cast<CrInt32u>(index) >= state.devices->GetCount())
        {
            state.SetError("Device index is out of range.");
            return CrBridge_Error_InvalidIndex;
        }
        const auto* device = state.devices->GetCameraObjectInfo(static_cast<CrInt32u>(index));
        if (device == nullptr)
        {
            state.SetError("Sony SDK returned a null device descriptor.");
            return CrBridge_Error_Sdk;
        }
        const int32_t required = CopyUtf8String(DeviceDescriptor(device), buffer, bufferSize);
        state.ClearError();
        return required;
    }
    catch (...) { return CatchException("CrBridge_GetDeviceDescriptor"); }
}

// CrBridge_Connect(string descriptorJson, int interfaceType)
// descriptorJson: JSON from GetDeviceDescriptor.  interfaceType: 0=USB, 1=WiredLan, 2=WiFi.
int32_t SONY_CR_BRIDGE_CALL CrBridge_Connect(const char* descriptorJson, int32_t interfaceType)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }

    bool connectionBusy = false;
    {
        std::lock_guard<std::mutex> eventLock(state.eventMutex);
        connectionBusy = state.deviceHandle != 0 || state.connectPending || state.connected;
    }
    if (connectionBusy)
    {
        state.SetError("A camera connection is already active or pending.");
        return CrBridge_Error_Busy;
    }

    try
    {
        if (!state.RefreshDevices())
            return CrBridge_Error_Sdk;

        const int32_t devIndex = FindDeviceByDescriptor(state, descriptorJson);
        if (devIndex < 0)
        {
            state.SetError("No matching camera found for the given descriptor.");
            return CrBridge_Error_InvalidIndex;
        }

        const auto* device = state.devices->GetCameraObjectInfo(static_cast<CrInt32u>(devIndex));
        if (device == nullptr)
        {
            state.SetError("Sony SDK returned a null device object.");
            return CrBridge_Error_Sdk;
        }

        static_cast<void>(interfaceType); // SDK selects interface from device descriptor

        CrDeviceHandle pendingHandle = 0;
        // SCRSDK::Connect takes non-const ICrCameraObjectInfo*; use const_cast since
        // GetCameraObjectInfo returns const but the SDK signature requires non-const.
        SCRSDK::ICrCameraObjectInfo* deviceMutable = const_cast<SCRSDK::ICrCameraObjectInfo*>(
            state.devices->GetCameraObjectInfo(static_cast<CrInt32u>(devIndex)));
        const CrError result = SCRSDK::Connect(deviceMutable, &state.callback, &pendingHandle);
        if (result != CrError_None)
        {
            state.SetSdkError("SCRSDK::Connect", result);
            return CrBridge_Error_Sdk;
        }

        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            state.connectPending = true;
            state.deviceHandle = pendingHandle;
        }

        // Wait for OnConnected callback
        const auto deadline = std::chrono::steady_clock::now()
                            + std::chrono::milliseconds(kDefaultConnectTimeoutMs);
        {
            std::unique_lock<std::mutex> eventLock(state.eventMutex);
            const bool ok = state.eventChanged.wait_until(eventLock, deadline, [&state] {
                return state.connected || state.disconnected || state.callbackError != 0;
            });
            if (!ok || !state.connected)
            {
                SCRSDK::Disconnect(pendingHandle);
                SCRSDK::ReleaseDevice(pendingHandle);
                state.deviceHandle = 0;
                state.connectPending = false;
                state.connected = false;
                if (state.lastError.empty())
                    state.SetError("Timed out waiting for camera connection.");
                return CrBridge_Error_Timeout;
            }
        }

        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...) { return CatchException("CrBridge_Connect"); }
}

// CrBridge_Disconnect()
int32_t SONY_CR_BRIDGE_CALL CrBridge_Disconnect(void)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);

    CrDeviceHandle handle = 0;
    {
        std::lock_guard<std::mutex> eventLock(state.eventMutex);
        handle = state.deviceHandle;
        state.deviceHandle = 0;
        state.connected = false;
        state.connectPending = false;
    }

    if (handle != 0)
    {
        SCRSDK::Disconnect(handle);
        SCRSDK::ReleaseDevice(handle);
    }

    if (state.initialized)
    {
        state.initialized = false;
        if (!SCRSDK::Release())
        {
            state.SetError("SCRSDK::Release() failed.");
            return CrBridge_Error_Sdk;
        }
    }

    state.ClearError();
    return CrBridge_Ok;
}

// CrBridge_GetLiveViewJpeg(uint8_t** data, int32_t* length)
// *data points into an internal bridge-owned buffer.  Do NOT free it.
// Valid only until the next GetLiveViewJpeg call.
int32_t SONY_CR_BRIDGE_CALL CrBridge_GetLiveViewJpeg(uint8_t** data, int32_t* length)
{
    if (data == nullptr || length == nullptr)
        return CrBridge_Error_InvalidArgument;

    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }
    if (state.deviceHandle == 0 || !state.IsConnected())
    {
        state.SetError("No connected camera.");
        return CrBridge_Error_NotConnected;
    }

    try
    {
        CrImageInfo imageInfo{};
        CrError result = SCRSDK::GetLiveViewImageInfo(state.deviceHandle, &imageInfo);
        if (result != CrError_None)
        {
            state.SetSdkError("GetLiveViewImageInfo", result);
            return CrBridge_Error_Sdk;
        }

        const CrInt32u bufferSize = imageInfo.GetBufferSize();
        if (bufferSize == 0)
        {
            state.SetError("Sony SDK reported an empty Live View buffer.");
            return CrBridge_Error_Sdk;
        }

        if (state.liveViewBuffer.size() < bufferSize)
            state.liveViewBuffer.resize(bufferSize);

        CrImageDataBlock imageBlock{};
        imageBlock.SetSize(static_cast<CrInt32u>(state.liveViewBuffer.size()));
        imageBlock.SetData(state.liveViewBuffer.data());

        result = SCRSDK::GetLiveViewImage(state.deviceHandle, &imageBlock);
        if (result != CrError_None)
        {
            state.SetSdkError("GetLiveViewImage", result);
            return CrBridge_Error_Sdk;
        }

        const CrInt32u imageSize = imageBlock.GetImageSize();
        if (imageSize == 0 || imageBlock.GetImageData() == nullptr)
        {
            state.SetError("Sony SDK returned an empty Live View JPEG.");
            return CrBridge_Error_Sdk;
        }

        *data   = static_cast<uint8_t*>(imageBlock.GetImageData());
        *length = static_cast<int32_t>(imageSize);
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...) { return CatchException("CrBridge_GetLiveViewJpeg"); }
}

// CrBridge_SetSaveInfo(string path, string prefix, int startNo)
// Sets the PC-side save folder so Sony SDK downloads images after shutter.
// Must be called after Connect and before TakePicture.
int32_t SONY_CR_BRIDGE_CALL CrBridge_SetSaveInfo(const char* path, const char* prefix, int32_t startNo)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }
    if (state.deviceHandle == 0 || !state.IsConnected())
    {
        state.SetError("No connected camera.");
        return CrBridge_Error_NotConnected;
    }
    if (path == nullptr || path[0] == '\0')
    {
        state.SetError("path must not be empty.");
        return CrBridge_Error_InvalidArgument;
    }

    try
    {
        // SCRSDK::SetSaveInfo takes non-const CrChar* (char*) for path and prefix
        const CrError result = SCRSDK::SetSaveInfo(
            state.deviceHandle,
            const_cast<CrChar*>(path),
            prefix != nullptr ? const_cast<CrChar*>(prefix) : const_cast<CrChar*>("Shot"),
            startNo);
        if (result != CrError_None)
        {
            state.SetSdkError("SetSaveInfo", result);
            return CrBridge_Error_Sdk;
        }
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...) { return CatchException("CrBridge_SetSaveInfo"); }
}

// CrBridge_TakePicture(string targetPath, int timeoutMs)
// Triggers shutter and waits for the downloaded file to be renamed to targetPath.
// targetPath: full destination path (UTF-8).  timeoutMs=0 → 30 s default.
int32_t SONY_CR_BRIDGE_CALL CrBridge_TakePicture(const char* targetPath, int32_t timeoutMs)
{
    if (targetPath == nullptr || targetPath[0] == '\0')
        return CrBridge_Error_InvalidArgument;

    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }
    if (state.deviceHandle == 0 || !state.IsConnected())
    {
        state.SetError("No connected camera.");
        return CrBridge_Error_NotConnected;
    }

    try
    {
        const int32_t waitMs = timeoutMs > 0 ? timeoutMs : kDefaultTakePictureTimeoutMs;

        // Reset download flag before triggering
        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            state.downloadReady = false;
            state.lastDownloadedFile.clear();
        }

        const CrDeviceHandle handle = state.deviceHandle;

        // Shutter Down
        const CrError downResult = SCRSDK::SendCommand(handle, CrCommandId_Release, CrCommandParam_Down);

        // RAII guard: always send Shutter Up
        struct ShutterGuard
        {
            CrDeviceHandle h; bool armed;
            ShutterGuard(CrDeviceHandle h_, bool ok) : h(h_), armed(ok) {}
            ~ShutterGuard() { if (armed && h != 0) { try { SCRSDK::SendCommand(h, CrCommandId_Release, CrCommandParam_Up); } catch (...) {} } }
            void disarm() { armed = false; }
        } shutterGuard(handle, downResult == CrError_None);

        if (downResult != CrError_None)
        {
            state.SetSdkError("SendCommand(shutter down)", downResult);
            return CrBridge_Error_Sdk;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(kDefaultShutterHoldMs));

        const CrError upResult = SCRSDK::SendCommand(handle, CrCommandId_Release, CrCommandParam_Up);
        shutterGuard.disarm();

        if (upResult != CrError_None)
        {
            state.SetSdkError("SendCommand(shutter up)", upResult);
            return CrBridge_Error_Sdk;
        }

        // Wait for OnCompleteDownload callback
        const auto deadline = std::chrono::steady_clock::now()
                            + std::chrono::milliseconds(waitMs);
        std::string downloadedPath;
        {
            std::unique_lock<std::mutex> eventLock(state.eventMutex);
            const bool ok = state.eventChanged.wait_until(eventLock, deadline,
                [&state] { return state.downloadReady; });
            if (!ok)
            {
                state.SetError("Timed out waiting for image download after shutter.");
                return CrBridge_Error_Timeout;
            }
            downloadedPath = state.lastDownloadedFile;
        }

        // Move/rename the downloaded file to targetPath if paths differ
        if (!downloadedPath.empty() && downloadedPath != targetPath)
        {
#ifdef _WIN32
            // Convert UTF-8 paths to wide for MoveFileExW
            auto toWide = [](const std::string& s) -> std::wstring {
                const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
                if (n <= 0) return {};
                std::wstring w(static_cast<size_t>(n), L'\0');
                MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), n);
                return w;
            };
            MoveFileExW(toWide(downloadedPath).c_str(), toWide(targetPath).c_str(),
                        MOVEFILE_REPLACE_EXISTING);
#else
            std::rename(downloadedPath.c_str(), targetPath);
#endif
        }

        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...) { return CatchException("CrBridge_TakePicture"); }
}

// CrBridge_LastError(char* utf8Message, int capacity)
int32_t SONY_CR_BRIDGE_CALL CrBridge_LastError(char* utf8Message, int32_t capacity)
{
    try { return CopyUtf8String(State().GetError(), utf8Message, capacity); }
    catch (...) { return CatchException("CrBridge_LastError"); }
}

} // extern "C"
