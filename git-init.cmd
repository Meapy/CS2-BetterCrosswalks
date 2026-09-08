@echo off
REM Creates the git repository for this mod and makes the first commit.
REM Run once, from this folder. Safe to delete afterwards.

cd /d "%~dp0"

if exist ".git" (
  echo A git repository already exists here. Nothing to do.
  exit /b 0
)

git init -b main || goto :error
git add -A || goto :error
git commit -m "Crosswalk Width: adjustable crosswalk width for Cities: Skylines II" || goto :error

echo.
echo Repository created. Add a remote with:
echo   git remote add origin ^<url^>
echo   git push -u origin main
exit /b 0

:error
echo.
echo Something went wrong - is git on your PATH?
exit /b 1
