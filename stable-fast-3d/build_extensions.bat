@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" > C:\Users\Lakshya\Desktop\stable-fast-3d\build_log.txt 2>&1
cd /d C:\Users\Lakshya\Desktop\stable-fast-3d
echo === Building texture_baker === >> C:\Users\Lakshya\Desktop\stable-fast-3d\build_log.txt 2>&1
C:\Users\Lakshya\Desktop\stable-fast-3d\venv\Scripts\pip.exe install ./texture_baker/ >> C:\Users\Lakshya\Desktop\stable-fast-3d\build_log.txt 2>&1
echo === Building uv_unwrapper === >> C:\Users\Lakshya\Desktop\stable-fast-3d\build_log.txt 2>&1
C:\Users\Lakshya\Desktop\stable-fast-3d\venv\Scripts\pip.exe install ./uv_unwrapper/ >> C:\Users\Lakshya\Desktop\stable-fast-3d\build_log.txt 2>&1
echo === Done === >> C:\Users\Lakshya\Desktop\stable-fast-3d\build_log.txt 2>&1
