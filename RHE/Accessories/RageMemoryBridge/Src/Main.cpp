#include "Capture.h"
#include "Hook.h"
#include "Log.h"
#include "Eviction.h"
#include "SubHandling.h"
#include <windows.h>
#include <main.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

// The output file sits in the game root, so every script that needs it reads it from the same place
static const char* const OUTPUT_FILE_NAME = "ModelMetaData.json";

// How often the script loop checks for models registered after the first write, in milliseconds
// The loop itself runs every frame so an eviction request is taken on the next frame
static const ULONGLONG WRITE_CHECK_INTERVAL = 1000;

// The game file versions the eviction and sub-handling layouts were confirmed on, one per edition
// Any other build leaves both unavailable, since a layout that moved there would be read or written blind
static const char* const VERIFIED_LEGACY_VERSION = "1.0.3889.0";
static const char* const VERIFIED_ENHANCED_VERSION = "1.0.1158.16";

// The record line prefix and the file ending the read back looks for. Every record is one line, and the file closes its array and object last
static const char* const RECORD_LINE_PREFIX = "\n    { \"Hash\": ";
static const char* const FILE_ENDING = "\n  ]\n}\n";

// Whether the capture hook installed, kept for the script loop's empty capture message
static bool hookInstalled = false;

static bool IsBuildVerified() { return GetGameFileVersion() == (IsEnhancedEdition() ? VERIFIED_ENHANCED_VERSION : VERIFIED_LEGACY_VERSION); }

static std::string EscapeJson(const std::string& text)
{
    std::string escaped;
    for (char character : text)
    {
        if (character == '"' || character == '\\') escaped += '\\';
        escaped += character;
    }
    return escaped;
}

static bool ReadWholeFile(const std::string& path, std::string& content)
{
    FILE* file = nullptr;
    if (fopen_s(&file, path.c_str(), "r") != 0 || file == nullptr) return false;
    char chunk[4096];
    size_t bytesRead;
    content.clear();
    while ((bytesRead = fread(chunk, 1, sizeof(chunk), file)) > 0) content.append(chunk, bytesRead);
    bool failed = ferror(file) != 0;
    fclose(file);
    return !failed;
}

static size_t CountOccurrences(const std::string& content, const char* needle)
{
    size_t count = 0;
    size_t needleLength = strlen(needle);
    for (size_t position = content.find(needle); position != std::string::npos; position = content.find(needle, position + needleLength)) count++;
    return count;
}

// GearRatioPatch reads each record line with plain text searches for its Hash and ModelName keys, not a JSON parser
// So the one record per line layout written here is a contract with it, and a tidier or minified writer would leave its log names blank
// Writes the file directly to its final name, then reads it back and confirms its record count and closing bracket
// Returns False with the reason in failure when any write, the close, or the read back fails, so the caller can write it again
static bool WriteModelMetaData(const std::vector<CapturedModel>& models, std::string& failure)
{
    std::string path = GetGameFolder() + OUTPUT_FILE_NAME;
    FILE* file = nullptr;
    if (fopen_s(&file, path.c_str(), "w") != 0 || file == nullptr)
    {
        failure = Dbg::ModelsOpenFailed();
        return false;
    }
    size_t missingHandlingIds = 0;
    bool written = fprintf(file, "{\n  \"Models\": [\n") >= 0;
    for (size_t i = 0; written && i < models.size(); i++)
    {
        const CapturedModel& model = models[i];
        if (model.handlingId.empty()) missingHandlingIds++;
        written = fprintf(file, "    { \"Hash\": %u, \"ModelName\": \"%s\", \"HandlingId\": \"%s\" }%s\n", model.hash, EscapeJson(model.modelName).c_str(), EscapeJson(model.handlingId).c_str(), i + 1 < models.size() ? "," : "") >= 0;
    }
    if (written) written = fprintf(file, "  ]\n}\n") >= 0;
    bool closed = fclose(file) == 0;
    if (!written || !closed)
    {
        failure = Dbg::ModelsWriteIncomplete();
        return false;
    }
    std::string content;
    if (!ReadWholeFile(path, content))
    {
        failure = Dbg::ModelsReadBackFailed();
        return false;
    }
    size_t recordCount = CountOccurrences(content, RECORD_LINE_PREFIX);
    size_t endingLength = strlen(FILE_ENDING);
    bool closesCorrectly = content.size() >= endingLength && content.compare(content.size() - endingLength, endingLength, FILE_ENDING) == 0;
    if (recordCount != models.size() || !closesCorrectly)
    {
        failure = Dbg::ModelsVerifyFailed(recordCount, models.size(), closesCorrectly);
        return false;
    }
    LogInfo(Dbg::ModelsWritten(models.size(), missingHandlingIds));
    return true;
}

static void ScriptMain()
{
    ResolveEviction();
    ResolveSubHandling();
    std::vector<CapturedModel> models;
    bool writePending = false;
    bool writeFailureLogged = false;
    bool overflowLogged = false;
    bool firstPass = true;
    ULONGLONG nextWriteCheck = 0;
    while (true)
    {
        ULONGLONG now = GetTickCount64();
        if (now >= nextWriteCheck)
        {
            nextWriteCheck = now + WRITE_CHECK_INTERVAL;
            if (CollectCapturedModels(models)) writePending = true;
            bool checkPassed = false;
            int checkVehicleNames = 0;
            int checkReads = 0;
            if (TakeCaptureCheckResult(checkPassed, checkVehicleNames, checkReads))
            {
                if (checkPassed) LogInfo(Dbg::CaptureCheckPassed(checkVehicleNames, checkReads));
                else LogError(Dbg::CaptureCheckFailed(checkVehicleNames, checkReads));
            }
            if (firstPass && models.empty())
            {
                if (!hookInstalled) LogInfo(Dbg::ModelsNoneCapturedHookNotInstalled());
                else if (HasCaptureStopped()) LogInfo(Dbg::ModelsNoneCapturedRecordingStopped());
                else LogInfo(Dbg::ModelsNoneCaptured());
            }
            firstPass = false;
            if (writePending)
            {
                // A failed write stays pending, so the next pass writes it again. The failure is logged once per run of failures
                std::string failure;
                if (WriteModelMetaData(models, failure))
                {
                    writePending = false;
                    writeFailureLogged = false;
                }
                else if (!writeFailureLogged)
                {
                    LogError(failure);
                    writeFailureLogged = true;
                }
            }
            if (!overflowLogged && GetCaptureOverflowCount() > 0)
            {
                LogWarn(Dbg::CaptureBufferFull());
                overflowLogged = true;
            }
        }
        // The game's streaming state and handling pool are touched only here, inside the game's own script update, never from a caller's thread
        ProcessEvictionRequests();
        ProcessSubHandlingRequests();
        scriptWait(0);
    }
}

// Attach runs before the game loads its vehicle data, so the hook has to be installed here rather than in the script loop
// The pattern scans run here too, since each setup's scans take far longer than a frame, which here only lengthens the load
// The module is pinned, so its only detach comes at process exit, when the game's code never runs again and nothing needs undoing
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        OpenLog();
        if (!PinPluginModule(module))
        {
            AbandonSubHandling();
            return TRUE;
        }
        hookInstalled = InstallModelCaptureHook();
        bool buildVerified = IsBuildVerified();
        if (!buildVerified) LogWarn(Dbg::BuildNotVerified(GetGameFileVersion()));
        ScanEvictionPatterns(buildVerified);
        ScanSubHandlingPatterns(buildVerified);
        // The scans finish before the script registers, so the script loop never reads their results early
        scriptRegister(module, ScriptMain);
    }
    return TRUE;
}
