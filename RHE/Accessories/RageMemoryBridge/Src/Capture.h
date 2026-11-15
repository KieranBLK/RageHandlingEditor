#pragma once
#include <cstdint>
#include <string>
#include <vector>

struct CapturedModel
{
    uint32_t hash;
    std::string modelName;
    std::string handlingId;
};

// Hook side: Copies the 2 raw names from the init data the game passes to the model info's Init into the next preallocated record under the lock
// The capture check's counters and result are stored here too, and nothing else happens on the game's loading thread
// So there is no allocation, hashing, casing, or logging here
void CaptureModel(const void* initData);

// Script side: Hands out the capture check's result once it has finished, and only the first time. Returns False while it is unfinished or already handed out
// The check passes when enough of the first calls read as vehicle names, and vehicleNames is how many of the reads did
bool TakeCaptureCheckResult(bool& passed, int& vehicleNames, int& reads);

// Script side: True once the capture check failed and recording stopped
bool HasCaptureStopped();

// Script side: Hashes, upper-cases, and dedupes every record captured since the last call into the script's own map by model hash
// Returns True when the map changed, and then replaces the models with the whole map sorted by model name. Nothing is copied or sorted otherwise
bool CollectCapturedModels(std::vector<CapturedModel>& models);

// Script side: How many models were dropped because the preallocated record buffer was full
size_t GetCaptureOverflowCount();

// The game's own one-at-a-time hash of the lowercased text, the hash it keys model names and asset file extensions by
uint32_t HashModelName(const std::string& name);

// Script side: The upper-cased model name captured for the hash. Returns False when the hash was never captured
bool FindCapturedModelName(uint32_t hash, std::string& name);
