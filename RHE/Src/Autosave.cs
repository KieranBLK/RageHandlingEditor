using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // Handlings with a content change not yet written to their autosave, drained once per tick so a burst of changes in one tick writes once
        private HashSet<HandlingKey> pendingAutosaveHandlings = new HashSet<HandlingKey>();
        // Handlings whose handling name mismatch was already logged by an autosave. The check repeats on every write, the warning only once per handling
        private HashSet<HandlingKey> autosaveMismatchLoggedHandlings = new HashSet<HandlingKey>();

        private void MarkAutosavePending(HandlingKey handling)
        {
            if (!autosaveEnabled) return;
            pendingAutosaveHandlings.Add(handling);
        }

        // Marks Current's vehicle and every mounted editor's vehicle pending, so switching Autosave on from the menu catches their autosaves up at once
        private void MarkEditedHandlingsAutosavePending()
        {
            for (int i = 0; i < editorContexts.Length; i++)
            {
                if (!Entity.IsNullOrNotExisting(editorContexts[i].TargetVehicle)) MarkAutosavePending(ResolveHandlingKey(editorContexts[i].TargetVehicle));
            }
        }

        private void FlushPendingAutosaves()
        {
            if (pendingAutosaveHandlings.Count == 0) return;
            var handlings = new List<HandlingKey>(pendingAutosaveHandlings);
            pendingAutosaveHandlings.Clear();
            foreach (HandlingKey handling in handlings)
            {
                Vehicle vehicle = ResolveLiveVehicleOfHandling(handling);
                if (vehicle == null)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.AutosaveNoVehicle(modelNames.DescribeHandling(handling)));
                    continue;
                }
                WriteAutosave(vehicle);
            }
        }

        // A live vehicle on this handling: Current's target first, then each mounted editor's, then the last vehicle the handling's own undo history saw
        private Vehicle ResolveLiveVehicleOfHandling(HandlingKey handling)
        {
            for (int i = 0; i < editorContexts.Length; i++)
            {
                if (IsLiveVehicleOfHandling(editorContexts[i].TargetVehicle, handling)) return editorContexts[i].TargetVehicle;
            }
            UndoHistory history;
            if (undoHistories.TryGetValue(handling, out history) && IsLiveVehicleOfHandling(history.LastKnownVehicle, handling)) return history.LastKnownVehicle;
            return null;
        }

        private static bool IsLiveVehicleOfHandling(Vehicle vehicle, HandlingKey handling)
        {
            return handling.IsValid && ResolveHandlingKey(vehicle).Equals(handling);
        }

        // Always overwrites the file named by its model name, like Save, while the document holds the handling name
        // Silent on success
        private void WriteAutosave(Vehicle vehicle)
        {
            string name = modelNames.ResolveModelName(vehicle);
            if (!IsUsableFileName(name))
            {
                logger.Log(Dbg.Level.Warn, Dbg.AutosaveUnusableName(name));
                return;
            }
            try
            {
                if (!Directory.Exists(autosavesSource.Folder)) Directory.CreateDirectory(autosavesSource.Folder);
                HandlingKey handling = ResolveHandlingKey(vehicle);
                string handlingName = modelNames.ResolveHandlingName(vehicle);
                XDocument doc = handlingMetaXml.BuildHandlingXml(vehicle.HandlingData, handlingName, ResolveHandlingFieldGates(vehicle), ResolveExportOverlay(handling));
                MarkHandlingNameMismatch(doc, "Autosave", handlingName, vehicle, false, !autosaveMismatchLoggedHandlings.Contains(handling));
                autosaveMismatchLoggedHandlings.Add(handling);
                string content = doc.Declaration.ToString() + Environment.NewLine + Environment.NewLine + doc.Root.ToString();
                if (!WriteVerifiedHandling(SavedHandlingPath(new SavedHandlingKey(autosavesSource, name)), content))
                {
                    logger.Log(Dbg.Level.Error, Dbg.AutosaveFailed(name, "did not read back as a valid handling"));
                    return;
                }
                logger.Log(Dbg.Level.Info, Dbg.AutosaveWritten(name));
            }
            catch (Exception ex) { logger.Log(Dbg.Level.Error, Dbg.AutosaveFailed(name, ex.Message)); }
        }
    }
}
