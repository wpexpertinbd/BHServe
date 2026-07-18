@echo off
rem BHServe post-login ionCube heal.
rem The BHServeHeal Scheduled Task runs THIS wrapper at logon (+1 min). A direct-exe scheduled task
rem was observed to launch bhserve.exe but not execute its work in that context; a .cmd wrapper calling
rem the CLI is proven reliable. Loop __heal-php ~10 times over ~10 min so a boot-storm ionCube load
rem failure is detected + the workers respawned, even if the app's own in-process triggers don't fire.
rem %~dp0 = this file's dir = the BHServe install dir, so bhserve.exe is the sibling. `ping` is the
rem console-less-safe sleep (`timeout` needs a console and fails under a hidden Scheduled Task).
setlocal
for /L %%i in (1,1,10) do (
  "%~dp0bhserve.exe" __heal-php
  ping -n 61 127.0.0.1 >nul
)
