@echo off
setlocal enabledelayedexpansion

echo Starting upload of markdown files...
set /a count=0
set /a success=0

for /r "C:\Blog\mostlylucidweb\Mostlylucid\Markdown" %%f in (*.md) do (
    set /a count+=1
    echo Uploading [!count!]: %%~nxf
    curl.exe -k -s -X POST "https://localhost:5020/api/documents/upload" -F "file=@%%f" -o nul
    if !errorlevel! equ 0 (
        set /a success+=1
    ) else (
        echo FAILED: %%~nxf
    )
)

echo.
echo Done! Processed !count! files, !success! uploaded successfully
