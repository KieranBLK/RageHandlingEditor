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
static const char* const LOG_FILE_NAME = "GearRatioPatch.log";
static const char* const PLUGIN_NAME = "GearRatioPatch";

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

static std::string FormatRatios(const float* ratios, int gearCount)
{
    std::string text;
    for (int gear = 1; gear <= gearCount; gear++)
    {
        char value[24];
        snprintf(value, sizeof(value), "%.3f", ratios[gear]);
        if (!text.empty()) text += ", ";
        text += value;
    }
    return text;
}
// --Plugin--
std::string ModulePinFailed() { return std::string("PLUGIN: ") + PLUGIN_NAME + " | Module pin failed | Nothing installed"; }
// --Hook--
std::string AnchorFoundExact(uintptr_t address, uintptr_t moduleOffset) { return "HOOK: Anchor | Found | Pattern=Exact | Address=" + Hex(address) + " | ModuleOffset=" + Hex(moduleOffset); }
std::string AnchorFoundLoose(uintptr_t address, uintptr_t moduleOffset, size_t exactMatches) { return "HOOK: Anchor | Found | Pattern=Loose | Address=" + Hex(address) + " | ModuleOffset=" + Hex(moduleOffset) + " | ExactMatches=" + std::to_string(exactMatches); }
std::string AnchorNotFound(size_t exactMatches, size_t looseMatches) { return "HOOK: Anchor | Not found, expected 1 match | ExactMatches=" + std::to_string(exactMatches) + " | LooseMatches=" + std::to_string(looseMatches) + " | Nothing patched"; }
std::string AnchorOutsideFunction(uintptr_t anchorOffset) { return "HOOK: Function | Anchor outside any registered function | AnchorOffset=" + Hex(anchorOffset) + " | Nothing patched"; }
std::string PrologueMismatch(uintptr_t functionOffset) { return "HOOK: Function | Unexpected prologue, cannot relocate | ModuleOffset=" + Hex(functionOffset) + " | Nothing patched"; }
std::string StubNotAllocated(uintptr_t functionOffset) { return "HOOK: Jump stub | No free memory in reach | ModuleOffset=" + Hex(functionOffset) + " | Nothing patched"; }
std::string StubOutOfReach(uintptr_t stub, int64_t distance) { return "HOOK: Jump stub | Out of rel32 reach | Stub=" + Hex(stub) + " | Distance=" + std::to_string(distance) + " | Nothing patched"; }
std::string StubNotExecutable(uintptr_t stub) { return "HOOK: Jump stub | Protect failed | Stub=" + Hex(stub) + " | Nothing patched"; }
std::string EntryUnprotectFailed(uintptr_t functionOffset) { return "HOOK: Function entry | Unprotect failed | ModuleOffset=" + Hex(functionOffset) + " | Nothing patched"; }
std::string InstallRoot() { return "InstallGearRatioHook:"; }
std::string InstallFunction(uintptr_t address, uintptr_t moduleOffset) { return " Function: Address=" + Hex(address) + " | ModuleOffset=" + Hex(moduleOffset); }
std::string InstallRatioArrayOffset(size_t offset) { return " RatioArrayOffset: " + Hex(offset); }
std::string InstallJumpStub(uintptr_t stub) { return " JumpStub: " + Hex(stub); }
// --Gears--
std::string GearCountClamped(int given, int used) { return "GEARS: Count | Above array bounds, clamping the fill | Given=" + std::to_string(given) + " | Used=" + std::to_string(used); }
std::string ReplaceGearRatiosRoot() { return "ReplaceGearRatios:"; }
std::string KeepGearRatiosRoot() { return "KeepGearRatios:"; }
std::string GearRatiosKeptReason() { return " Reason: First gear at or below top after doubling"; }
std::string GearRatiosModel(const std::string& modelName, uint32_t modelHash) { return " Model: " + modelName + " (" + Hash(modelHash) + ")"; }
std::string GearRatiosCount(int gearCount) { return " Gears: " + std::to_string(gearCount); }
std::string GearRatiosAlternateRatios(bool alternateRatios) { return std::string(" AlternateRatios: ") + (alternateRatios ? "True" : "False"); }
std::string GearRatiosBefore(const float* ratios, int gearCount) { return " Before: " + FormatRatios(ratios, gearCount); }
std::string GearRatiosAfter(const float* ratios, int gearCount) { return " After: " + FormatRatios(ratios, gearCount); }
std::string GearRatiosKept(const float* ratios, int gearCount) { return " Ratios: " + FormatRatios(ratios, gearCount); }
// --Models--
std::string ModelListEmpty(const char* fileName) { return std::string("MODELS: List | Loaded with no records, every name reads from the game or as uncaptured | File=") + fileName; }
} // namespace Dbg
