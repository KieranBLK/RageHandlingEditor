#include "Capture.h"
#include <windows.h>
#include <algorithm>
#include <cctype>
#include <cstring>
#include <map>
#include <mutex>

// Init data string slots: Each holds a pointer to the game's own null-terminated string
static const size_t MODEL_NAME_OFFSET = 0x0;
static const size_t HANDLING_ID_OFFSET = 0x10;

// A name longer than this, or holding a non-printable byte, is treated as garbage rather than a real name. The buffer adds the terminator
static const size_t MAX_NAME_LENGTH = 64;
static const size_t NAME_BUFFER_SIZE = MAX_NAME_LENGTH + 1;

// The first calls are checked as a group: Too few real names among them means the hook sits on the wrong call or the wrong slots
static const int CAPTURE_CHECK_COUNT = 8;
static const int CAPTURE_CHECK_MINIMUM_VALID = 6;

// How many raw records the preallocated buffer holds. A model registered past this is counted as dropped instead of written past the buffer
static const size_t MAX_CAPTURED_MODELS = 8192;

// One captured model exactly as the game spelled it: The hook only copies bytes into it
struct CaptureRecord
{
    char modelName[NAME_BUFFER_SIZE];
    char handlingId[NAME_BUFFER_SIZE];
};

// The hook writes records and the counters under the mutex. A record below capturedCount is never written again
// The capture check's result is only stored here for the script loop to log, so the hook never writes the log
static std::mutex captureMutex;
static int checkedCalls = 0;
static int validCalls = 0;
static bool checkFinished = false;
static bool recordingStopped = false;
static CaptureRecord captureRecords[MAX_CAPTURED_MODELS];
static size_t capturedCount = 0;
static size_t captureOverflowCount = 0;

// Touched only by the script thread: How many records the map has absorbed, the map itself, and whether the capture check's result was handed out
static bool checkResultTaken = false;
static size_t processedCount = 0;
static std::map<uint32_t, CapturedModel> scriptModels;

// Copies a string through a pointer read from the init data, stopping at the first fault instead of crashing the game
// Holds no C++ objects, since a structured exception frame cannot share a function with them
static bool ReadGuardedString(const void* slotAddress, char* output, size_t outputSize)
{
    __try
    {
        const char* text = *reinterpret_cast<const char* const*>(slotAddress);
        if (text == nullptr) return false;
        size_t length = 0;
        while (length + 1 < outputSize)
        {
            char character = text[length];
            if (character == '\0') break;
            if (character < 0x20 || character > 0x7E) return false;
            output[length++] = character;
        }
        if (length == 0 || text[length] != '\0') return false;
        output[length] = '\0';
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// The game's model hash: One-at-a-time hashing of the lowercased name
uint32_t HashModelName(const std::string& name)
{
    uint32_t hash = 0;
    for (char character : name)
    {
        hash += static_cast<uint8_t>(tolower(static_cast<unsigned char>(character)));
        hash += hash << 10;
        hash ^= hash >> 6;
    }
    hash += hash << 3;
    hash ^= hash >> 11;
    hash += hash << 15;
    return hash;
}

static std::string ToUpper(const char* text)
{
    std::string upper = text;
    for (char& character : upper) character = static_cast<char>(toupper(static_cast<unsigned char>(character)));
    return upper;
}

void CaptureModel(const void* initData)
{
    if (initData == nullptr) return;
    const uint8_t* data = static_cast<const uint8_t*>(initData);
    char modelName[NAME_BUFFER_SIZE];
    char handlingId[NAME_BUFFER_SIZE];
    bool hasModelName = ReadGuardedString(data + MODEL_NAME_OFFSET, modelName, sizeof(modelName));
    bool hasHandlingId = ReadGuardedString(data + HANDLING_ID_OFFSET, handlingId, sizeof(handlingId));
    if (!hasHandlingId) handlingId[0] = '\0';
    std::lock_guard<std::mutex> lock(captureMutex);
    if (recordingStopped) return;
    if (checkedCalls < CAPTURE_CHECK_COUNT)
    {
        checkedCalls++;
        if (hasModelName) validCalls++;
        if (checkedCalls == CAPTURE_CHECK_COUNT)
        {
            checkFinished = true;
            if (validCalls < CAPTURE_CHECK_MINIMUM_VALID)
            {
                // The game still runs every Init normally, only the recording stops, and an empty capture leaves the previous file untouched
                recordingStopped = true;
                capturedCount = 0;
                return;
            }
        }
    }
    if (!hasModelName) return;
    if (capturedCount >= MAX_CAPTURED_MODELS)
    {
        captureOverflowCount++;
        return;
    }
    CaptureRecord& record = captureRecords[capturedCount];
    strcpy_s(record.modelName, sizeof(record.modelName), modelName);
    strcpy_s(record.handlingId, sizeof(record.handlingId), handlingId);
    capturedCount++;
}

bool TakeCaptureCheckResult(bool& passed, int& vehicleNames, int& reads)
{
    if (checkResultTaken) return false;
    std::lock_guard<std::mutex> lock(captureMutex);
    if (!checkFinished) return false;
    checkResultTaken = true;
    passed = !recordingStopped;
    vehicleNames = validCalls;
    reads = checkedCalls;
    return true;
}

bool HasCaptureStopped()
{
    std::lock_guard<std::mutex> lock(captureMutex);
    return recordingStopped;
}

bool CollectCapturedModels(std::vector<CapturedModel>& models)
{
    size_t availableCount;
    {
        std::lock_guard<std::mutex> lock(captureMutex);
        availableCount = capturedCount;
    }
    if (availableCount <= processedCount) return false;
    bool changed = false;
    for (; processedCount < availableCount; processedCount++)
    {
        const CaptureRecord& record = captureRecords[processedCount];
        CapturedModel model;
        model.hash = HashModelName(record.modelName);
        // Names are stored upper-cased, the casing every export writes. The hash is case-insensitive, so it is unaffected
        model.modelName = ToUpper(record.modelName);
        model.handlingId = ToUpper(record.handlingId);
        auto existing = scriptModels.find(model.hash);
        if (existing != scriptModels.end() && existing->second.modelName == model.modelName && existing->second.handlingId == model.handlingId) continue;
        scriptModels[model.hash] = model;
        changed = true;
    }
    if (!changed) return false;
    models.clear();
    for (const auto& pair : scriptModels) models.push_back(pair.second);
    std::sort(models.begin(), models.end(), [](const CapturedModel& left, const CapturedModel& right) { return left.modelName < right.modelName; });
    return true;
}

size_t GetCaptureOverflowCount()
{
    std::lock_guard<std::mutex> lock(captureMutex);
    return captureOverflowCount;
}

bool FindCapturedModelName(uint32_t hash, std::string& name)
{
    auto entry = scriptModels.find(hash);
    if (entry == scriptModels.end()) return false;
    name = entry->second.modelName;
    return true;
}
