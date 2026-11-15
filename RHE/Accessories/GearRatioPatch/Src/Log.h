#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

// The game executable's own folder, with a trailing backslash
std::string GetGameFolder();

// The 2 editions are different executables, told apart by the game executable's file name
bool IsEnhancedEdition();

// The game executable's file version, read from its own version resource. Reads unknown when it cannot be read
std::string GetGameFileVersion();

// Wipes the log and writes its header: The LOADED line, the hook library and game INFO lines, and the separator
// The versions come from the already loaded modules' own version resources, which loads nothing, so it is safe on the attach path
void OpenLog();

// Each takes a finished message from Dbg, so no call site formats text
void LogInfo(const std::string& message);
void LogWarn(const std::string& message);
void LogError(const std::string& message);

// One function per log message, so every logged line is built here, each in the area, subject, and fields form the scripts' logs use
// A cohesive block is a root line ending in a colon, then one child line per data point, each led by one space
namespace Dbg
{
// --Plugin--
std::string ModulePinFailed();
// --Hook--
std::string AnchorFoundExact(uintptr_t address, uintptr_t moduleOffset);
std::string AnchorFoundLoose(uintptr_t address, uintptr_t moduleOffset, size_t exactMatches);
std::string AnchorNotFound(size_t exactMatches, size_t looseMatches);
std::string AnchorOutsideFunction(uintptr_t anchorOffset);
std::string PrologueMismatch(uintptr_t functionOffset);
std::string StubNotAllocated(uintptr_t functionOffset);
std::string StubOutOfReach(uintptr_t stub, int64_t distance);
std::string StubNotExecutable(uintptr_t stub);
std::string EntryUnprotectFailed(uintptr_t functionOffset);
std::string InstallRoot();
std::string InstallFunction(uintptr_t address, uintptr_t moduleOffset);
std::string InstallRatioArrayOffset(size_t offset);
std::string InstallJumpStub(uintptr_t stub);
// --Gears--
std::string GearCountClamped(int given, int used);
std::string ReplaceGearRatiosRoot();
std::string KeepGearRatiosRoot();
std::string GearRatiosKeptReason();
std::string GearRatiosModel(const std::string& modelName, uint32_t modelHash);
std::string GearRatiosCount(int gearCount);
std::string GearRatiosAlternateRatios(bool alternateRatios);
// The ratio arrays hold reverse first, so the forward gears are read from index 1 through gearCount
std::string GearRatiosBefore(const float* ratios, int gearCount);
std::string GearRatiosAfter(const float* ratios, int gearCount);
std::string GearRatiosKept(const float* ratios, int gearCount);
// --Models--
std::string ModelListEmpty(const char* fileName);
} // namespace Dbg
