#pragma once

#include <stdint.h>

#ifdef _WIN32
#ifdef SONY_CR_BRIDGE_BUILD
#define SONY_CR_BRIDGE_API __declspec(dllexport)
#else
#define SONY_CR_BRIDGE_API __declspec(dllimport)
#endif
#define SONY_CR_BRIDGE_CALL __cdecl
#else
#define SONY_CR_BRIDGE_API
#define SONY_CR_BRIDGE_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum CrBridgeResult
{
    CrBridge_Ok = 0,
    CrBridge_Error_InvalidArgument = -1,
    CrBridge_Error_NotInitialized = -2,
    CrBridge_Error_NotConnected = -3,
    CrBridge_Error_Sdk = -4,
    CrBridge_Error_Timeout = -5,
    CrBridge_Error_InvalidIndex = -6,
    CrBridge_Error_OutOfMemory = -7,
    CrBridge_Error_Internal = -8,
    CrBridge_Error_Busy = -9,
    CrBridge_Error_InvalidState = -10,
    CrBridge_Error_Canceled = -11,
    CrBridge_Error_InvalidFile = -12
};

/*
 * ABI CONTRACT — matches Huateng.Turntable.Sdk.Internal.SonyDevice P/Invoke declarations exactly.
 *
 * CrBridge_Initialize  : sdkRoot = directory containing Cr_Core.dll (UTF-8).
 *                        May be NULL or empty → use current working directory.
 * CrBridge_GetDeviceDescriptor : returns required buffer length (>0) or negative error.
 *                        buffer/bufferSize follow the same two-pass pattern as Win32.
 * CrBridge_Connect     : descriptor = JSON string from GetDeviceDescriptor.
 *                        interfaceType: 0=USB, 1=WiredLan, 2=WiFi.
 * CrBridge_GetLiveViewJpeg : data points into an internal bridge-owned buffer valid until
 *                        the next GetLiveViewJpeg call on the same thread or that thread's exit;
 *                        copy on the calling thread and do NOT free it.
 *                        length receives the JPEG byte count (int32).
 * CrBridge_TakePicture : targetPath = full path where the captured JPEG should be saved.
 *                        timeoutMs  = max wait for file download (0 → 30 000 ms default).
 */

/* Initialise the Sony Camera Remote SDK. Must be called once before all other functions. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_Initialize(const char* sdkRoot);

/* Returns the number of bytes required to hold the descriptor JSON (including NUL),
   or a negative CrBridgeResult error code. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceDescriptor(
    int32_t index,
    char*   buffer,
    int32_t bufferSize);

/* Connect to the camera identified by the descriptor JSON (obtained from GetDeviceDescriptor).
   interfaceType: 0=USB, 1=WiredLan, 2=WiFi. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_Connect(
    const char* descriptorJson,
    int32_t     interfaceType);

/* Disconnect and release all SDK resources. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_Disconnect(void);

/* Get one Live View JPEG frame.  *data points into an internal buffer (do NOT free).
   *length receives the JPEG byte count. Returns CrBridge_Ok or a negative error. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_GetLiveViewJpeg(
    uint8_t** data,
    int32_t*  length);

/* Trigger the shutter and wait for the image file to be downloaded to targetPath.
   targetPath = full output path (UTF-8).  timeoutMs = 0 → 30 000 ms default. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_TakePicture(
    const char* targetPath,
    int32_t     timeoutMs);

typedef int32_t (SONY_CR_BRIDGE_CALL *CrBridgeShouldCancel)(void);

/* The optional cancellation callback is synchronous and must not call into the bridge. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_TakePictureEx(
    const char* targetPath,
    int32_t timeoutMs,
    CrBridgeShouldCancel shouldCancel);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceCount(void);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_StartLiveView(void);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_StopLiveView(void);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_SetSaveInfo(
    const char* path,
    const char* prefix,
    int32_t     startNo);

/* Read the last error message (UTF-8).  Two-pass: call with buffer=NULL to get required size. */
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_LastError(
    char*   utf8Message,
    int32_t capacity);

#ifdef __cplusplus
}
#endif
