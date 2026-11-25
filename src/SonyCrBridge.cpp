#include "SonyCrBridge.h"

#include <CameraRemote_SDK.h>

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

namespace
{
using namespace SCRSDK;

constexpr uint32_t kDefaultConnectTimeoutMs = 15000;
constexpr uint32_t kDefaultLiveViewTimeoutMs = 1000;
constexpr uint32_t kDefaultShutterHoldMs = 100;
constexpr uint32_t kMaximumShutterHoldMs = 10000;

class BridgeState;

class CameraCallback final : public IDeviceCallback
{
public:
    explicit CameraCallback(BridgeState& state) : state_(state) {}

    void OnConnected(DeviceConnectionVersion version) override;
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
    bool liveView = false;
    bool connectPending = false;
    bool connected = false;
    bool disconnected = false;
    CrInt32u callbackError = 0;
    CrDeviceHandle deviceHandle = 0;
    ICrEnumCameraObjectInfo* devices = nullptr;
    std::unique_ptr<CrImageDataBlock> liveViewImage;
    std::vector<CrInt8u> liveViewBuffer;
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
        static_cast<void>(filename);
        static_cast<void>(type);
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

void CameraCallback::OnConnected(DeviceConnectionVersion)
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

void CameraCallback::OnWarning(CrInt32u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnPropertyChangedCodes(CrInt32u, CrInt32u*)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnLvPropertyChangedCodes(CrInt32u, CrInt32u*)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyContentsTransfer(CrInt32u, CrContentHandle, CrChar*)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnWarningExt(CrInt32u, CrInt32, CrInt32, CrInt32)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyFTPTransferResult(CrInt32u, CrInt32u, CrInt32u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyRemoteTransferResult(CrInt32u, CrInt32u, CrChar*)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyRemoteTransferResult(CrInt32u, CrInt32u, CrInt8u*, CrInt64u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyRemoteTransferContentsListChanged(CrInt32u, CrInt32u, CrInt32u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyRemoteFirmwareUpdateResult(CrInt32u, const void*)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnReceivePlaybackTimeCode(CrInt32u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnReceivePlaybackData(CrInt8u, CrInt32, CrInt8u*, CrInt64, CrInt64, CrInt32, CrInt32)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyMonitorUpdated(CrInt32u, CrInt32u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

void CameraCallback::OnNotifyPostViewImage(CrChar*, CrInt32u)
{
    try { state_.NotifyEvent(); } catch (...) {}
}

std::string JsonEscape(const char* value)
{
    if (value == nullptr)
    {
        return {};
    }

    std::ostringstream escaped;
    for (const unsigned char character : std::string(value))
    {
        switch (character)
        {
        case '"': escaped << "\\\""; break;
        case '\\': escaped << "\\\\"; break;
        case '\b': escaped << "\\b"; break;
        case '\f': escaped << "\\f"; break;
        case '\n': escaped << "\\n"; break;
        case '\r': escaped << "\\r"; break;
        case '\t': escaped << "\\t"; break;
        default:
            if (character < 0x20)
            {
                escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                        << static_cast<unsigned int>(character);
            }
            else
            {
                escaped << static_cast<char>(character);
            }
            break;
        }
    }
    return escaped.str();
}

std::string HexId(const ICrCameraObjectInfo* device)
{
    if (device == nullptr || device->GetId() == nullptr)
    {
        return {};
    }

    const CrInt8u* id = device->GetId();
    const CrInt32u idSize = device->GetIdSize();
    std::ostringstream output;
    output << std::hex << std::setfill('0');
    for (CrInt32u index = 0; index < idSize; ++index)
    {
        output << std::setw(2) << static_cast<unsigned int>(id[index]);
    }
    return output.str();
}

std::string DeviceDescriptor(const ICrCameraObjectInfo* device)
{
    std::ostringstream descriptor;
    descriptor << "{\"name\":\"" << JsonEscape(device->GetName())
               << "\",\"model\":\"" << JsonEscape(device->GetModel())
               << "\",\"connectionType\":\"" << JsonEscape(device->GetConnectionTypeName())
               << "\",\"adapter\":\"" << JsonEscape(device->GetAdaptorName())
               << "\",\"idHex\":\"" << HexId(device)
               << "\",\"ipAddress\":\"" << JsonEscape(device->GetIPAddressChar())
               << "\",\"macAddress\":\"" << JsonEscape(device->GetMACAddressChar())
               << "\"}";
    return descriptor.str();
}

int32_t CopyUtf8String(const std::string& value, char* buffer, int32_t capacity)
{
    if (capacity < 0 || (buffer == nullptr && capacity != 0))
    {
        return CrBridge_Error_InvalidArgument;
    }
    if (value.size() >= static_cast<size_t>(INT32_MAX))
    {
        return CrBridge_Error_Internal;
    }

    const auto required = static_cast<int32_t>(value.size() + 1);
    if (buffer != nullptr && capacity >= required)
    {
        std::memcpy(buffer, value.c_str(), static_cast<size_t>(required));
    }
    return required;
}

int32_t CatchException(const char* operation)
{
    try
    {
        throw;
    }
    catch (const std::bad_alloc&)
    {
        State().SetError(std::string(operation) + " failed: out of memory.");
        return CrBridge_Error_OutOfMemory;
    }
    catch (const std::exception& exception)
    {
        State().SetError(std::string(operation) + " failed: " + exception.what());
        return CrBridge_Error_Internal;
    }
    catch (...)
    {
        State().SetError(std::string(operation) + " failed with an unknown exception.");
        return CrBridge_Error_Internal;
    }
}
}

extern "C"
{
int32_t SONY_CR_BRIDGE_CALL CrBridge_Initialize(void)
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

int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceCount(void)
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
        {
            return CrBridge_Error_Sdk;
        }
        state.ClearError();
        return state.devices == nullptr ? 0 : static_cast<int32_t>(state.devices->GetCount());
    }
    catch (...)
    {
        return CatchException("CrBridge_GetDeviceCount");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceDescriptor(int32_t index, char* utf8Json, int32_t capacity)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }
    if (index < 0 || capacity < 0 || (utf8Json == nullptr && capacity != 0))
    {
        state.SetError("Invalid device index or output buffer.");
        return CrBridge_Error_InvalidArgument;
    }

    try
    {
        if (!state.RefreshDevices())
        {
            return CrBridge_Error_Sdk;
        }
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
        const int32_t required = CopyUtf8String(DeviceDescriptor(device), utf8Json, capacity);
        state.ClearError();
        return required;
    }
    catch (...)
    {
        return CatchException("CrBridge_GetDeviceDescriptor");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_Connect(int32_t index, uint32_t timeoutMs)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }
    if (index < 0)
    {
        state.SetError("Device index must not be negative.");
        return CrBridge_Error_InvalidArgument;
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
        {
            return CrBridge_Error_Sdk;
        }
        if (state.devices == nullptr || static_cast<CrInt32u>(index) >= state.devices->GetCount())
        {
            state.SetError("Device index is out of range.");
            return CrBridge_Error_InvalidIndex;
        }

        const ICrCameraObjectInfo* device = state.devices->GetCameraObjectInfo(static_cast<CrInt32u>(index));
        if (device == nullptr)
        {
            state.SetError("Sony SDK returned a null device descriptor.");
            return CrBridge_Error_Sdk;
        }

        CrDeviceHandle newHandle = 0;
        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            state.connectPending = true;
            state.connected = false;
            state.disconnected = false;
            state.callbackError = 0;
            state.deviceHandle = 0;
        }

        const CrError result = SCRSDK::Connect(
            const_cast<ICrCameraObjectInfo*>(device),
            &state.callback,
            &newHandle,
            CrSdkControlMode_Remote,
            CrReconnecting_ON);
        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            state.deviceHandle = newHandle;
        }
        if (result != CrError_None)
        {
            {
                std::lock_guard<std::mutex> eventLock(state.eventMutex);
                state.connectPending = false;
            }
            state.SetSdkError("Connect", result);
            return CrBridge_Error_Sdk;
        }

        const auto waitMs = timeoutMs == 0 ? kDefaultConnectTimeoutMs : timeoutMs;
        std::unique_lock<std::mutex> eventLock(state.eventMutex);
        const bool eventReceived = state.eventChanged.wait_for(
            eventLock,
            std::chrono::milliseconds(waitMs),
            [&state] { return state.connected || state.disconnected || state.callbackError != 0; });
        if (!eventReceived)
        {
            // FIX 1: 超时必须清除 connectPending，防止 Disconnect 二次死锁
            state.connectPending = false;
            eventLock.unlock();
            // 尽力向 SDK 发起取消连接，忽略返回值（句柄可能尚未就绪）
            const CrDeviceHandle pendingHandle = state.deviceHandle;
            if (pendingHandle != 0)
            {
                SCRSDK::Disconnect(pendingHandle);
                SCRSDK::ReleaseDevice(pendingHandle);
                std::lock_guard<std::mutex> hLock(state.eventMutex);
                state.deviceHandle = 0;
            }
            state.SetError("Timed out waiting for Sony SDK OnConnected callback.");
            return CrBridge_Error_Timeout;
        }
        if (!state.connected)
        {
            const CrInt32u callbackError = state.callbackError;
            eventLock.unlock();
            std::ostringstream message;
            message << "Sony SDK connection callback reported error 0x" << std::hex << callbackError << '.';
            state.SetError(message.str());
            return CrBridge_Error_Sdk;
        }
        eventLock.unlock();
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...)
    {
        return CatchException("CrBridge_Connect");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_Disconnect(void)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (!state.initialized)
    {
        state.SetError("Call CrBridge_Initialize before using the bridge.");
        return CrBridge_Error_NotInitialized;
    }
    {
        std::unique_lock<std::mutex> eventLock(state.eventMutex);
        if (state.connectPending && !state.eventChanged.wait_for(
                eventLock,
                std::chrono::milliseconds(kDefaultConnectTimeoutMs),
                [&state] { return !state.connectPending; }))
        {
            state.SetError("Timed out waiting for the pending Sony SDK connection callback.");
            return CrBridge_Error_Timeout;
        }
    }

    if (state.deviceHandle == 0)
    {
        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            if (state.connected)
            {
                state.SetError("SDK reported a connected camera without a valid device handle.");
                return CrBridge_Error_Internal;
            }
        }
        try
        {
            if (state.devices != nullptr)
            {
                state.devices->Release();
                state.devices = nullptr;
            }
            if (state.initialized)
            {
                if (!SCRSDK::Release())
                {
                    state.SetError("SCRSDK::Release() failed.");
                    return CrBridge_Error_Sdk;
                }
                state.initialized = false;
            }
        }
        catch (...)
        {
            return CatchException("CrBridge_Disconnect");
        }
        state.ClearError();
        return CrBridge_Ok;
    }

    try
    {
        if (state.liveView)
        {
            const CrError stopResult = SCRSDK::StopLiveView(state.deviceHandle);
            state.liveView = false;
            if (stopResult != CrError_None)
            {
                state.SetSdkError("StopLiveView", stopResult);
            }
        }

        bool alreadyDisconnected = false;
        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            alreadyDisconnected = state.disconnected;
            if (!alreadyDisconnected)
            {
                state.disconnected = false;
            }
        }
        if (!alreadyDisconnected)
        {
            const CrError result = SCRSDK::Disconnect(state.deviceHandle);
            if (result != CrError_None)
            {
                std::lock_guard<std::mutex> eventLock(state.eventMutex);
                if (!state.disconnected)
                {
                    state.SetSdkError("Disconnect", result);
                    return CrBridge_Error_Sdk;
                }
            }
        }

        // FIX 2: 等待断连回调，超时后不提前 return，继续走幂等清理
        bool disconnectTimedOut = false;
        {
            std::unique_lock<std::mutex> eventLock(state.eventMutex);
            if (!state.eventChanged.wait_for(
                    eventLock,
                    std::chrono::milliseconds(kDefaultConnectTimeoutMs),
                    [&state] { return state.disconnected; }))
            {
                disconnectTimedOut = true;
                state.SetError("Timed out waiting for Sony SDK OnDisconnected callback.");
                // 不 return — 继续执行幂等资源释放
            }
        }

        // 幂等 Teardown：无论超时与否，以下清理必须 100% 执行
        const CrDeviceHandle handle = state.deviceHandle;
        CrError releaseResult = CrError_None;
        if (handle != 0)
        {
            releaseResult = SCRSDK::ReleaseDevice(handle);
        }
        {
            std::lock_guard<std::mutex> eventLock(state.eventMutex);
            state.deviceHandle = 0;
            state.connected = false;
            state.disconnected = false;
        }
        state.liveView = false;
        state.liveViewImage.reset();
        state.liveViewBuffer.clear();

        if (state.devices != nullptr)
        {
            state.devices->Release();
            state.devices = nullptr;
        }

        const bool releaseSucceeded = SCRSDK::Release();
        state.initialized = false;

        // 超时优先上报，其次 SDK release 错误
        if (disconnectTimedOut)
        {
            return CrBridge_Error_Timeout;
        }
        if (releaseResult != CrError_None)
        {
            state.SetSdkError("ReleaseDevice", releaseResult);
            return CrBridge_Error_Sdk;
        }
        if (!releaseSucceeded)
        {
            state.SetError("SCRSDK::Release() failed.");
            return CrBridge_Error_Sdk;
        }
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...)
    {
        return CatchException("CrBridge_Disconnect");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_StartLiveView(void)
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
    if (state.liveView)
    {
        state.ClearError();
        return CrBridge_Ok;
    }

    try
    {
        const CrError result = SCRSDK::StartLiveView(state.deviceHandle);
        if (result != CrError_None)
        {
            state.SetSdkError("StartLiveView", result);
            return CrBridge_Error_Sdk;
        }
        state.liveViewImage = std::make_unique<CrImageDataBlock>();
        state.liveViewBuffer.clear();
        state.liveView = true;
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...)
    {
        return CatchException("CrBridge_StartLiveView");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_StopLiveView(void)
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
    if (!state.liveView)
    {
        state.ClearError();
        return CrBridge_Ok;
    }

    try
    {
        const CrError result = SCRSDK::StopLiveView(state.deviceHandle);
        if (result != CrError_None)
        {
            state.SetSdkError("StopLiveView", result);
            return CrBridge_Error_Sdk;
        }
        state.liveView = false;
        state.liveViewImage.reset();
        state.liveViewBuffer.clear();
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...)
    {
        return CatchException("CrBridge_StopLiveView");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_GetLiveViewJpeg(uint8_t** data, uint32_t* size, uint32_t timeoutMs)
{
    auto& state = State();
    std::lock_guard<std::mutex> apiLock(state.apiMutex);
    if (data == nullptr || size == nullptr)
    {
        state.SetError("JPEG output pointers must not be null.");
        return CrBridge_Error_InvalidArgument;
    }
    *data = nullptr;
    *size = 0;
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
    if (!state.liveView)
    {
        state.SetError("Live View has not been started.");
        return CrBridge_Error_InvalidState;
    }

    try
    {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(
            timeoutMs == 0 ? kDefaultLiveViewTimeoutMs : timeoutMs);
        do
        {
            CrImageInfo imageInfo;
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

            if (!state.liveViewImage)
            {
                state.liveViewImage = std::make_unique<CrImageDataBlock>();
            }
            if (state.liveViewBuffer.size() < bufferSize)
            {
                state.liveViewBuffer.resize(bufferSize);
            }
            state.liveViewImage->SetSize(static_cast<CrInt32u>(state.liveViewBuffer.size()));
            state.liveViewImage->SetData(state.liveViewBuffer.data());
            result = SCRSDK::GetLiveViewImage(state.deviceHandle, state.liveViewImage.get());
            if (result == CrError_None)
            {
                const CrInt32u imageSize = state.liveViewImage->GetImageSize();
                if (imageSize == 0 || state.liveViewImage->GetImageData() == nullptr)
                {
                    state.SetError("Sony SDK returned an empty Live View JPEG.");
                    return CrBridge_Error_Sdk;
                }

                auto* output = static_cast<uint8_t*>(std::malloc(imageSize));
                if (output == nullptr)
                {
                    state.SetError("Could not allocate the Live View JPEG buffer.");
                    return CrBridge_Error_OutOfMemory;
                }
                std::memcpy(output, state.liveViewImage->GetImageData(), imageSize);
                *data = output;
                *size = imageSize;
                state.ClearError();
                return CrBridge_Ok;
            }

            if (result != CrWarning_Frame_NotUpdated && result != CrError_Memory_Insufficient)
            {
                state.SetSdkError("GetLiveViewImage", result);
                return CrBridge_Error_Sdk;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        } while (std::chrono::steady_clock::now() < deadline);

        state.SetError("Timed out waiting for a Live View JPEG frame.");
        return CrBridge_Error_Timeout;
    }
    catch (...)
    {
        return CatchException("CrBridge_GetLiveViewJpeg");
    }
}

void SONY_CR_BRIDGE_CALL CrBridge_FreeBuffer(void* buffer)
{
    std::free(buffer);
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_TakePicture(uint32_t holdMs)
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

    try
    {
        const CrInt32u duration = holdMs == 0
            ? kDefaultShutterHoldMs
            : std::min(holdMs, kMaximumShutterHoldMs);
        const CrError downResult = SCRSDK::SendCommand(
            state.deviceHandle, CrCommandId_Release, CrCommandParam_Down);

        // FIX 3: RAII 快门守卫 — 无论后续如何退出，Shutter Up 必须执行
        // 防止快门按下后因异常或提前 return 导致相机长期锁死
        struct ShutterGuard
        {
            CrDeviceHandle handle;
            bool armed;
            ShutterGuard(CrDeviceHandle h, bool ok) : handle(h), armed(ok) {}
            ~ShutterGuard()
            {
                if (armed && handle != 0)
                {
                    try { SCRSDK::SendCommand(handle, CrCommandId_Release, CrCommandParam_Up); }
                    catch (...) {}
                }
            }
            void disarm() { armed = false; }
        } shutterGuard(state.deviceHandle, downResult == CrError_None);

        if (downResult == CrError_None)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(duration));
        }
        const CrError upResult = SCRSDK::SendCommand(
            state.deviceHandle, CrCommandId_Release, CrCommandParam_Up);
        // FIX 3b: 手动 Up 已发，守卫解除；但只在 Down 成功时才解除
        // （若 Down 失败，守卫本就未 armed，disarm 是 no-op，逻辑一致）
        shutterGuard.disarm();

        if (downResult != CrError_None)
        {
            state.SetSdkError("SendCommand(shutter down)", downResult);
            return CrBridge_Error_Sdk;
        }
        if (upResult != CrError_None)
        {
            state.SetSdkError("SendCommand(shutter up)", upResult);
            return CrBridge_Error_Sdk;
        }
        state.ClearError();
        return CrBridge_Ok;
    }
    catch (...)
    {
        return CatchException("CrBridge_TakePicture");
    }
}

int32_t SONY_CR_BRIDGE_CALL CrBridge_LastError(char* utf8Message, int32_t capacity)
{
    try
    {
        return CopyUtf8String(State().GetError(), utf8Message, capacity);
    }
    catch (...)
    {
        return CatchException("CrBridge_LastError");
    }
}
}
