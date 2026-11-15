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
std::string InitWindowUnreadable();
std::string InitTargetNotFunctionStart(uintptr_t target);
std::string InitSiteFoundBySearch(uintptr_t anchorOffset);
std::string InitSiteNotFound();
std::string StubNotAllocated(uintptr_t site);
std::string StubOutOfReach(uintptr_t stub, int64_t distance);
std::string StubNotExecutable(uintptr_t stub);
std::string InitSiteUnprotectFailed(uintptr_t site);
std::string SiteFound(uintptr_t site, uintptr_t moduleOffset, bool loose, size_t exactMatches);
std::string SiteNotFound(size_t exactMatches, size_t looseMatches);
std::string InstallRoot();
std::string InstallSite(uintptr_t site, bool searched, uintptr_t anchorOffset);
std::string InstallSiteDirect(uintptr_t site, bool loose, uintptr_t moduleOffset);
std::string InstallOriginalInit(uintptr_t target);
std::string InstallJumpStub(uintptr_t stub);
// --Capture--
std::string CaptureCheckFailed(int vehicleNames, int reads);
std::string CaptureCheckPassed(int vehicleNames, int reads);
std::string CaptureBufferFull();
// --Models--
std::string ModelsWritten(size_t models, size_t missingHandlingIds);
std::string ModelsNoneCaptured();
std::string ModelsNoneCapturedHookNotInstalled();
std::string ModelsNoneCapturedRecordingStopped();
std::string ModelsOpenFailed();
std::string ModelsWriteIncomplete();
std::string ModelsReadBackFailed();
std::string ModelsVerifyFailed(size_t records, size_t expected, bool closesCorrectly);
// --Setup--
std::string BuildNotVerified(const std::string& version);
// --Scan--
std::string PatternMatchedNone();
std::string PatternTargetsDiffer(size_t matches);
std::string PatternMatchCount(size_t matches);
std::string PatternTargetUnreadable(size_t matches);
std::string NotFunctionStart();
std::string NotCallInstruction();
std::string DisplacementUnreadable();
// --Eviction--
std::string ModuleLookupFailed();
std::string MapLayoutMismatch();
std::string EvictionUnavailable(const std::string& part, const std::string& reason);
std::string EvictionReadyRoot();
std::string EvictionReadyPart(const std::string& part, uintptr_t moduleOffset);
std::string EvictionReadyFragmentModule(uintptr_t address);
std::string EvictionOutcomeLine(const std::string& modelName, uint32_t hash, const std::string& outcome);
// --SubHandling--
std::string SubHandlingBytesMismatch();
std::string SubHandlingParserUnreadable();
std::string SubHandlingNotJump();
std::string SubHandlingNewMismatch();
std::string SubHandlingUnavailable(const std::string& part, const std::string& reason);
std::string SubHandlingCategoryUnavailable(const std::string& category, const std::string& reason);
std::string SubHandlingStructureMissing();
std::string SubHandlingConstructorMissing();
std::string SubHandlingDelegateBound();
std::string SubHandlingReadyRoot();
std::string SubHandlingReadyPart(const std::string& part, uintptr_t moduleOffset);
std::string SubHandlingReadyParser(uintptr_t address);
std::string SubHandlingReadyCategory(const std::string& category, uintptr_t structure, uintptr_t factoryModuleOffset);
std::string SubHandlingOutcomeLine(uintptr_t handling, const std::string& category, const std::string& outcome);
std::string SubHandlingDeletionOutcomeLine(uintptr_t handling, const std::string& category, const std::string& outcome);
std::string SubHandlingClassUnreadable();
std::string SubHandlingReadyAdvancedData(uintptr_t structure, uintptr_t constructorModuleOffset);
std::string SubHandlingAdvancedDataOutcomeLine(uintptr_t carData, int capacity, const std::string& outcome);
} // namespace Dbg
