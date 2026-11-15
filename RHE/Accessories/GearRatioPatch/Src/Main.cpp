#include "Hook.h"
#include "Log.h"
#include <windows.h>

// Attach runs before the game spawns its first vehicle, so the hook is in place before any transmission is built
// The module is pinned, so its only detach comes at process exit, when the game's code never runs again and nothing needs undoing
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        OpenLog();
        if (PinPluginModule(module)) InstallGearRatioHook();
    }
    return TRUE;
}
