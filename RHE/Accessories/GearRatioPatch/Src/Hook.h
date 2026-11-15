#pragma once
#include <windows.h>

// Must run before any patch, since every patch jumps into this module. On a failed pin it logs one ERROR and returns False, and the caller installs nothing
bool PinPluginModule(HMODULE module);

// Finds the gear ratio function and redirects its entry through the correction. Every failure logs one ERROR and patches nothing
bool InstallGearRatioHook();

// Export, safe from any thread: 1 once the hook is installed, 0 otherwise. A consumer reads it to know whether ratios past 6 gears fall steadily
extern "C" __declspec(dllexport) int IsGearRatioPatchActive();
