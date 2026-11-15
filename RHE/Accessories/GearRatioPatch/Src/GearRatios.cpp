#include "GearRatios.h"
#include "Log.h"
#include "ModelNames.h"
#include <cmath>
#include <cstring>
#include <mutex>
#include <set>
#include <string>
#include <tuple>

// The ratio array holds reverse and then this many forward gears
static const int MAX_FORWARD_GEARS = 10;

// The game halves first gear once for each of 2 flags, and with both, first gear falls to or below top gear
// Doubling it undoes one halving, which puts it back above top gear so the fill between them can fall steadily
static const float FIRST_GEAR_RESTORE_FACTOR = 2.0f;

// The fill runs on whichever game thread spawns or modifies a vehicle, so the log-once sets are guarded
static std::mutex seenMutex;
static std::set<int> clampedCountsLogged;
static std::set<std::tuple<uint32_t, int, uint32_t, uint32_t, bool>> loggedSets;

static uint32_t FloatBits(float value)
{
    uint32_t bits = 0;
    memcpy(&bits, &value, sizeof(bits));
    return bits;
}

// One log block per model and distinct set, so a vehicle spawned repeatedly never grows the log
// Returns True only the first time a combination is recorded
static bool RecordSetAsLogged(uint32_t modelHash, int gearCount, float firstGear, float topGear, bool alternateRatios)
{
    std::lock_guard<std::mutex> lock(seenMutex);
    return loggedSets.insert(std::make_tuple(modelHash, gearCount, FloatBits(firstGear), FloatBits(topGear), alternateRatios)).second;
}

int ClampGearCount(int gearCount)
{
    if (gearCount <= MAX_FORWARD_GEARS) return gearCount;
    try
    {
        bool firstTime = false;
        {
            std::lock_guard<std::mutex> lock(seenMutex);
            firstTime = clampedCountsLogged.insert(gearCount).second;
        }
        if (firstTime) LogInfo(Dbg::GearCountClamped(gearCount, MAX_FORWARD_GEARS));
    }
    catch (...)
    {
        // The clamp is what keeps the fill inside the array, so a failed log line must never skip it
        return MAX_FORWARD_GEARS;
    }
    return MAX_FORWARD_GEARS;
}

// A 1-gear set is a CVT's, and reverse is never part of the check, so both stay as the game wrote them
void CorrectGearRatios(uint8_t* transmission, size_t arrayOffset, int gearCount, bool alternateRatios, void* vehicle)
{
    if (gearCount < 2 || gearCount > MAX_FORWARD_GEARS) return;
    float* ratios = reinterpret_cast<float*>(transmission + arrayOffset);
    bool fallsSteadily = true;
    for (int gear = 2; gear <= gearCount; gear++)
    {
        if (ratios[gear] >= ratios[gear - 1]) fallsSteadily = false;
    }
    if (fallsSteadily) return;
    VehicleModel model;
    ReadVehicleModel(vehicle, model);
    float before[MAX_FORWARD_GEARS + 1];
    memcpy(before, ratios, sizeof(float) * static_cast<size_t>(gearCount + 1));
    float firstGear = ratios[1];
    float topGear = ratios[gearCount];
    if (firstGear <= topGear) firstGear *= FIRST_GEAR_RESTORE_FACTOR;
    bool keptAsIs = firstGear <= topGear;
    if (!keptAsIs)
    {
        // Each gear is the one before it divided by the same step, which reaches top gear exactly at the last gear
        float step = powf(firstGear / topGear, 1.0f / static_cast<float>(gearCount - 1));
        ratios[1] = firstGear;
        for (int gear = 2; gear < gearCount; gear++) ratios[gear] = ratios[gear - 1] / step;
    }
    try
    {
        if (!RecordSetAsLogged(model.hash, gearCount, before[1], topGear, alternateRatios)) return;
        std::string modelName = ResolveModelName(model.hash, model.displayName);
        if (keptAsIs)
        {
            LogWarn(Dbg::KeepGearRatiosRoot());
            LogWarn(Dbg::GearRatiosKeptReason());
            LogWarn(Dbg::GearRatiosModel(modelName, model.hash));
            LogWarn(Dbg::GearRatiosCount(gearCount));
            LogWarn(Dbg::GearRatiosAlternateRatios(alternateRatios));
            LogWarn(Dbg::GearRatiosKept(before, gearCount));
            return;
        }
        LogInfo(Dbg::ReplaceGearRatiosRoot());
        LogInfo(Dbg::GearRatiosModel(modelName, model.hash));
        LogInfo(Dbg::GearRatiosCount(gearCount));
        LogInfo(Dbg::GearRatiosAlternateRatios(alternateRatios));
        LogInfo(Dbg::GearRatiosBefore(before, gearCount));
        LogInfo(Dbg::GearRatiosAfter(ratios, gearCount));
    }
    catch (...)
    {
        // The correction is already written, so a failure while logging costs only the log lines and must never reach the game's own call
        return;
    }
}
