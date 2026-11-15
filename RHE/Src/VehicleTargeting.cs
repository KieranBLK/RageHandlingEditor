using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using LemonUI.Menus;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // First unmounted extra editor whose tree did not fail to build, or null if none is left. It never builds a tree, since startup reads it before the warm-up
        private EditorContext GetFreeAdditionalEditor()
        {
            for (int i = 1; i < editorContexts.Length; i++) { if (!IsAdditionalEditorMounted(editorContexts[i]) && editorContexts[i].BuildState != EditorBuildState.Failed) return editorContexts[i]; }
            return null;
        }

        // A tree whose vehicle died reads unmounted here until the tick unmounts it
        private static bool IsAdditionalEditorMounted(EditorContext context) { return !Entity.IsNullOrNotExisting(context.TargetVehicle); }

        // Gates Current's "(Current)" title differentiator and its Active coloring, since neither means anything with no other editor mounted to tell it apart from
        private bool AnyAdditionalEditorMounted()
        {
            for (int i = 1; i < editorContexts.Length; i++) { if (IsAdditionalEditorMounted(editorContexts[i])) return true; }
            return false;
        }

        // The extra editor that owns item as its main menu row, or null. An unmounted editor's row is never in the main menu, so it can never be the highlighted item
        private EditorContext ResolveAdditionalEditorForMainMenuItem(NativeItem item)
        {
            if (item == null) return null;
            for (int i = 1; i < editorContexts.Length; i++) { if (editorContexts[i].MainMenuItem == item) return editorContexts[i]; }
            return null;
        }

        // The only place the seat edge advances. Current's target stays scoped through menu depth and leaving the vehicle, until another vehicle is entered or it dies
        // On a seat change the merge, the retarget, and the Diff capture run before lastKnownSeatedVehicle is stamped, so a throw leaves the edge for the next tick
        // Each step is safe to repeat: A finished merge leaves no editor on the handling, the retarget writes the same vehicle, and the capture runs once per handling
        private void TrackSeatedVehicle(Ped player)
        {
            bool seated = !Entity.IsNullOrNotExisting(player) && player.IsInVehicle();
            if (!seated)
            {
                lastKnownSeatedVehicle = null;
                return;
            }
            Vehicle occupied = player.CurrentVehicle;
            if (startupLoadInProgress)
            {
                currentContext.TargetVehicle = occupied;
                return;
            }
            bool seatChanged = HasSeatedVehicleChanged(occupied);
            if (seatChanged)
            {
                CheckAutoMergeSeatCollision(occupied);
                if (!Entity.IsNullOrNotExisting(occupied))
                {
                    currentContext.TargetVehicle = occupied;
                    CheckPreviewDiffSnapshotCapture(occupied);
                    CaptureUndoAllBaseline(occupied);
                }
                lastKnownSeatedVehicle = occupied;
            }
            if (!Entity.IsNullOrNotExisting(occupied) && (seatChanged || uptimeTicks - lastTrailerCheckTick >= TRAILER_CHECK_INTERVAL))
            {
                lastTrailerCheckTick = uptimeTicks;
                CheckTrailerAutoMount(occupied);
            }
        }

        // Maps each truck Handle to the Handle of the trailer the player manually unmounted from it while it was still physically attached
        // CheckTrailerAutoMount consults it so the very next tick does not silently re-mount what was just unmounted
        // It is session intent and never persisted across a reload
        private Dictionary<int, int> declinedTrailerPerTruck = new Dictionary<int, int>();

        // Re-evaluated every TRAILER_CHECK_INTERVAL ticks and on a new seat, so a trailer attached *after* the player sits in the truck is still caught
        // Safe to call repeatedly: The handling check makes a repeat call for a mounted trailer, or a second trailer on a mounted handling, a no-op
        // It mounts without claiming Active, so the trailer never displaces the truck the player is driving as the Respawn, Save, and Exports target
        private void CheckTrailerAutoMount(Vehicle occupied)
        {
            Vehicle trailer = occupied.TrailerVehicle;
            if (Entity.IsNullOrNotExisting(trailer))
            {
                // No trailer attached at all is a genuine detach, regardless of what was previously declined for this truck
                declinedTrailerPerTruck.Remove(occupied.Handle);
                return;
            }
            int declinedTrailerHandle;
            if (declinedTrailerPerTruck.TryGetValue(occupied.Handle, out declinedTrailerHandle))
            {
                if (declinedTrailerHandle == trailer.Handle) return;
                // A different trailer than the one declined means a genuine detach and a different reattach, so the old decline no longer applies
                declinedTrailerPerTruck.Remove(occupied.Handle);
            }
            EditorContext freeEditor = GetFreeAdditionalEditor();
            if (freeEditor == null) return;
            HandlingKey trailerKey = ResolveHandlingKey(trailer);
            if (!trailerKey.IsValid || trailerKey.Equals(ResolveHandlingKey(occupied)) || IsHandlingAlreadyScoped(trailerKey)) return;
            MountVehicleIntoAdditionalEditor(trailer, freeEditor, false, true, false, true);
        }

        // Compares Handle, the vehicle's real identity, since Ped.CurrentVehicle constructs a new wrapper on every access
        private bool HasSeatedVehicleChanged(Vehicle occupied)
        {
            return Entity.IsNullOrNotExisting(lastKnownSeatedVehicle) || Entity.IsNullOrNotExisting(occupied) || occupied.Handle != lastKnownSeatedVehicle.Handle;
        }

        // The crosshair and the hover pick run while selectingVehicle is True, and mainMenu stays hidden until the selection is confirmed or canceled
        private void EnterVehicleSelectionMode()
        {
            selectingVehicle = true;
            selectModeArmedThisTick = true;
            hoveredVehicleForSelection = null;
            HideMenu(mainMenu);
            string cancelHint = keyToggleMenu != Keys.None ? string.Format(HINT_CANCEL_KEYS, keyToggleMenu) : HINT_CANCEL_KEY_BACKSPACE;
            // Frozen so the aim and cancel hint stays readable for the whole selection, however long the player takes to aim
            notifications.NotifyFrozen(NOTIFICATION_KEY_SELECT_VEHICLE, string.Format(NOTIF_SELECT_VEHICLE_AIM, cancelHint));
        }

        // The vehicle, seat, and every weapon slot index currently ammo-restricted against it. SET_VEHICLE_WEAPON_RESTRICTED_AMMO persists until explicitly unset
        // Slots are tracked by *slot index* rather than weapon hash, since restricted ammo is a per-slot-index native and 2 different slots can share the same hash
        // A vehicle *or* seat change fully re-enables the old set (via ReenableSeatWeaponSuppression) before disabling the new one, rather than diffing the two sets
        // suppressedSeatWeaponAmmoCounts holds the restricted ammo count each slot had before suppression, in the same order, and the re-enable restores it
        private Vehicle suppressedSeatWeaponVehicle;
        private VehicleSeat suppressedSeatWeaponSeat = VehicleSeat.None;
        private List<int> suppressedSeatWeaponSlots = new List<int>();
        private List<int> suppressedSeatWeaponAmmoCounts = new List<int>();

        // The restricted ammo count a slot holds right now, or -1 (the unrestricted default) on a build that cannot read it
        private static int ReadRestrictedAmmoCount(Vehicle vehicle, int slot)
        {
            try { return vehicle.GetRestrictedAmmoCount(slot); }
            catch (GameVersionNotSupportedException) { return -1; }
        }

        // Aiming to target another vehicle must not fire the seat-mounted weapon or show its sight, so this uses 2 fully independent mechanisms
        // A restricted ammo count of 0 (Vehicle.SetRestrictedAmmoCount) blocks the shot outright with no native-side queuing
        // HideComponentThisFrame(HudComponent.Reticle) hides the game's aiming reticle, a per-frame HUD toggle unrelated to fire-enable state
        // preResolvedVehicle comes from the caller and is shared with UpdateVehicleSelectionHover, since Ped.CurrentVehicle builds a fresh wrapper on every access
        private void SuppressSeatMountedWeapon(Ped player, Vehicle preResolvedVehicle)
        {
            if (Entity.IsNullOrNotExisting(preResolvedVehicle))
            {
                ReenableSeatWeaponSuppression();
                return;
            }

            // sameContext only needs the vehicle and seat identity, not the handling data
            // It is checked first so the steady state (unchanged vehicle and seat, the common case) skips resolving the handling data entirely
            VehicleSeat seat = player.SeatIndex;
            bool sameContext = !Entity.IsNullOrNotExisting(suppressedSeatWeaponVehicle) && suppressedSeatWeaponVehicle.Handle == preResolvedVehicle.Handle && suppressedSeatWeaponSeat == seat;
            if (sameContext)
            {
                if (suppressedSeatWeaponSlots.Count > 0) GTA.UI.Hud.HideComponentThisFrame(GTA.UI.HudComponent.Reticle);
                return;
            }
            HandlingData handling = preResolvedVehicle.HandlingData;
            VehicleWeaponHandlingData weaponData = handling != null && handling.IsValid ? handling.VehicleWeaponHandlingData : null;
            if (weaponData == null)
            {
                ReenableSeatWeaponSuppression();
                return;
            }

            // Re-enables whatever the *last* context (old vehicle or old seat) had suppressed before disabling the new context's own set
            // This keeps every re-enable going through the one ReenableSeatWeaponSuppression path
            ReenableSeatWeaponSuppression();
            var required = new List<int>();
            VehicleWeaponHash[] hashes = weaponData.WeaponHash;
            VehicleSeat[] seats = weaponData.WeaponSeats;
            int slotCount = Math.Min(hashes.Length, seats.Length);
            for (int i = 0; i < slotCount; i++)
            {
                if (seats[i] != seat || IsUnsetWeaponHash(hashes[i])) continue;
                required.Add(i);
            }
            var previousCounts = new List<int>();
            for (int i = 0; i < required.Count; i++)
            {
                previousCounts.Add(ReadRestrictedAmmoCount(preResolvedVehicle, required[i]));
                preResolvedVehicle.SetRestrictedAmmoCount(required[i], 0);
            }
            if (required.Count > 0) GTA.UI.Hud.HideComponentThisFrame(GTA.UI.HudComponent.Reticle);
            suppressedSeatWeaponVehicle = preResolvedVehicle;
            suppressedSeatWeaponSeat = seat;
            suppressedSeatWeaponSlots = required;
            suppressedSeatWeaponAmmoCounts = previousCounts;
        }

        // OnAborted calls this directly on a script reload, since no following tick exists to defer to, so no restriction survives with nothing left to re-enable it
        // Restoring ammo needs no hash lookup or occupant resolution, since it reads and writes purely by slot index regardless of who occupies the seat
        // The reticle hide has no persistent state to clear: It simply stops being called
        private void ReenableSeatWeaponSuppression()
        {
            if (!Entity.IsNullOrNotExisting(suppressedSeatWeaponVehicle))
            {
                for (int i = 0; i < suppressedSeatWeaponSlots.Count; i++) suppressedSeatWeaponVehicle.SetRestrictedAmmoCount(suppressedSeatWeaponSlots[i], suppressedSeatWeaponAmmoCounts[i]);
            }
            suppressedSeatWeaponVehicle = null;
            suppressedSeatWeaponSeat = VehicleSeat.None;
            suppressedSeatWeaponSlots.Clear();
            suppressedSeatWeaponAmmoCounts.Clear();
        }

        // UnmountAdditionalEditor calls this directly, since an unmount can flip the Active editor back to Current with no SyncFieldValues call anywhere in that path
        // Saved Handlings itself stays enabled with no vehicle, so saved handlings can be browsed and managed on foot, and only applying one needs a vehicle
        private void SyncActiveActionEnablement()
        {
            Vehicle activeVehicle = activeContext.TargetVehicle;
            bool activeHasVehicle = !Entity.IsNullOrNotExisting(activeVehicle);
            unloadVehicleItem.Enabled = activeHasVehicle && !pendingModelUnloads.ContainsKey(activeVehicle.Model.Hash);
            respawnVehicleItem.Enabled = activeHasVehicle;
            saveHandlingItem.Enabled = activeHasVehicle;
            SyncSavedHandlingRowsApplicable(activeHasVehicle);
        }

        // True when any tree's target is on the handling, whatever the vehicle models
        // It blocks a duplicate mount outright, since the phantom and pending dictionaries have no automatic resync between 2 independently tracked displays
        private bool IsHandlingAlreadyScoped(HandlingKey handling) { return ResolveEditorContextOfHandling(handling) != null; }

        // The tree whose target is on the handling, or null when no tree edits it
        // At most one tree matches, since the scope guard and the seat merge keep a handling in one tree
        private EditorContext ResolveEditorContextOfHandling(HandlingKey handling)
        {
            if (!handling.IsValid) return null;
            for (int i = 0; i < editorContexts.Length; i++)
            {
                if (ResolveHandlingKey(editorContexts[i].TargetVehicle).Equals(handling)) return editorContexts[i];
            }
            return null;
        }

        // Confirms on the same hoveredVehicleForSelection the crosshair is already showing, with no separate raycast
        // A miss (nothing in view), or a vehicle whose handling is already scoped, does not exit the mode, so aiming again and confirming works without re-arming
        // A successful confirm keeps focus on Select Vehicle's own row rather than auto-navigating into the new editor
        private void ConfirmVehicleSelection()
        {
            if (Entity.IsNullOrNotExisting(hoveredVehicleForSelection))
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECT_VEHICLE_NONE, NOTIF_SELECT_NONE);
                return;
            }
            Vehicle confirmedVehicle = hoveredVehicleForSelection;
            if (IsHandlingAlreadyScoped(ResolveHandlingKey(confirmedVehicle)))
            {
                notifications.Notify(NOTIFICATION_KEY_SELECT_VEHICLE_SCOPED, string.Format(NOTIF_SELECT_ALREADY_EDITED, modelNames.ResolveModelName(confirmedVehicle)));
                return;
            }
            // Select Vehicle's own row is already hidden once every slot is full (SyncSelectVehicleSlot), so this never fires through normal play
            // It is guarded anyway rather than trusting that UI visibility precondition alone
            if (GetFreeAdditionalEditor() == null)
            {
                notifications.PostNotificationOnce(NOTIFICATION_KEY_SELECT_VEHICLE_FULL, string.Format(NOTIF_SELECT_EDITORS_FULL, MAX_ADDITIONAL_EDITORS));
                ExitVehicleSelectionMode();
                return;
            }
            MountVehicleIntoAdditionalEditor(confirmedVehicle);
            ExitVehicleSelectionMode();
        }

        private void MountVehicleIntoAdditionalEditor(Vehicle vehicle) { MountVehicleIntoAdditionalEditor(vehicle, GetFreeAdditionalEditor(), true, true, true, false); }

        // A restore (the SelectedSlots section) must land in its own original editor rather than the first free one, and mount without notifying
        // The trailer auto-mount must mount without claiming Active
        // repositionSelection is True only for the deliberate Select Vehicle confirm, so the silent paths never disturb where the player's mainMenu navigation sits
        // autoMounted picks the notification text only (Mounted vs Selected), and a missing editor is a silent no-op since every call site already guards it
        private void MountVehicleIntoAdditionalEditor(Vehicle vehicle, EditorContext context, bool markAsActive, bool notify, bool repositionSelection, bool autoMounted)
        {
            if (context == null) return;
            // The tree must be whole before TargetVehicle marks it mounted, since every mounted tree's rows are read from then on
            if (!EnsureEditorBuilt(context))
            {
                logger.Log(Dbg.Level.Warn, Dbg.EditorTreeMountRefused(context.Index, modelNames.ResolveModelName(vehicle)));
                return;
            }
            context.TargetVehicle = vehicle;
            ResetEditorContextVehicleState(context);
            CheckPreviewDiffSnapshotCapture(vehicle);
            CaptureUndoAllBaseline(vehicle);
            context.MainMenuItem.Title = string.Format(TITLE_EDIT_SELECTED, modelNames.ResolveModelName(vehicle));
            // Inserted before Select Vehicle's own current position, so mounted editors accumulate above it in mount order
            mainMenu.Add(mainMenu.Items.IndexOf(currentSelectVehicleSlotItem), context.MainMenuItem);
            SyncSelectVehicleSlot();
            // currentSelectVehicleSlotItem is null once this mount fills the last free editor (SyncSelectVehicleSlot just hid it)
            // IndexOf(null) is always -1, which set_SelectedIndex rejects outright, so focus lands on the just mounted row instead of Select Vehicle's own now-gone row
            if (repositionSelection) mainMenu.SelectedIndex = currentSelectVehicleSlotItem != null ? mainMenu.Items.IndexOf(currentSelectVehicleSlotItem) : mainMenu.Items.IndexOf(context.MainMenuItem);
            if (markAsActive) activeContext = context;
            // Unconditional, not just when this mount claims Active: Current's own row and title also depend on whether *any* AdditionalEditor is mounted
            // That just became True regardless of markAsActive, so a silent mount like the trailer auto-mount still needs both refreshed
            ApplyActiveColors();
            RefreshCurrentEditorTitle();
            if (notify) notifications.Notify(NOTIFICATION_KEY_SELECT_VEHICLE, string.Format(autoMounted ? NOTIF_VEHICLE_MOUNTED : NOTIF_VEHICLE_SELECTED, modelNames.ResolveModelName(vehicle)));
        }

        // A script reload keeps live entities, so the mounted vehicle's own Handle is enough and no model lookup is needed
        private void WriteSelectedSlots(StringBuilder sb)
        {
            for (int i = 1; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                if (Entity.IsNullOrNotExisting(context.TargetVehicle)) continue;
                sb.Append(context.Index).Append('|').Append(context.TargetVehicle.Handle).Append('|').Append(context.TargetVehicle.Model.Hash).AppendLine();
            }
        }

        // Loads after the PreviewDiffSnapshot and PhantomSubData sections. Each mount builds its tree first, inside the constructor, where a load already blocks
        // A restored handle resolving to something else (a recycled handle, or an entity another script deleted) is caught by the null check and model hash check below
        private bool ConsumeSelectedSlotsLine(string raw)
        {
            string[] parts = raw.Split('|');
            if (parts.Length != 3) return false;
            int slotIndex, handle, modelHash;
            if (!int.TryParse(parts[0], out slotIndex) || !int.TryParse(parts[1], out handle) || !int.TryParse(parts[2], out modelHash)) return false;
            if (slotIndex < 1 || slotIndex >= editorContexts.Length) return false;
            EditorContext context = editorContexts[slotIndex];
            // Guards against a hand-edited/corrupted file claiming the same slot twice, since the first line already mounted it this pass
            if (!Entity.IsNullOrNotExisting(context.TargetVehicle)) return false;
            Vehicle vehicle = Entity.FromHandle(handle) as Vehicle;
            if (Entity.IsNullOrNotExisting(vehicle) || vehicle.Model.Hash != modelHash) return false;
            // Guards against a hand-edited/corrupted file listing 2 vehicles on the same handling under two different slots, so it mounts once
            if (IsHandlingAlreadyScoped(ResolveHandlingKey(vehicle))) return false;
            MountVehicleIntoAdditionalEditor(vehicle, context, true, false, false, false);
            return true;
        }

        private void CancelVehicleSelection()
        {
            ExitVehicleSelectionMode();
            notifications.Notify(NOTIFICATION_KEY_SELECT_VEHICLE, NOTIF_SELECTION_CANCELED);
        }

        // Deferred until Confirm and Cancel's own input is genuinely released (the SeatWeaponReenable tick stage's own check), not just one tick later
        // The control that confirms or cancels Selection Mode also fires a seat-mounted weapon, and its release can lag the JustPressed edge by several frames
        // A one-tick defer would race a held button into a fresh discharge, so waiting for the release closes that however long the button stays down
        // OnAborted has no tick loop to defer through, so it calls ReenableSeatWeaponSuppression directly
        private bool pendingSeatWeaponReenable;

        // Every way out of the mode ends here, so this is the one place the frozen aim notification is released
        private void ExitVehicleSelectionMode()
        {
            notifications.RemoveFrozenNotification(NOTIFICATION_KEY_SELECT_VEHICLE);
            selectingVehicle = false;
            hoveredVehicleForSelection = null;
            ShowMenu(mainMenu);
            pendingSeatWeaponReenable = true;
        }

        // One model's local bounds, cached by model hash since a model's dimensions never change. Radius is the farthest corner from the origin
        private struct SelectionBounds { public Vector3 Min; public Vector3 Max; public float Radius; }
        private readonly Dictionary<int, SelectionBounds> selectionBoundsByModel = new Dictionary<int, SelectionBounds>();

        private bool selectingVehicle;
        // Set for the one tick EnterVehicleSelectionMode runs, so the Enter press that armed select mode is not also read as an immediate confirm later in that tick
        // The menu library's own processing already read that press earlier in the same tick
        private bool selectModeArmedThisTick;
        private Vehicle hoveredVehicleForSelection;

        // One-shot per tick while select mode is armed. The confirm action reads the result directly instead of raycasting again
        // Shape tests cannot hit a vehicle in superdummy physics (many parked and distant vehicles), so the ray is tested against each loaded vehicle's model bounds
        // The nearest on-screen hit wins, then the occlusion line rejects one hidden behind the world
        // playerVehicle is resolved once by the caller and shared with SuppressSeatMountedWeapon, since Ped.CurrentVehicle builds a fresh wrapper on every access
        private void UpdateVehicleSelectionHover(Vehicle playerVehicle)
        {
            Vector3 origin = GameplayCamera.Position;
            Vector3 direction = GameplayCamera.Direction;
            bool hasPlayerVehicle = !Entity.IsNullOrNotExisting(playerVehicle);
            Vehicle nearestVehicle = null;
            float nearestDistance = SELECT_VEHICLE_RANGE;
            foreach (Vehicle candidate in World.GetAllVehicles())
            {
                // Compares Handle, the vehicle's real identity, since both wrappers are fetched independently
                if (hasPlayerVehicle && candidate.Handle == playerVehicle.Handle) continue;
                float hitDistance;
                if (!RayHitsVehicleBounds(origin, direction, candidate, nearestDistance, out hitDistance) || !candidate.IsOnScreen) continue;
                nearestVehicle = candidate;
                nearestDistance = hitDistance;
            }
            Entity excludeEntity = hasPlayerVehicle ? playerVehicle : null;
            hoveredVehicleForSelection = nearestVehicle != null && !IsSelectionTargetOccluded(origin, origin + direction * nearestDistance, excludeEntity) ? nearestVehicle : null;
        }

        // Slab test of the ray against the vehicle's model bounds in its own local space. A cheap bounding sphere check rejects most vehicles first
        private bool RayHitsVehicleBounds(Vector3 origin, Vector3 direction, Vehicle vehicle, float maxDistance, out float hitDistance)
        {
            hitDistance = 0.0f;
            Model model = vehicle.Model;
            SelectionBounds bounds;
            if (!selectionBoundsByModel.TryGetValue(model.Hash, out bounds))
            {
                model.GetDimensions(out bounds.Min, out bounds.Max);
                float farthestX = Math.Max(Math.Abs(bounds.Min.X), Math.Abs(bounds.Max.X));
                float farthestY = Math.Max(Math.Abs(bounds.Min.Y), Math.Abs(bounds.Max.Y));
                float farthestZ = Math.Max(Math.Abs(bounds.Min.Z), Math.Abs(bounds.Max.Z));
                bounds.Radius = new Vector3(farthestX, farthestY, farthestZ).Length();
                selectionBoundsByModel[model.Hash] = bounds;
            }
            Vector3 position = vehicle.Position;
            Vector3 toCenter = position - origin;
            float along = Vector3.Dot(toCenter, direction);
            if (along < -bounds.Radius || along - bounds.Radius > maxDistance) return false;
            if ((toCenter - direction * along).Length() > bounds.Radius) return false;
            Quaternion inverse = Quaternion.Invert(vehicle.Quaternion);
            Vector3 localOrigin = inverse * (origin - position);
            Vector3 localDirection = inverse * direction;
            float near = 0.0f;
            float far = maxDistance;
            if (!ClipRayToSlab(localOrigin.X, localDirection.X, bounds.Min.X, bounds.Max.X, ref near, ref far)) return false;
            if (!ClipRayToSlab(localOrigin.Y, localDirection.Y, bounds.Min.Y, bounds.Max.Y, ref near, ref far)) return false;
            if (!ClipRayToSlab(localOrigin.Z, localDirection.Z, bounds.Min.Z, bounds.Max.Z, ref near, ref far)) return false;
            hitDistance = near;
            return true;
        }

        private static bool ClipRayToSlab(float origin, float direction, float min, float max, ref float near, ref float far)
        {
            if (Math.Abs(direction) < SELECT_VEHICLE_PARALLEL_EPSILON) return origin >= min && origin <= max;
            float entry = (min - origin) / direction;
            float exit = (max - origin) / direction;
            if (entry > exit)
            {
                float swap = entry;
                entry = exit;
                exit = swap;
            }
            if (entry > near) near = entry;
            if (exit < far) far = exit;
            return near <= far;
        }

        // The bounds pick ignores the world, so a thin line against map geometry and props checks nothing solid sits between the camera and the hit
        // Glass and foliage stay see-through. Synchronous, since the result is read in the same tick
        private static bool IsSelectionTargetOccluded(Vector3 origin, Vector3 hitPosition, Entity excludeEntity)
        {
            Vector3 toHit = hitPosition - origin;
            float distance = toHit.Length();
            if (distance <= SELECT_VEHICLE_OCCLUSION_MARGIN) return false;
            Vector3 lineEnd = origin + toHit * ((distance - SELECT_VEHICLE_OCCLUSION_MARGIN) / distance);
            ShapeTestHandle handle = ShapeTest.StartExpensiveSyncTestLOSProbe(origin, lineEnd, IntersectFlags.Map | IntersectFlags.Objects, excludeEntity);
            ShapeTestResult result;
            return handle.GetResult(out result) == ShapeTestStatus.Ready && result.DidHit;
        }

        // The color tiers a temporary recolor touches, saved so Restore can put them back exactly
        private struct SavedItemColors
        {
            public Color TitleNormal;
            public Color TitleHovered;
            public Color AltTitleNormal;
            public Color AltTitleHovered;
            public Color ArrowsNormal;
            public Color ArrowsHovered;
            public Color TitleDisabled;
            public Color AltTitleDisabled;

            public static SavedItemColors Capture(ColorSet colors)
            {
                SavedItemColors saved = new SavedItemColors();
                saved.TitleNormal = colors.TitleNormal;
                saved.TitleHovered = colors.TitleHovered;
                saved.AltTitleNormal = colors.AltTitleNormal;
                saved.AltTitleHovered = colors.AltTitleHovered;
                saved.ArrowsNormal = colors.ArrowsNormal;
                saved.ArrowsHovered = colors.ArrowsHovered;
                saved.TitleDisabled = colors.TitleDisabled;
                saved.AltTitleDisabled = colors.AltTitleDisabled;
                return saved;
            }

            public void Restore(ColorSet colors)
            {
                colors.TitleNormal = TitleNormal;
                colors.TitleHovered = TitleHovered;
                colors.AltTitleNormal = AltTitleNormal;
                colors.AltTitleHovered = AltTitleHovered;
                colors.ArrowsNormal = ArrowsNormal;
                colors.ArrowsHovered = ArrowsHovered;
                colors.TitleDisabled = TitleDisabled;
                colors.AltTitleDisabled = AltTitleDisabled;
            }
        }

        private Dictionary<NativeItem, SavedItemColors> capturedItemColors = new Dictionary<NativeItem, SavedItemColors>();

        // Called once, immediately after an item that later needs a state-driven color override is constructed, before any other mutation could touch its colors
        // ApplyItemStateColor restores exactly this captured state whenever the item's own override condition is False
        private void CaptureOriginalItemColor(NativeItem item)
        {
            capturedItemColors[item] = SavedItemColors.Capture(item.Colors);
        }

        private static readonly Color ACTIVE_COLOR = Color.LimeGreen;
        // Danger-state cue for a Settings checkbox sitting in a non-default, riskier state (Prompt On Save off, Always Overwrite on)
        private static readonly Color SETTINGS_WARNING_COLOR = Color.Red;

        // Current's own row is only colored when it is Active *and* at least one extra editor is mounted
        // Neither the color nor the title's "(Current)" differentiator (RefreshCurrentEditorTitle) means anything with nothing else to differentiate against
        private void ApplyActiveColors()
        {
            ApplyItemStateColor(currentContext.MainMenuItem, activeContext == currentContext && AnyAdditionalEditorMounted(), ACTIVE_COLOR);
            for (int i = 1; i < editorContexts.Length; i++)
            {
                EditorContext context = editorContexts[i];
                if (context.MainMenuItem == null) continue;
                ApplyItemStateColor(context.MainMenuItem, activeContext == context, ACTIVE_COLOR);
            }
        }

        // The single mechanism behind every state-driven mainMenu and Settings row color (Active green, Settings danger-state red)
        private void ApplyItemStateColor(NativeItem item, bool applyOverride, Color overrideColor)
        {
            ApplyItemStateColor(item, applyOverride, overrideColor, Color.Empty);
        }

        // disabledOverrideColor also recolors the Disabled tier while applyOverride is True, for a row that is shown disabled (an unfocused Preview/Diff
        // row) and must still carry its override cue. Color.Empty leaves that tier at its captured original
        // The revert branch restores the 6 title and value tiers and leaves the arrow tiers alone
        private void ApplyItemStateColor(NativeItem item, bool applyOverride, Color overrideColor, Color disabledOverrideColor)
        {
            if (applyOverride)
            {
                item.Colors.TitleNormal = overrideColor;
                item.Colors.AltTitleNormal = overrideColor;
                item.Colors.TitleHovered = overrideColor;
                item.Colors.AltTitleHovered = overrideColor;
                item.Colors.TitleDisabled = disabledOverrideColor != Color.Empty ? disabledOverrideColor : capturedItemColors[item].TitleDisabled;
                item.Colors.AltTitleDisabled = disabledOverrideColor != Color.Empty ? disabledOverrideColor : capturedItemColors[item].AltTitleDisabled;
            }
            else
            {
                SavedItemColors saved = capturedItemColors[item];
                item.Colors.TitleNormal = saved.TitleNormal;
                item.Colors.AltTitleNormal = saved.AltTitleNormal;
                item.Colors.TitleHovered = saved.TitleHovered;
                item.Colors.AltTitleHovered = saved.AltTitleHovered;
                item.Colors.TitleDisabled = saved.TitleDisabled;
                item.Colors.AltTitleDisabled = saved.AltTitleDisabled;
            }
            item.UpdateColors();
        }

        // Tracks Select Vehicle's own trailing slot in mainMenu, null while hidden (every extra editor mounted)
        private NativeItem currentSelectVehicleSlotItem;

        // respawnVehicleItem is always the correct reinsertion anchor however many mounted editor rows sit above it
        // Mounted rows always insert above Select Vehicle's own position (MountVehicleIntoAdditionalEditor), and Select Vehicle always sits just above Respawn Vehicle
        private void SyncSelectVehicleSlot()
        {
            NativeItem desired = GetFreeAdditionalEditor() != null ? selectVehicleItem : null;
            int insertPosition = mainMenu.Items.IndexOf(respawnVehicleItem);
            SyncMenuSlot(mainMenu, ref currentSelectVehicleSlotItem, desired, insertPosition);
        }

        // A reused editor must show its new vehicle's own values, so the cached custom weapon hashes are dropped and the next sync re-reads them from live data
        private static void ResetEditorContextVehicleState(EditorContext context)
        {
            Array.Clear(context.Weapon.CustomWeaponHashes, 0, context.Weapon.CustomWeaponHashes.Length);
        }

        // The editor's built tree (menus and items) is never discarded, only hidden, so the same context can later be remounted with a different vehicle
        private void UnmountAdditionalEditor(EditorContext context)
        {
            if (context.TargetVehicle == null) return;
            context.TargetVehicle = null;
            ResetEditorContextVehicleState(context);
            mainMenu.Items.Remove(context.MainMenuItem);
            ClampMenuScrollWindow(mainMenu);
            if (mainMenu.Items.Count > 0) mainMenu.SelectedIndex = Math.Max(0, mainMenu.SelectedIndex);
            SyncSelectVehicleSlot();
            if (activeContext == context) activeContext = currentContext;
            // Unconditional, not just when this editor held Active: Current's own title and color also depend on whether *any* extra editor remains mounted
            // The shared actions' own Enabled state must also reflect Active flipping back to Current here, since nothing else in this path calls SyncFieldValues
            ApplyActiveColors();
            RefreshCurrentEditorTitle();
            SyncActiveActionEnablement();
        }

        // If the unmounted vehicle is still the seated truck's TrailerVehicle, it was auto-mounted while physically attached
        // The decline is recorded first, so CheckTrailerAutoMount's free-editor check cannot re-mount it the very next tick
        // Leaving the editor's tree is a defensive net that is a no-op today, since armed-delete is only reachable while mainMenu itself is highlighted and visible
        private void ConfirmUnmountAdditionalEditor(EditorContext context)
        {
            if (Entity.IsNullOrNotExisting(context.TargetVehicle)) return;
            string name = modelNames.ResolveModelName(context.TargetVehicle);
            int unmountedHandle = context.TargetVehicle.Handle;
            Ped player = Game.LocalPlayerPed;
            if (!Entity.IsNullOrNotExisting(player) && player.IsInVehicle())
            {
                Vehicle seatedVehicle = player.CurrentVehicle;
                if (!Entity.IsNullOrNotExisting(seatedVehicle) && !Entity.IsNullOrNotExisting(seatedVehicle.TrailerVehicle) && seatedVehicle.TrailerVehicle.Handle == unmountedHandle) declinedTrailerPerTruck[seatedVehicle.Handle] = unmountedHandle;
            }
            UnmountAdditionalEditor(context);
            LeaveTree(context, mainMenu, false);
            notifications.Notify(NOTIFICATION_KEY_UNMOUNT_SELECTED, string.Format(NOTIF_VEHICLE_UNMOUNTED, name));
        }

        // Set when a seat merge captures the menu the player was viewing inside the merging editor's tree
        // It is consumed once, right after Current's field sync resyncs its item lists to the new vehicle later in the same tick
        // The transplanted SelectedIndex then lands on a menu whose item count already matches (the root, AdvancedData, and Weapon menus resize per vehicle)
        private NativeMenu pendingSeatMergeTargetMenu;
        private int pendingSeatMergeSelectedIndex;

        // If the newly seated vehicle's handling matches an already-mounted extra editor, that editor folds into Current instead of both coexisting on the handling
        // IsHandlingAlreadyScoped only blocks a deliberate Select Vehicle mount, so this closes the passive seat-tracking path it cannot see
        private void CheckAutoMergeSeatCollision(Vehicle occupied)
        {
            HandlingKey handling = ResolveHandlingKey(occupied);
            if (!handling.IsValid) return;
            for (int i = 1; i < editorContexts.Length; i++)
            {
                if (!ResolveHandlingKey(editorContexts[i].TargetVehicle).Equals(handling)) continue;
                MergeSelectedSlotIntoCurrent(editorContexts[i]);
                return;
            }
        }

        // The menu the player is viewing inside the merging editor's tree is captured before unmounting it
        // Current's equivalent menu then picks up the same navigation position with no intermediate visible frame
        // If nothing was captured the merge is silent, and whatever the player is viewing, including Current's own tree, is untouched
        private void MergeSelectedSlotIntoCurrent(EditorContext context)
        {
            NativeMenu capturedMenu = null;
            NativeMenu equivalent = null;
            int capturedSelectedIndex = 0;
            for (int i = 0; i < context.AllOwnMenus.Length; i++)
            {
                if (context.AllOwnMenus[i] != null && context.AllOwnMenus[i].Visible)
                {
                    capturedMenu = context.AllOwnMenus[i];
                    capturedSelectedIndex = capturedMenu.SelectedIndex;
                    equivalent = ResolveEquivalentCurrentMenu(context, i);
                    break;
                }
            }
            // The captured menu hides now and Current's equivalent shows on the SeatMerge stage, once Current's lists match the new vehicle
            // With no equivalent the player lands on mainMenu
            LeaveTree(context, equivalent == null ? mainMenu : null, true);
            UnmountAdditionalEditor(context);
            if (capturedMenu == null || equivalent == null) return;
            pendingSeatMergeTargetMenu = equivalent;
            pendingSeatMergeSelectedIndex = capturedSelectedIndex;
        }

        // Safe because every tree builds its startup menus in one fixed order, and 2 vehicles on the same handling share the literal same HandlingData object
        // A menu past StartupMenuCount is an AdvancedData entry menu created at runtime, in the order that tree first showed it
        // It maps to Current's same entry of the same kind instead, created there if Current has not listed it yet
        private NativeMenu ResolveEquivalentCurrentMenu(EditorContext context, int position)
        {
            if (position < context.StartupMenuCount) return position < currentContext.AllOwnMenus.Length ? currentContext.AllOwnMenus[position] : null;
            NativeMenu entryMenu = context.AllOwnMenus[position];
            for (int k = 0; k < context.AdvancedData.Lists.Length; k++)
            {
                AdvancedDataItemSet[] entries = context.AdvancedData.Lists[k].Entries;
                for (int i = 0; i < entries.Length; i++) if (entries[i].Menu != null && entries[i].Menu == entryMenu) return EnsureAdvancedDataEntryMenu(currentContext, (AdvancedDataMenuKind)k, i).Menu;
            }
            return null;
        }

        // True only while player is seated in *exactly* target, so being seated in some *other* vehicle or on foot both read False
        // The Teleport and Delete Vehicle guards and their hint conditions share it, so all agree on when those actions are no-ops
        private bool IsPlayerAlreadyInEditedVehicle(Ped player, Vehicle target)
        {
            if (Entity.IsNullOrNotExisting(player) || !player.IsInVehicle()) return false;
            return !Entity.IsNullOrNotExisting(target) && player.CurrentVehicle.Handle == target.Handle;
        }

        // Seats the player in the vehicle context edits, from on foot or from a *different* vehicle
        // The hotkey only fires while the Teleport hint shows, so the guards below are silent
        // SetIntoVehicle is ignored while the driver seat is occupied, and a leave task waits for the vehicle to stop or loses to the ped's own tasks
        // Clearing the driver's tasks immediately sets it out of the vehicle within the same call, so the seat is empty for the player on this frame
        private void TeleportToVehicleDriverSeat(EditorContext context)
        {
            Vehicle target = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(target)) return;
            Ped player = Game.LocalPlayerPed;
            if (Entity.IsNullOrNotExisting(player)) return;
            if (IsPlayerAlreadyInEditedVehicle(player, target)) return;
            Ped occupant = target.GetPedOnSeat(VehicleSeat.Driver);
            bool driverRemoved = !Entity.IsNullOrNotExisting(occupant);
            if (driverRemoved) occupant.Task.ClearAllImmediately();
            player.SetIntoVehicle(target, VehicleSeat.Driver);
            logger.Log(Dbg.Level.Info, Dbg.TeleportedToVehicle(modelNames.ResolveModelName(target), target.Model.Hash, driverRemoved));
        }

        // Deletes the vehicle context edits, along with any non-player occupants so none are left floating where it stood
        // No-ops with no vehicle or when the player is seated in it, silently, since the hotkey only fires while the Delete Vehicle hint shows
        // A visible vehicle's disappearance is its own confirmation, so only an off-screen deletion posts a notification
        // The dead-target handling in Tick.cs clears or unmounts the editor afterward
        private void DeleteEditedVehicle(EditorContext context)
        {
            Vehicle target = context.TargetVehicle;
            if (Entity.IsNullOrNotExisting(target)) return;
            Ped player = Game.LocalPlayerPed;
            if (IsPlayerAlreadyInEditedVehicle(player, target)) return;
            bool onScreen = target.IsOnScreen;
            string modelName = modelNames.ResolveModelName(target);
            int modelHash = target.Model.Hash;
            int occupantsDeleted = 0;
            foreach (Ped occupant in target.Occupants)
            {
                if (Entity.IsNullOrNotExisting(occupant) || (!Entity.IsNullOrNotExisting(player) && occupant.Handle == player.Handle)) continue;
                occupant.Delete();
                occupantsDeleted++;
            }
            target.Delete();
            if (!onScreen) notifications.Notify(NOTIFICATION_KEY_DELETE_VEHICLE, string.Format(NOTIF_DELETED, modelName));
            logger.Log(Dbg.Level.Info, Dbg.DeletedVehicle(modelName, modelHash, occupantsDeleted, onScreen));
        }
    }
}
