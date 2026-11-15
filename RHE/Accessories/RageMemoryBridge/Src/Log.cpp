#include "Log.h"
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <mutex>

// The project passes the editor script's ScriptVersion in from its Core.cs at build time, so the plugin always reports the version it shipped with
#ifndef PLUGIN_VERSION
#error PLUGIN_VERSION is not defined. The project file reads it from RageHandlingEditor's Core.cs
#endif
#define STRINGIZE_VALUE(value) #value
#define STRINGIZE(value) STRINGIZE_VALUE(value)
static_assert(sizeof(STRINGIZE(PLUGIN_VERSION)) > 1, "PLUGIN_VERSION is empty, so the project file could not read ScriptVersion from RageHandlingEditor's Core.cs");

// The log's file name in the game folder, beside the plugin, and the name its LOADED line shows
static const char* const LOG_FILE_NAME = "RageMemoryBridge.log";
static const char* const PLUGIN_NAME = "RageMemoryBridge";

// The Enhanced game executable's file name, which tells the 2 editions apart, and the hook library module whose version the header shows
static const char* const ENHANCED_EXECUTABLE_NAME = "GTA5_Enhanced.exe";
static const char* const SHV_MODULE_NAME = "ScriptHookV.dll";

// Every module's version resource has this id, and its fixed file info opens with this signature
static const WORD VERSION_RESOURCE_ID = 1;
static const DWORD FIXED_FILE_INFO_SIGNATURE = 0xFEEF04BD;

// The text a version reads as when its module or version resource cannot be read
static const char* const UNKNOWN_VERSION = "unknown";

static std::mutex logMutex;
static std::string logPath;

static std::string GetGameExecutablePath()
{
    char modulePath[MAX_PATH] = {};
    GetModuleFileNameA(nullptr, modulePath, MAX_PATH);
    return modulePath;
}

std::string GetGameFolder()
{
    std::string folder = GetGameExecutablePath();
    size_t lastSlash = folder.find_last_of("\\/");
    if (lastSlash != std::string::npos) folder.resize(lastSlash + 1);
    return folder;
}

bool IsEnhancedEdition()
{
    std::string path = GetGameExecutablePath();
    size_t lastSlash = path.find_last_of("\\/");
    std::string fileName = lastSlash == std::string::npos ? path : path.substr(lastSlash + 1);
    return _stricmp(fileName.c_str(), ENHANCED_EXECUTABLE_NAME) == 0;
}

static std::string GetGameEdition() { return IsEnhancedEdition() ? "Enhanced" : "Legacy"; }

// Reads a loaded module's file version straight from its own version resource, which uses only kernel32 and loads no library
// The version library would load a DLL, which the attach path must not do. Any failure reads unknown
static std::string ReadModuleFileVersion(HMODULE module)
{
    if (module == nullptr) return UNKNOWN_VERSION;
    HRSRC resource = FindResourceA(module, MAKEINTRESOURCEA(VERSION_RESOURCE_ID), RT_VERSION);
    if (resource == nullptr) return UNKNOWN_VERSION;
    DWORD size = SizeofResource(module, resource);
    HGLOBAL loaded = LoadResource(module, resource);
    const uint8_t* data = loaded != nullptr ? static_cast<const uint8_t*>(LockResource(loaded)) : nullptr;
    if (data == nullptr || size < sizeof(VS_FIXEDFILEINFO)) return UNKNOWN_VERSION;
    // The fixed file info sits on a 4 byte boundary after the resource's own header and key, so the first aligned signature marks it
    for (DWORD offset = 0; offset + sizeof(VS_FIXEDFILEINFO) <= size; offset += sizeof(DWORD))
    {
        VS_FIXEDFILEINFO info;
        memcpy(&info, data + offset, sizeof(info));
        if (info.dwSignature != FIXED_FILE_INFO_SIGNATURE) continue;
        char version[64];
        snprintf(version, sizeof(version), "%u.%u.%u.%u", HIWORD(info.dwFileVersionMS), LOWORD(info.dwFileVersionMS), HIWORD(info.dwFileVersionLS), LOWORD(info.dwFileVersionLS));
        return version;
    }
    return UNKNOWN_VERSION;
}

std::string GetGameFileVersion() { return ReadModuleFileVersion(GetModuleHandleA(nullptr)); }

// One 12 hour clock format, so the header time and every line's prefix always read alike
static std::string FormatClockTime(const SYSTEMTIME& time)
{
    int hour = time.wHour % 12 == 0 ? 12 : time.wHour % 12;
    char text[16];
    snprintf(text, sizeof(text), "%d:%02d:%02d%s", hour, time.wMinute, time.wSecond, time.wHour < 12 ? "AM" : "PM");
    return text;
}

static std::string FormatHeaderTime()
{
    SYSTEMTIME time;
    GetLocalTime(&time);
    char date[16];
    snprintf(date, sizeof(date), "%d/%02d/%02d ", time.wMonth, time.wDay, time.wYear % 100);
    return date + FormatClockTime(time);
}

// Appends lines exactly as given, so the header lines carry no timestamp or label of their own
static void AppendLines(const std::string& text)
{
    std::lock_guard<std::mutex> lock(logMutex);
    FILE* file = nullptr;
    if (fopen_s(&file, logPath.c_str(), "a") != 0 || file == nullptr) return;
    fputs(text.c_str(), file);
    fclose(file);
}

static void WriteLine(const char* level, const std::string& message)
{
    SYSTEMTIME time;
    GetLocalTime(&time);
    AppendLines("[" + FormatClockTime(time) + "] [" + level + "] -- " + message + "\n");
}

void OpenLog()
{
    logPath = GetGameFolder() + LOG_FILE_NAME;
    {
        std::lock_guard<std::mutex> lock(logMutex);
        FILE* file = nullptr;
        if (fopen_s(&file, logPath.c_str(), "w") == 0 && file != nullptr) fclose(file);
    }
    std::string loadedLine = std::string("[LOADED] -- ") + PLUGIN_NAME + " v" + STRINGIZE(PLUGIN_VERSION);
    std::string infoLine = "[INFO] -- | GameVersion: " + GetGameEdition() + " v" + GetGameFileVersion() + " | " + FormatHeaderTime();
    std::string hookVersionsLine = "[INFO] -- | SHV: v" + ReadModuleFileVersion(GetModuleHandleA(SHV_MODULE_NAME));
    std::string separator(infoLine.size() > hookVersionsLine.size() ? infoLine.size() : hookVersionsLine.size(), '-');
    AppendLines(loadedLine + "\n" + hookVersionsLine + "\n" + infoLine + "\n" + separator + "\n");
}

void LogInfo(const std::string& message) { WriteLine("INFO", message); }
void LogWarn(const std::string& message) { WriteLine("WARN", message); }
void LogError(const std::string& message) { WriteLine("ERROR", message); }

namespace Dbg
{
static std::string Hex(uint64_t value)
{
    char text[24];
    snprintf(text, sizeof(text), "0x%llX", static_cast<unsigned long long>(value));
    return text;
}

static std::string Hash(uint32_t value)
{
    char text[16];
    snprintf(text, sizeof(text), "0x%08X", value);
    return text;
}
// --Plugin--
std::string ModulePinFailed() { return std::string("PLUGIN: ") + PLUGIN_NAME + " | Module pin failed | Nothing installed"; }
// --Hook--
std::string AnchorFoundExact(uintptr_t address, uintptr_t moduleOffset) { return "HOOK: Anchor | Found | Pattern=Exact | Address=" + Hex(address) + " | ModuleOffset=" + Hex(moduleOffset); }
std::string AnchorFoundLoose(uintptr_t address, uintptr_t moduleOffset, size_t exactMatches) { return "HOOK: Anchor | Found | Pattern=Loose | Address=" + Hex(address) + " | ModuleOffset=" + Hex(moduleOffset) + " | ExactMatches=" + std::to_string(exactMatches); }
std::string AnchorNotFound(size_t exactMatches, size_t looseMatches) { return "HOOK: Anchor | Not found, expected 1 match | ExactMatches=" + std::to_string(exactMatches) + " | LooseMatches=" + std::to_string(looseMatches) + " | Nothing patched"; }
std::string InitWindowUnreadable() { return "HOOK: Init site | Unreadable after the anchor | Nothing patched"; }
std::string InitTargetNotFunctionStart(uintptr_t target) { return "HOOK: Init site | Target is not a function start | Target=" + Hex(target) + " | Nothing patched"; }
std::string InitSiteFoundBySearch(uintptr_t anchorOffset) { return "HOOK: Init site | Found by search | AnchorOffset=" + Hex(anchorOffset); }
std::string InitSiteNotFound() { return "HOOK: Init site | Not found by search | Nothing patched"; }
std::string StubNotAllocated(uintptr_t site) { return "HOOK: Jump stub | No free memory in reach | Site=" + Hex(site) + " | Nothing patched"; }
std::string StubOutOfReach(uintptr_t stub, int64_t distance) { return "HOOK: Jump stub | Out of rel32 reach | Stub=" + Hex(stub) + " | Distance=" + std::to_string(distance) + " | Nothing patched"; }
std::string StubNotExecutable(uintptr_t stub) { return "HOOK: Jump stub | Protect failed | Stub=" + Hex(stub) + " | Nothing patched"; }
std::string InitSiteUnprotectFailed(uintptr_t site) { return "HOOK: Init site | Unprotect failed | Site=" + Hex(site) + " | Nothing patched"; }
std::string SiteFound(uintptr_t site, uintptr_t moduleOffset, bool loose, size_t exactMatches) { return std::string("HOOK: Init site | Found | Pattern=") + (loose ? "Loose" : "Exact") + " | Address=" + Hex(site) + " | ModuleOffset=" + Hex(moduleOffset) + (loose ? " | ExactMatches=" + std::to_string(exactMatches) : ""); }
std::string SiteNotFound(size_t exactMatches, size_t looseMatches) { return "HOOK: Init site | Not found, expected 1 match | ExactMatches=" + std::to_string(exactMatches) + " | LooseMatches=" + std::to_string(looseMatches) + " | Nothing patched"; }
std::string InstallRoot() { return "InstallInitHook:"; }
std::string InstallSite(uintptr_t site, bool searched, uintptr_t anchorOffset) { return std::string(" Site: ") + Hex(site) + " | Match=" + (searched ? "Search" : "Exact") + " | AnchorOffset=" + Hex(anchorOffset); }
std::string InstallSiteDirect(uintptr_t site, bool loose, uintptr_t moduleOffset) { return std::string(" Site: ") + Hex(site) + " | Match=" + (loose ? "Loose" : "Exact") + " | ModuleOffset=" + Hex(moduleOffset); }
std::string InstallOriginalInit(uintptr_t target) { return " OriginalInit: " + Hex(target); }
std::string InstallJumpStub(uintptr_t stub) { return " JumpStub: " + Hex(stub); }
// --Capture--
std::string CaptureCheckFailed(int vehicleNames, int reads) { return "CAPTURE: Check | Failed, recording stopped | VehicleNames=" + std::to_string(vehicleNames) + " | Reads=" + std::to_string(reads); }
std::string CaptureCheckPassed(int vehicleNames, int reads) { return "CAPTURE: Check | Passed | VehicleNames=" + std::to_string(vehicleNames) + " | Reads=" + std::to_string(reads); }
std::string CaptureBufferFull() { return "CAPTURE: Buffer | Full, later models not recorded"; }
// --Models--
std::string ModelsWritten(size_t models, size_t missingHandlingIds) { return "MODELS: Write | Done, verified | Models=" + std::to_string(models) + " | MissingHandlingIds=" + std::to_string(missingHandlingIds); }
std::string ModelsNoneCaptured() { return "MODELS: Capture | None, vehicles registered before this load | File unchanged"; }
std::string ModelsNoneCapturedHookNotInstalled() { return "MODELS: Capture | None, the capture hook is not installed | File unchanged"; }
std::string ModelsNoneCapturedRecordingStopped() { return "MODELS: Capture | None, the capture check stopped recording | File unchanged"; }
std::string ModelsOpenFailed() { return "MODELS: Write | Open failed"; }
std::string ModelsWriteIncomplete() { return "MODELS: Write | Incomplete, a write failed"; }
std::string ModelsReadBackFailed() { return "MODELS: Write | Read back failed"; }
std::string ModelsVerifyFailed(size_t records, size_t expected, bool closesCorrectly) { return "MODELS: Write | Verify failed | Records=" + std::to_string(records) + " | Expected=" + std::to_string(expected) + " | ClosesCorrectly=" + (closesCorrectly ? "True" : "False"); }
// --Setup--
std::string BuildNotVerified(const std::string& version) { return "SETUP: Game build | Not verified, eviction and sub-handling unavailable | Version=" + version; }
// --Scan--
std::string PatternMatchedNone() { return "Pattern matched 0 times"; }
std::string PatternTargetsDiffer(size_t matches) { return "Pattern matched " + std::to_string(matches) + " times with differing targets"; }
std::string PatternMatchCount(size_t matches) { return "Pattern matched " + std::to_string(matches) + " times, expected 1"; }
std::string PatternTargetUnreadable(size_t matches) { return "Pattern matched " + std::to_string(matches) + " times, a target could not be read"; }
std::string NotFunctionStart() { return "Target is not a registered function start"; }
std::string NotCallInstruction() { return "Byte before the displacement is not a call opcode"; }
std::string DisplacementUnreadable() { return "Displacement could not be read"; }
// --Eviction--
std::string ModuleLookupFailed() { return "Fragment module lookup returned nothing or faulted"; }
std::string MapLayoutMismatch() { return "Map globals are not 8 bytes apart"; }
std::string EvictionUnavailable(const std::string& part, const std::string& reason) { return "EVICTION: Setup | Unavailable | Part=" + part + " | " + reason; }
std::string EvictionReadyRoot() { return "ResolveEviction:"; }
std::string EvictionReadyPart(const std::string& part, uintptr_t moduleOffset) { return " " + part + ": ModuleOffset=" + Hex(moduleOffset); }
std::string EvictionReadyFragmentModule(uintptr_t address) { return " FragmentModule: Address=" + Hex(address); }
std::string EvictionOutcomeLine(const std::string& modelName, uint32_t hash, const std::string& outcome) { return "EVICTION: " + (modelName.empty() ? std::string("Uncaptured model") : modelName) + " (" + Hash(hash) + ") | " + outcome; }
// --SubHandling--
std::string SubHandlingBytesMismatch() { return "Bytes at the checked address differ from the expected instruction"; }
std::string SubHandlingParserUnreadable() { return "Parser pointer is null or unreadable"; }
std::string SubHandlingNotJump() { return "Target is not a jump to a function"; }
std::string SubHandlingNewMismatch() { return "Allocator does not start with its expected bytes"; }
std::string SubHandlingUnavailable(const std::string& part, const std::string& reason) { return "SUBHANDLING: Setup | Unavailable | Part=" + part + " | " + reason; }
std::string SubHandlingCategoryUnavailable(const std::string& category, const std::string& reason) { return "SUBHANDLING: Setup | Category unavailable | Category=" + category + " | " + reason; }
std::string SubHandlingStructureMissing() { return "Class not found in the parser registry"; }
std::string SubHandlingConstructorMissing() { return "Placement constructor is null"; }
std::string SubHandlingDelegateBound() { return "Factory or placement constructor is bound to an object"; }
std::string SubHandlingReadyRoot() { return "ResolveSubHandling:"; }
std::string SubHandlingReadyPart(const std::string& part, uintptr_t moduleOffset) { return " " + part + ": ModuleOffset=" + Hex(moduleOffset); }
std::string SubHandlingReadyParser(uintptr_t address) { return " Parser: Address=" + Hex(address); }
std::string SubHandlingReadyCategory(const std::string& category, uintptr_t structure, uintptr_t factoryModuleOffset) { return " " + category + ": Structure=" + Hex(structure) + " | FactoryModuleOffset=" + Hex(factoryModuleOffset); }
std::string SubHandlingOutcomeLine(uintptr_t handling, const std::string& category, const std::string& outcome) { return "SUBHANDLING: Handling=" + Hex(handling) + " | Category=" + category + " | " + outcome; }
std::string SubHandlingDeletionOutcomeLine(uintptr_t handling, const std::string& category, const std::string& outcome) { return "SUBHANDLING: Handling=" + Hex(handling) + " | Category=" + category + " | Deletion | " + outcome; }
std::string SubHandlingClassUnreadable() { return "Class structure could not be read"; }
std::string SubHandlingReadyAdvancedData(uintptr_t structure, uintptr_t constructorModuleOffset) { return " AdvancedData: Structure=" + Hex(structure) + " | ConstructorModuleOffset=" + Hex(constructorModuleOffset); }
std::string SubHandlingAdvancedDataOutcomeLine(uintptr_t carData, int capacity, const std::string& outcome) { return "SUBHANDLING: CarData=" + Hex(carData) + " | AdvancedDataCapacity=" + std::to_string(capacity) + " | " + outcome; }
} // namespace Dbg
