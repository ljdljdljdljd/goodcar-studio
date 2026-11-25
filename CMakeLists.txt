cmake_minimum_required(VERSION 3.21)

project(SonyCrBridge LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "SonyCrBridge.dll must be built with the Windows x64 toolchain.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "SonyCrBridge requires a 64-bit Windows toolchain.")
endif()

# SDK 头文件和库已内嵌在 external/ 目录，无需用户设置 CRSDK_ROOT
# 若用户已有本地 SDK 安装，可通过 -DCRSDK_ROOT=<path> 覆盖
set(CRSDK_ROOT "" CACHE PATH "Optional: override path to Sony CrSDK RemoteCli directory")

set(_BUNDLED_INCLUDE "${CMAKE_CURRENT_SOURCE_DIR}/external/crsdk/CRSDK")
set(_BUNDLED_LIB     "${CMAKE_CURRENT_SOURCE_DIR}/external/crsdk/Cr_Core.lib")

if(CRSDK_ROOT)
    find_path(CRSDK_INCLUDE_DIR
        NAMES CameraRemote_SDK.h
        HINTS "${CRSDK_ROOT}/app/CRSDK" "${CRSDK_ROOT}/include"
        PATH_SUFFIXES CRSDK
    )
    find_library(CRSDK_CORE_LIBRARY
        NAMES Cr_Core
        HINTS "${CRSDK_ROOT}/external/crsdk" "${CRSDK_ROOT}/lib"
    )
else()
    # 使用内嵌 SDK
    if(NOT EXISTS "${_BUNDLED_INCLUDE}/CameraRemote_SDK.h")
        message(FATAL_ERROR "Bundled Sony SDK headers not found at ${_BUNDLED_INCLUDE}. Run: git submodule update --init or set -DCRSDK_ROOT=<path>")
    endif()
    set(CRSDK_INCLUDE_DIR "${_BUNDLED_INCLUDE}")
    set(CRSDK_CORE_LIBRARY "${_BUNDLED_LIB}")
endif()

if(NOT CRSDK_INCLUDE_DIR OR NOT CRSDK_CORE_LIBRARY)
    message(FATAL_ERROR "Sony Camera Remote SDK not found. Set -DCRSDK_ROOT=<RemoteCli path>.")
endif()

get_filename_component(CRSDK_LIBRARY_DIR "${CRSDK_CORE_LIBRARY}" DIRECTORY)

add_library(SonyCrBridge SHARED
    src/SonyCrBridge.cpp
    include/SonyCrBridge.h
)

target_compile_features(SonyCrBridge PRIVATE cxx_std_17)
target_include_directories(SonyCrBridge
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE "${CRSDK_INCLUDE_DIR}"
)
target_link_libraries(SonyCrBridge PRIVATE "${CRSDK_CORE_LIBRARY}")
target_compile_definitions(SonyCrBridge PRIVATE
    SONY_CR_BRIDGE_BUILD
    WIN32_LEAN_AND_MEAN
    NOMINMAX
)
set_target_properties(SonyCrBridge PROPERTIES
    OUTPUT_NAME SonyCrBridge
    CXX_EXTENSIONS OFF
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

set(CRSDK_CORE_DLL "${CRSDK_LIBRARY_DIR}/Cr_Core.dll")
if(EXISTS "${CRSDK_CORE_DLL}")
    add_custom_command(TARGET SonyCrBridge POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "${CRSDK_CORE_DLL}"
            "$<TARGET_FILE_DIR:SonyCrBridge>/Cr_Core.dll"
        VERBATIM
    )
endif()

set(CRSDK_ADAPTER_DIR "${CRSDK_LIBRARY_DIR}/CrAdapter")
if(IS_DIRECTORY "${CRSDK_ADAPTER_DIR}")
    add_custom_command(TARGET SonyCrBridge POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${CRSDK_ADAPTER_DIR}"
            "$<TARGET_FILE_DIR:SonyCrBridge>/CrAdapter"
        VERBATIM
    )
endif()
