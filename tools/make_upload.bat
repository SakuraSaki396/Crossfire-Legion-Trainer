@echo off
chcp 65001 >nul
rem 双击本文件 = 重新生成 _upload 目录（只含该上传到 GitHub 的文件）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make_upload.ps1"
echo.
pause
