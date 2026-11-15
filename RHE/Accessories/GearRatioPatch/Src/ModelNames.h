#pragma once
#include <cstdint>
#include <string>

// The vehicle the fill ran for, named in the log. The display name holds the game's 12 byte label and its terminator
struct VehicleModel
{
    uint32_t hash;
    char displayName[13];
};

// Reads the vehicle's model hash and display name through its model info pointer. Leaves the hash 0 and the name empty when any read fails
void ReadVehicleModel(void* vehicle, VehicleModel& model);

// Resolves a vehicle's model name for the log, the name RageMemoryBridge's log uses
// The name comes from RageMemoryBridge's ModelMetaData.json in the game root, since the game keeps only the hash after registration
// Without that file, the game's own display name stands in, and without either the name reads Uncaptured model
std::string ResolveModelName(uint32_t modelHash, const std::string& displayName);
