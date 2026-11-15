#pragma once
#include <cstddef>
#include <cstdint>

// The game's caller passes the gear count unclamped, and the fill writes the slot at that count, so a count above the array's capacity is capped first
int ClampGearCount(int gearCount);

// Runs after the game's own fill. A set that already falls steadily from first to top gear is left exactly as the game built it
// alternateRatios is the fill's own fifth argument, passed through only so the log can show which top gear the game chose
// The vehicle is the raw game pointer the fill ran for, read only when a set needs correcting, to name it in the log
void CorrectGearRatios(uint8_t* transmission, size_t arrayOffset, int gearCount, bool alternateRatios, void* vehicle);
