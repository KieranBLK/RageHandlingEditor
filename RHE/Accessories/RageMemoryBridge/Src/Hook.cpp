#include "Hook.h"
#include "Capture.h"
#include "Log.h"
#include "Scan.h"
#include <windows.h>
#include <cstring>
#include <string>
#include <vector>

// The call to the vehicle model registration function, directly followed by two loads of the loader's own locals
// The exact pattern is tried first, then the loose one with the stack offset as a wildcard, since a recompile shifts that byte first
static const char* const ANCHOR_PATTERN = "E8 ?? ?? ?? ?? 48 8B 4D E0 48 8B 11";
static const char* const ANCHOR_PATTERN_LOOSE = "E8 ?? ?? ?? ?? 48 8B 4D ?? 48 8B 11";

// First layer: The model info Init call sits this far past the anchor, with exactly these bytes from the end of the anchor's call up to it
static const size_t INIT_SITE_OFFSET = 0x15;
static const char* const INIT_SITE_SEQUENCE = "48 8B 4D E0 48 8B 11 48 8B C8 4C 8B F0 49 03 D7 E8";

// Second layer: The first call within this many bytes of the anchor that follows a move of the registration result into the first argument
static const size_t INIT_SEARCH_WINDOW = 0x40;
static const char* const FIRST_ARGUMENT_FROM_RESULT = "48 8B C8";

// Every byte the site checks and the forward search read, counted from the anchor: Its call, then the search window
static const size_t INIT_READ_WINDOW = REL32_INSTRUCTION_SIZE + INIT_SEARCH_WINDOW;

// Enhanced builds the registration inline, so its patterns find the Init call itself: An add of the InitData array base, then the 2 argument moves and the call
// The exact pattern is tried first, then the loose one with the 2 stack offsets before it as wildcards, each with the call this far into it
static const char* const ENHANCED_SITE_PATTERN = "49 03 3E 48 89 D9 48 89 FA E8";
static const size_t ENHANCED_SITE_CALL_OFFSET = 9;
static const char* const ENHANCED_SITE_PATTERN_LOOSE = "8B 74 24 ?? 48 8D 6C 24 ?? 49 03 3E 48 89 D9 48 89 FA E8";
static const size_t ENHANCED_SITE_LOOSE_CALL_OFFSET = 0x12;

typedef void(__fastcall* ModelInfoInitFunction)(void* modelInfo, void* initData);

static ModelInfoInitFunction originalInit = nullptr;

// A failed capture costs only that model's record, and the game's own Init must always run after it
static void CaptureModelGuarded(const void* initData)
{
    try
    {
        CaptureModel(initData);
    }
    catch (...)
    {
        return;
    }
}

static void __fastcall InitHook(void* modelInfo, void* initData)
{
    CaptureModelGuarded(initData);
    originalInit(modelInfo, initData);
}

static uintptr_t FindAnchor(const char* exactPattern, const char* loosePattern)
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    std::vector<uintptr_t> matches = ScanModule(exactPattern);
    if (matches.size() == 1)
    {
        LogInfo(Dbg::AnchorFoundExact(matches[0], matches[0] - moduleBase));
        return matches[0];
    }
    std::vector<uintptr_t> looseMatches = ScanModule(loosePattern);
    if (looseMatches.size() == 1)
    {
        LogWarn(Dbg::AnchorFoundLoose(looseMatches[0], looseMatches[0] - moduleBase, matches.size()));
        return looseMatches[0];
    }
    LogError(Dbg::AnchorNotFound(matches.size(), looseMatches.size()));
    return 0;
}

// Writes the bytes over protected code, then puts the protection back and flushes the instruction cache. Returns False when the protection cannot be changed
static bool WriteCode(uint8_t* address, const void* bytes, size_t size)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(address, size, PAGE_EXECUTE_READWRITE, &oldProtect)) return false;
    memcpy(address, bytes, size);
    VirtualProtect(address, size, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), address, size);
    return true;
}

// Writes the stub's absolute jump and its target, which the jump reads through jmp qword ptr [rip + 2]
// The 2 bytes between the jump and its target are padding
static void WriteStubJump(uint8_t* stub, uintptr_t target)
{
    const uint8_t stubJump[] = { 0xFF, 0x25, 0x02, 0x00, 0x00, 0x00 };
    memcpy(stub, stubJump, sizeof(stubJump));
    memcpy(stub + STUB_TARGET_OFFSET, &target, sizeof(target));
}

// The stub page is allocated read and write, so it only becomes executable once everything in it is written
static bool MakeStubExecutable(uint8_t* stub)
{
    DWORD oldProtect = 0;
    if (!VirtualProtect(stub, STUB_SIZE, PAGE_EXECUTE_READ, &oldProtect)) return false;
    FlushInstructionCache(GetCurrentProcess(), stub, STUB_SIZE);
    return true;
}

bool PinPluginModule(HMODULE module)
{
    HMODULE pinned = nullptr;
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN, reinterpret_cast<LPCSTR>(module), &pinned)) return true;
    LogError(Dbg::ModulePinFailed());
    return false;
}

static uintptr_t ReadCallTarget(const uint8_t* site)
{
    return ReadRelativeTarget(reinterpret_cast<uintptr_t>(site) + REL32_DISPLACEMENT_OFFSET);
}

static uint8_t* SearchInitSite(uintptr_t anchor)
{
    const uint8_t* start = reinterpret_cast<const uint8_t*>(anchor + REL32_INSTRUCTION_SIZE);
    bool firstArgumentSet = false;
    for (size_t offset = 0; offset + REL32_INSTRUCTION_SIZE <= INIT_SEARCH_WINDOW; offset++)
    {
        if (offset >= 3 && MatchesPattern(start + offset - 3, FIRST_ARGUMENT_FROM_RESULT)) firstArgumentSet = true;
        if (start[offset] != CALL_OPCODE || !firstArgumentSet) continue;
        if (IsRegisteredFunctionStart(ReadCallTarget(start + offset))) return const_cast<uint8_t*>(start + offset);
    }
    return nullptr;
}

// Points the Init call at a stub that jumps to the capture, after both editions' paths have found and checked the site
// Logs the install block, with the site line the caller built, or one ERROR when no stub fits
static bool RedirectInitCall(uint8_t* site, const std::string& siteLine)
{
    uintptr_t target = ReadCallTarget(site);
    uintptr_t callEnd = reinterpret_cast<uintptr_t>(site) + REL32_INSTRUCTION_SIZE;
    uint8_t* stub = AllocateStubNear(reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr)), callEnd, STUB_SIZE);
    if (stub == nullptr)
    {
        LogError(Dbg::StubNotAllocated(reinterpret_cast<uintptr_t>(site)));
        return false;
    }
    int64_t distance = static_cast<int64_t>(reinterpret_cast<uintptr_t>(stub)) - static_cast<int64_t>(callEnd);
    if (distance < INT32_MIN || distance > INT32_MAX)
    {
        LogError(Dbg::StubOutOfReach(reinterpret_cast<uintptr_t>(stub), distance));
        VirtualFree(stub, 0, MEM_RELEASE);
        return false;
    }
    WriteStubJump(stub, reinterpret_cast<uintptr_t>(&InitHook));
    if (!MakeStubExecutable(stub))
    {
        LogError(Dbg::StubNotExecutable(reinterpret_cast<uintptr_t>(stub)));
        VirtualFree(stub, 0, MEM_RELEASE);
        return false;
    }
    originalInit = reinterpret_cast<ModelInfoInitFunction>(target);
    int32_t hookRelative = static_cast<int32_t>(distance);
    if (!WriteCode(site + REL32_DISPLACEMENT_OFFSET, &hookRelative, sizeof(int32_t)))
    {
        LogError(Dbg::InitSiteUnprotectFailed(reinterpret_cast<uintptr_t>(site)));
        VirtualFree(stub, 0, MEM_RELEASE);
        return false;
    }
    LogInfo(Dbg::InstallRoot());
    LogInfo(siteLine);
    LogInfo(Dbg::InstallOriginalInit(target));
    LogInfo(Dbg::InstallJumpStub(reinterpret_cast<uintptr_t>(stub)));
    return true;
}

// The site checks and the forward search read up to INIT_READ_WINDOW bytes from the anchor, so that whole window is confirmed readable first
static bool InstallInitHook(uintptr_t anchor)
{
    if (!IsReadableRange(anchor, INIT_READ_WINDOW))
    {
        LogError(Dbg::InitWindowUnreadable());
        return false;
    }
    uint8_t* site = nullptr;
    bool searched = false;
    if (MatchesPattern(reinterpret_cast<const uint8_t*>(anchor + REL32_INSTRUCTION_SIZE), INIT_SITE_SEQUENCE))
    {
        site = reinterpret_cast<uint8_t*>(anchor + INIT_SITE_OFFSET);
        uintptr_t exactTarget = ReadCallTarget(site);
        if (!IsRegisteredFunctionStart(exactTarget))
        {
            LogError(Dbg::InitTargetNotFunctionStart(exactTarget));
            return false;
        }
    }
    else
    {
        site = SearchInitSite(anchor);
        if (site == nullptr)
        {
            LogError(Dbg::InitSiteNotFound());
            return false;
        }
        searched = true;
        LogWarn(Dbg::InitSiteFoundBySearch(reinterpret_cast<uintptr_t>(site) - anchor));
    }
    return RedirectInitCall(site, Dbg::InstallSite(reinterpret_cast<uintptr_t>(site), searched, reinterpret_cast<uintptr_t>(site) - anchor));
}

// Each pattern must match exactly once, and the call it holds must reach a registered function start
static bool InstallEnhancedInitHook()
{
    uintptr_t moduleBase = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));
    std::vector<uintptr_t> matches = ScanModule(ENHANCED_SITE_PATTERN);
    std::vector<uintptr_t> looseMatches;
    uintptr_t site = 0;
    bool loose = false;
    if (matches.size() == 1) site = matches[0] + ENHANCED_SITE_CALL_OFFSET;
    else
    {
        looseMatches = ScanModule(ENHANCED_SITE_PATTERN_LOOSE);
        if (looseMatches.size() == 1)
        {
            site = looseMatches[0] + ENHANCED_SITE_LOOSE_CALL_OFFSET;
            loose = true;
        }
    }
    if (site == 0)
    {
        LogError(Dbg::SiteNotFound(matches.size(), looseMatches.size()));
        return false;
    }
    if (loose) LogWarn(Dbg::SiteFound(site, site - moduleBase, true, matches.size()));
    else LogInfo(Dbg::SiteFound(site, site - moduleBase, false, matches.size()));
    if (!IsReadableRange(site, REL32_INSTRUCTION_SIZE))
    {
        LogError(Dbg::InitWindowUnreadable());
        return false;
    }
    uint8_t* siteBytes = reinterpret_cast<uint8_t*>(site);
    uintptr_t target = ReadCallTarget(siteBytes);
    if (!IsRegisteredFunctionStart(target))
    {
        LogError(Dbg::InitTargetNotFunctionStart(target));
        return false;
    }
    return RedirectInitCall(siteBytes, Dbg::InstallSiteDirect(site, loose, site - moduleBase));
}

bool InstallModelCaptureHook()
{
    if (IsEnhancedEdition()) return InstallEnhancedInitHook();
    uintptr_t anchor = FindAnchor(ANCHOR_PATTERN, ANCHOR_PATTERN_LOOSE);
    return anchor != 0 && InstallInitHook(anchor);
}
