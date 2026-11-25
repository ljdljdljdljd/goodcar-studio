cmake_minimum_required(VERSION 3.21)

project(SonyCrBridge LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "SonyCrBridge.dll must be built with the Windows x64 toolchain.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "SonyCrBridge requires a 64-bit Windows toolchain.")
endif()

set(CRSDK_ROOT "" CACHE PATH "Path to the extracted Sony Camera Remote SDK RemoteCli directory")

if(NOT CRSDK_ROOT)
    message(FATAL_ERROR "Set CRSDK_ROOT to the RemoteCli directory from Sony CrSDK_v2.02.00.")
endif()

find_path(CRSDK_INCLUDE_DIR
    NAMES CameraRemote_SDK.h
    HINTS
        "${CRSDK_ROOT}/app/CRSDK"
        "${CRSDK_ROOT}/include"
    PATH_SUFFIXES CRSDK
)

find_library(CRSDK_CORE_LIBRARY
    NAMES Cr_Core
    HINTS
        "${CRSDK_ROOT}/external/crsdk"
        "${CRSDK_ROOT}/lib"
)

if(NOT CRSDK_INCLUDE_DIR OR NOT CRSDK_CORE_LIBRARY)
    message(FATAL_ERROR
        "Sony Camera Remote SDK was not found. Set CRSDK_ROOT to the extracted RemoteCli directory.\n"
        "Expected headers under app/CRSDK and Cr_Core.lib under external/crsdk.\n"
        "Download CrSDK_v2.02.00 from Sony and accept its license separately."
    )
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
