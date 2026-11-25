# SonyCrBridge

Windows x64 C++17 bridge DLL for Sony Camera Remote SDK v2.02.00. The repository intentionally does not contain Sony SDK headers, import libraries, or runtime binaries; obtain and install the SDK under Sony's license.

## Build

Install the Sony Camera Remote SDK for Windows and extract its `RemoteCli` directory. Configure with Visual Studio 2022 x64 and CMake:

```powershell
cmake -S . -B build -A x64 -DCRSDK_ROOT="C:/SDK/CrSDK_v2.02.00/RemoteCli"
cmake --build build --config Release
```

`CRSDK_ROOT` must contain `app/CRSDK/CameraRemote_SDK.h` and `external/crsdk/Cr_Core.lib`. CMake links `Cr_Core.lib` and copies `Cr_Core.dll` plus `CrAdapter` beside the built DLL when those runtime files are present.

Output: `build/Release/SonyCrBridge.dll` with the multi-configuration Visual Studio generator.

## Export ABI

The C ABI and calling convention are declared in `include/SonyCrBridge.h`. All exports use `extern "C"` and `__cdecl`:

1. `CrBridge_Initialize`
2. `CrBridge_GetDeviceCount`
3. `CrBridge_GetDeviceDescriptor`
4. `CrBridge_Connect`
5. `CrBridge_Disconnect`
6. `CrBridge_StartLiveView`
7. `CrBridge_StopLiveView`
8. `CrBridge_GetLiveViewJpeg`
9. `CrBridge_FreeBuffer`
10. `CrBridge_TakePicture`
11. `CrBridge_LastError`

Operations return `CrBridge_Ok` (`0`) or a negative `CrBridgeResult`; `GetDeviceCount` returns the count or a negative error. Descriptor and error-text functions return the required UTF-8 byte count including the null terminator. Query the size with a null buffer and zero capacity, then call again with a sufficiently large buffer. A descriptor is JSON with name, model, connection, adapter, ID, IP, and MAC fields. A zero connect timeout uses 15 seconds; a zero live-view timeout uses 1 second; a zero shutter hold uses 100 ms.

The returned JPEG allocation must be released with `CrBridge_FreeBuffer` so allocation and deallocation stay in the bridge's CRT. `CrBridge_TakePicture` sends shutter-down, holds it, sends shutter-up, and returns after those SDK calls; it sends shutter-up even if shutter-down reports an error. Capture transfer completion is asynchronous and depends on camera transfer settings.

## Callback behavior

Sony's connection/disconnection calls are asynchronous. The bridge keeps its callback object alive and blocks `CrBridge_Connect` and `CrBridge_Disconnect` until `OnConnected` / `OnDisconnected`, or a bounded timeout/error. The callback's `OnError` is also signaled to the waiting operation, and operation-result callback payloads are released with the SDK API. Each export serializes SDK API calls to avoid concurrent camera-command races; callback handlers use a separate event mutex.

`Disconnect` tears down the device and SDK runtime after the disconnect callback; call `CrBridge_Initialize` again before starting another SDK session. Complete `Disconnect` successfully before unloading the bridge. Keep `Cr_Core.dll` and the SDK `CrAdapter` runtime directory alongside the consuming application (or in the SDK-prescribed load paths).
