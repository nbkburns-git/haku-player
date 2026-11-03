@echo off
rem Build haku_sr.dll + hsr_test.exe (Release, x64) with the VS 2022 Build Tools, into native\haku_sr\out.
rem The TensorRT runtime DLLs it loads are copied next to it.
setlocal
set HERE=%~dp0
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set PATH=C:\Program Files\CMake\bin;%PATH%
cmake -S "%HERE%." -B "%HERE%build" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release >"%HERE%build.log" 2>&1 || (type "%HERE%build.log" & exit /b 1)
cmake --build "%HERE%build" >>"%HERE%build.log" 2>&1 || (type "%HERE%build.log" & exit /b 1)
if not exist "%HERE%out" mkdir "%HERE%out"
copy /y "%HERE%build\haku_sr.dll" "%HERE%out\" >nul
copy /y "%HERE%build\hsr_test.exe" "%HERE%out\" >nul
for %%f in (nvinfer_11.dll nvonnxparser_11.dll nvinfer_builder_resource_sm89_11.dll nvinfer_builder_resource_ptx_11.dll) do (
  if not exist "%HERE%out\%%f" copy /y "%HERE%..\..\deps\tensorrt\bin\%%f" "%HERE%out\" >nul
)
echo built: %HERE%out\haku_sr.dll
