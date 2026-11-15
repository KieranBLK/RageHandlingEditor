#include "ModelNames.h"
#include "Log.h"
#include "Scan.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>

// The model list RageMemoryBridge writes beside this plugin, and the 2 keys each record line holds
static const char* const MODEL_LIST_FILE_NAME = "ModelMetaData.json";
static const char* const HASH_KEY = "\"Hash\": ";
static const char* const MODEL_NAME_KEY = "\"ModelName\": \"";

// The name a model reads as when neither the model list nor the game's display name has one
static const char* const UNCAPTURED_MODEL_NAME = "Uncaptured model";

// Longest record line the reader accepts, well past any model and handling name pair
static const int MAX_RECORD_LINE = 512;

// The vehicle's model hash and display name, read through the entity's model info pointer, the same on both editions
// The display name is the game's own 12 byte label from vehicles.meta's gameName
static const size_t ENTITY_MODEL_INFO_OFFSET = 0x20;
static const size_t MODEL_INFO_HASH_OFFSET = 0x18;
static const size_t MODEL_INFO_DISPLAY_NAME_OFFSET = 0x298;
static const size_t DISPLAY_NAME_SIZE = 12;

// The fill runs on whichever game thread spawns a vehicle, so the map and its loaded state are guarded
static std::mutex namesMutex;
static std::map<uint32_t, std::string> modelNames;
static bool modelListLoaded = false;

void ReadVehicleModel(void* vehicle, VehicleModel& model)
{
    model.hash = 0;
    model.displayName[0] = '\0';
    if (vehicle == nullptr) return;
    uintptr_t modelInfoSlot = reinterpret_cast<uintptr_t>(vehicle) + ENTITY_MODEL_INFO_OFFSET;
    if (!IsReadableRange(modelInfoSlot, sizeof(uintptr_t))) return;
    uintptr_t modelInfo = *reinterpret_cast<const uintptr_t*>(modelInfoSlot);
    if (modelInfo == 0 || !IsReadableRange(modelInfo + MODEL_INFO_HASH_OFFSET, sizeof(uint32_t))) return;
    model.hash = *reinterpret_cast<const uint32_t*>(modelInfo + MODEL_INFO_HASH_OFFSET);
    if (!IsReadableRange(modelInfo + MODEL_INFO_DISPLAY_NAME_OFFSET, DISPLAY_NAME_SIZE)) return;
    const char* displayName = reinterpret_cast<const char*>(modelInfo + MODEL_INFO_DISPLAY_NAME_OFFSET);
    for (size_t i = 0; i < DISPLAY_NAME_SIZE && displayName[i] != '\0'; i++)
    {
        // A byte outside printable text means the slot holds something else, so no name is shown rather than garbage
        if (displayName[i] < 0x20 || displayName[i] > 0x7E)
        {
            model.displayName[0] = '\0';
            return;
        }
        model.displayName[i] = displayName[i];
        model.displayName[i + 1] = '\0';
    }
}

// Each record sits on its own line, as RageMemoryBridge writes it. A line without both keys is skipped
static void ParseRecordLine(const char* line)
{
    const char* hashText = strstr(line, HASH_KEY);
    const char* nameText = strstr(line, MODEL_NAME_KEY);
    if (hashText == nullptr || nameText == nullptr) return;
    uint32_t hash = static_cast<uint32_t>(strtoul(hashText + strlen(HASH_KEY), nullptr, 10));
    const char* nameStart = nameText + strlen(MODEL_NAME_KEY);
    const char* nameEnd = strchr(nameStart, '"');
    if (nameEnd == nullptr || nameEnd == nameStart) return;
    modelNames[hash] = std::string(nameStart, nameEnd);
}

// A missing file leaves the list unloaded, so a later lookup tries again once RageMemoryBridge has written it
// A file that opens but yields no records is reported once, since a changed line layout in the writer would otherwise leave every name blank with no word
static void LoadModelList()
{
    std::string path = GetGameFolder() + MODEL_LIST_FILE_NAME;
    FILE* file = nullptr;
    if (fopen_s(&file, path.c_str(), "r") != 0 || file == nullptr) return;
    char line[MAX_RECORD_LINE];
    while (fgets(line, sizeof(line), file) != nullptr) ParseRecordLine(line);
    fclose(file);
    modelListLoaded = true;
    if (modelNames.empty()) LogWarn(Dbg::ModelListEmpty(MODEL_LIST_FILE_NAME));
}

std::string ResolveModelName(uint32_t modelHash, const std::string& displayName)
{
    std::string name;
    {
        std::lock_guard<std::mutex> lock(namesMutex);
        if (!modelListLoaded) LoadModelList();
        auto entry = modelNames.find(modelHash);
        if (entry != modelNames.end()) name = entry->second;
    }
    if (name.empty()) name = displayName.empty() ? UNCAPTURED_MODEL_NAME : displayName;
    return name;
}
