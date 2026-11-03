cmake_minimum_required(VERSION 3.24)
project(haku_sr CXX)

# CUDA and TensorRT come from ../../deps (see deps/README): CUDA 13.4 redistributables, TensorRT 11.4 headers + runtime DLLs.
set(DEPS ${CMAKE_CURRENT_SOURCE_DIR}/../../deps)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")   # static CRT: one less DLL to ship

add_library(haku_sr SHARED haku_sr.cpp haku_sr.h)
target_include_directories(haku_sr PRIVATE ${DEPS}/cuda/include ${DEPS}/tensorrt/include)
target_link_directories(haku_sr PRIVATE ${DEPS}/cuda/lib/x64)
target_link_libraries(haku_sr PRIVATE cudart_static d3d11 dxgi d3dcompiler)
target_compile_options(haku_sr PRIVATE /W3 /O2 /wd4996)

add_executable(hsr_test hsr_test.cpp)
target_link_libraries(hsr_test PRIVATE haku_sr d3d11)
