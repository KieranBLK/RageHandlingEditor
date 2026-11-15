#pragma once
#include <windows.h>

// Must run before any patch, since every patch jumps into this module. On a failed pin it logs one ERROR and returns False, and the caller installs nothing
bool PinPluginModule(HMODULE module);

// Redirects the model info Init call through the capture, leaving the game untouched on any failed step
// Each edition has its own patterns: One finds the registration call before Init
// Enhanced builds that registration inline, so its patterns find the Init call itself
bool InstallModelCaptureHook();
