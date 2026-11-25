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
    CrBridge_Error_InvalidState = -10
};

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_Initialize(void);
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceCount(void);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_GetDeviceDescriptor(
    int32_t index,
    char* utf8Json,
    int32_t capacity);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_Connect(
    int32_t index,
    uint32_t timeoutMs);
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_Disconnect(void);
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_StartLiveView(void);
SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_StopLiveView(void);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_GetLiveViewJpeg(
    uint8_t** data,
    uint32_t* size,
    uint32_t timeoutMs);
SONY_CR_BRIDGE_API void SONY_CR_BRIDGE_CALL CrBridge_FreeBuffer(void* buffer);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_TakePicture(uint32_t holdMs);

SONY_CR_BRIDGE_API int32_t SONY_CR_BRIDGE_CALL CrBridge_LastError(
    char* utf8Message,
    int32_t capacity);

#ifdef __cplusplus
}
#endif
