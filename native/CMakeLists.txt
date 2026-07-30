cmake_minimum_required(VERSION 3.21)

project(FoxTransNative LANGUAGES C CXX)

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(FOXTRANS_NATIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/output")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${FOXTRANS_NATIVE_OUTPUT_DIRECTORY}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${FOXTRANS_NATIVE_OUTPUT_DIRECTORY}")
foreach(configuration DEBUG RELEASE RELWITHDEBINFO MINSIZEREL)
    set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_${configuration}
        "${FOXTRANS_NATIVE_OUTPUT_DIRECTORY}")
    set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${configuration}
        "${FOXTRANS_NATIVE_OUTPUT_DIRECTORY}")
endforeach()

file(GLOB FVAD_SIGNAL_PROCESSING_SOURCES
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/libfvad/src/signal_processing/*.c")
file(GLOB FVAD_VAD_SOURCES
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/libfvad/src/vad/*.c")

add_library(FoxTrans.Native SHARED
    foxtrans_native.c
    foxtrans_vr.c
    vendor/miniaudio/miniaudio.c
    vendor/libfvad/src/fvad.c
    ${FVAD_SIGNAL_PROCESSING_SOURCES}
    ${FVAD_VAD_SOURCES}
    vendor/openvr/src/openvr_api_public.cpp
    vendor/openvr/src/jsoncpp.cpp
    vendor/openvr/src/vrcore/dirtools_public.cpp
    vendor/openvr/src/vrcore/envvartools_public.cpp
    vendor/openvr/src/vrcore/pathtools_public.cpp
    vendor/openvr/src/vrcore/sharedlibtools_public.cpp
    vendor/openvr/src/vrcore/hmderrors_public.cpp
    vendor/openvr/src/vrcore/vrpathregistry_public.cpp
    vendor/openvr/src/vrcore/strtools_public.cpp)

target_include_directories(FoxTrans.Native PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/miniaudio"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/libfvad/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/libfvad/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/libfvad/src/signal_processing"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/libfvad/src/vad"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/openvr/headers"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/openvr/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/vendor/openvr/src/vrcore")

# FoxTrans uses only capture. Disabling miniaudio's unrelated decoders and
# engines keeps the bundled native library small and avoids extra dependencies.
target_compile_definitions(FoxTrans.Native PRIVATE
    MA_NO_DECODING
    MA_NO_ENCODING
    MA_NO_RESOURCE_MANAGER
    MA_NO_NODE_GRAPH
    MA_NO_ENGINE
    MA_ENABLE_ONLY_SPECIFIC_BACKENDS
    OPENVR_API_NODLL
    OPENVR_BUILD_STATIC
    VR_API_PUBLIC
    VRCORE_NO_PLATFORM)

if (WIN32)
    target_compile_definitions(FoxTrans.Native PRIVATE MA_ENABLE_WASAPI WIN64)
    target_link_libraries(FoxTrans.Native PRIVATE ole32 uuid avrt)
elseif (UNIX)
    target_compile_definitions(FoxTrans.Native PRIVATE
        MA_ENABLE_PULSEAUDIO
        MA_ENABLE_ALSA
        LINUX
        POSIX)
    if (CMAKE_SIZEOF_VOID_P EQUAL 8)
        target_compile_definitions(FoxTrans.Native PRIVATE LINUX64)
    endif()
    target_link_libraries(FoxTrans.Native PRIVATE ${CMAKE_DL_LIBS} pthread m)
endif()

set_target_properties(FoxTrans.Native PROPERTIES
    OUTPUT_NAME "FoxTrans.Native"
    C_STANDARD 11
    C_STANDARD_REQUIRED ON
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)
