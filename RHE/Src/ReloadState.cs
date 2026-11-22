using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using GTA;

namespace RHE
{
    public partial class RageHandlingEditor : Script
    {
        // The kinds of state a script reload carries, in the order the file lists them and the constructor loads them. The names are written to the file
        private enum ReloadStateSection { UndoHistory, PhantomSubData, PreviewDiffSnapshot, SelectedSlots, MenuRestore, UpdateCheck }

        private readonly IReloadStateFile reloadState;

        private class ReloadStateProvider
        {
            public ReloadStateSection Section;
            public Action<StringBuilder> Write;
            public Func<string, bool> ConsumeLine;
        }

        // Listed in ReloadStateSection order, since the file indexes the array by section
        private ReloadStateProvider[] BuildReloadStateProviders()
        {
            return new[]
            {
                ReloadStateFile.BuildReloadStateProvider(ReloadStateSection.UndoHistory, WriteUndoHistory, ConsumeUndoHistoryLine),
                ReloadStateFile.BuildReloadStateProvider(ReloadStateSection.PhantomSubData, WritePhantomSubDataHistory, ConsumePhantomSubDataLine),
                ReloadStateFile.BuildReloadStateProvider(ReloadStateSection.PreviewDiffSnapshot, WritePreviewDiffSnapshot, ConsumePreviewDiffSnapshotLine),
                ReloadStateFile.BuildReloadStateProvider(ReloadStateSection.SelectedSlots, WriteSelectedSlots, ConsumeSelectedSlotsLine),
                ReloadStateFile.BuildReloadStateProvider(ReloadStateSection.MenuRestore, WriteMenuRestore, ConsumeMenuRestoreLine),
                ReloadStateFile.BuildReloadStateProvider(ReloadStateSection.UpdateCheck, updateChecker.WriteReloadStateLines, updateChecker.ConsumeReloadStateLine)
            };
        }

        private static string EncodeSnapshot(XDocument snapshot)
        {
            return snapshot == null ? EMPTY_SNAPSHOT_MARKER : Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshot.ToString()));
        }

        private static XDocument DecodeSnapshot(string encoded)
        {
            return encoded == EMPTY_SNAPSHOT_MARKER ? null : XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }

        private interface IReloadStateFile
        {
            void WriteReloadState();
            void ReadReloadState();
            void LoadReloadStateSection(ReloadStateSection section);
            string FormatReloadStateHandlingHash(HandlingKey key);
            bool ResolveReloadStateHandling(ReloadStateSection section, string hashText, out HandlingKey key);
        }

        // A section's lines reach only its own consumer
        private sealed class ReloadStateFile : IReloadStateFile
        {
            private readonly ILogger logger;
            private readonly IModelNameResolver modelNames;
            // Indexed by ReloadStateSection
            private readonly ReloadStateProvider[] reloadStateProviders;
            // Each section's hash texts resolved during its load, indexed by ReloadStateSection and cleared once that load finishes
            private readonly Dictionary<string, HandlingKey>[] resolvedHashes;
            // The lines the file held for each section, kept from ReadReloadState until that section's load runs
            private readonly Dictionary<ReloadStateSection, List<string>> reloadStateLines = new Dictionary<ReloadStateSection, List<string>>();

            public ReloadStateFile(ILogger fileLogger, IModelNameResolver nameResolver, ReloadStateProvider[] providers)
            {
                logger = fileLogger;
                modelNames = nameResolver;
                reloadStateProviders = providers;
                resolvedHashes = new Dictionary<string, HandlingKey>[providers.Length];
                for (int i = 0; i < providers.Length; i++) resolvedHashes[i] = new Dictionary<string, HandlingKey>();
            }

            public static ReloadStateProvider BuildReloadStateProvider(ReloadStateSection section, Action<StringBuilder> write, Func<string, bool> consumeLine)
            {
                return new ReloadStateProvider { Section = section, Write = write, ConsumeLine = consumeLine };
            }

            // When the game process started, in UTC ticks. A reload keeps the process and a relaunch does not, so this tells them apart at any delay
            private static long ReadProcessStartTicks(out string failure)
            {
                failure = null;
                try
                {
                    using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess()) return process.StartTime.ToUniversalTime().Ticks;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    return 0;
                }
            }

            // One section failing to write drops only that section. A file with no section is deleted, so nothing stale outlives a clean abort
            public void WriteReloadState()
            {
                string startFailure;
                long startTicks = ReadProcessStartTicks(out startFailure);
                if (startFailure != null) logger.Log(Dbg.Level.Warn, Dbg.ReloadStateHeaderUnreadable(startFailure));
                var document = new StringBuilder();
                document.Append(startTicks).AppendLine();
                var written = new StringBuilder();
                int sectionCount = 0;
                for (int i = 0; i < reloadStateProviders.Length; i++)
                {
                    ReloadStateProvider provider = reloadStateProviders[i];
                    var lines = new StringBuilder();
                    try { provider.Write(lines); }
                    catch (Exception ex)
                    {
                        logger.Log(Dbg.Level.Warn, Dbg.ReloadStateWriteFailed(provider.Section.ToString(), ex.Message));
                        continue;
                    }
                    if (lines.Length == 0) continue;
                    string sectionText = lines.ToString();
                    int lineCount = 0;
                    for (int j = 0; j < sectionText.Length; j++) if (sectionText[j] == '\n') lineCount++;
                    document.Append(RELOAD_STATE_SECTION_OPEN).Append(provider.Section.ToString()).Append(RELOAD_STATE_SECTION_CLOSE).AppendLine().Append(sectionText);
                    written.Append(sectionCount > 0 ? " | " : "").Append(provider.Section.ToString()).Append('=').Append(lineCount);
                    sectionCount++;
                }
                if (sectionCount == 0)
                {
                    if (File.Exists(RELOAD_STATE_PATH)) File.Delete(RELOAD_STATE_PATH);
                    return;
                }
                string directory = Path.GetDirectoryName(RELOAD_STATE_PATH);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                WriteFileReplacing(RELOAD_STATE_PATH, document.ToString());
                logger.Log(Dbg.Level.Info, Dbg.ReloadStateWritten(written.ToString()));
            }

            // Reads the file once and deletes it, so a state that never loads cannot outlive the reload that read it
            // The sections load later at their own constructor positions, each one only after what it depends on exists
            // Every section is session state, so a file another game launch wrote is discarded whole
            // Export-only categories outlive a relaunch in each handling's autosave instead
            public void ReadReloadState()
            {
                reloadStateLines.Clear();
                if (!File.Exists(RELOAD_STATE_PATH)) return;
                string[] lines;
                try { lines = File.ReadAllLines(RELOAD_STATE_PATH); }
                catch (Exception ex)
                {
                    logger.Log(Dbg.Level.Warn, Dbg.ReloadStateReadFailed(ex.Message));
                    return;
                }
                string startFailure;
                long startTicks = ReadProcessStartTicks(out startFailure);
                long fileTicks = 0;
                bool headerParsed = lines.Length > 0 && long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out fileTicks);
                string headerCause = startFailure;
                if (headerCause == null && !headerParsed) headerCause = "The first line is not a start time";
                if (headerCause != null) logger.Log(Dbg.Level.Warn, Dbg.ReloadStateHeaderUnreadable(headerCause));
                if (headerCause == null && fileTicks == startTicks)
                {
                    List<string> sectionLines = null;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (line.Length == 0) continue;
                        if (line[0] == RELOAD_STATE_SECTION_OPEN) sectionLines = OpenReloadStateSection(line);
                        else if (sectionLines != null) sectionLines.Add(line);
                    }
                }
                else logger.Log(Dbg.Level.Info, Dbg.ReloadStateOtherLaunch());
                try { File.Delete(RELOAD_STATE_PATH); }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.ReloadStateDeleteFailed(ex.Message)); }
            }

            private List<string> OpenReloadStateSection(string headerLine)
            {
                ReloadStateSection section;
                string name = headerLine.Length > 2 && headerLine[headerLine.Length - 1] == RELOAD_STATE_SECTION_CLOSE ? headerLine.Substring(1, headerLine.Length - 2) : null;
                if (name == null || !Enum.TryParse(name, out section) || !Enum.IsDefined(typeof(ReloadStateSection), section)) return null;
                return GetOrAdd(reloadStateLines, section);
            }

            // A consumer that throws drops the rest of its own section only, so the other sections still load
            public void LoadReloadStateSection(ReloadStateSection section)
            {
                List<string> sectionLines;
                if (!reloadStateLines.TryGetValue(section, out sectionLines)) return;
                reloadStateLines.Remove(section);
                ReloadStateProvider provider = reloadStateProviders[(int)section];
                int loaded = 0;
                try
                {
                    for (int i = 0; i < sectionLines.Count; i++) if (provider.ConsumeLine(sectionLines[i])) loaded++;
                }
                catch (Exception ex) { logger.Log(Dbg.Level.Warn, Dbg.ReloadStateSectionFailed(section.ToString(), ex.Message)); }
                resolvedHashes[(int)section].Clear();
                if (loaded > 0) logger.Log(Dbg.Level.Info, Dbg.ReloadStateSectionLoaded(section.ToString(), loaded));
            }

            // The key's verified name hash as the 8 hex digits every handling-keyed line starts with, or null when it does not verify
            public string FormatReloadStateHandlingHash(HandlingKey key)
            {
                int nameHash;
                return modelNames.ReadHandlingNameHash(key, out nameHash) ? nameHash.ToString("X8") : null;
            }

            // Reads the handling a line names. A hash text that does not parse or names no handling skips every line of that hash, with one WARN per hash
            public bool ResolveReloadStateHandling(ReloadStateSection section, string hashText, out HandlingKey key)
            {
                Dictionary<string, HandlingKey> sectionHashes = resolvedHashes[(int)section];
                if (!sectionHashes.TryGetValue(hashText, out key))
                {
                    int nameHash;
                    key = int.TryParse(hashText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nameHash) ? ResolveHandlingKeyFromNameHash(nameHash) : new HandlingKey();
                    sectionHashes[hashText] = key;
                    if (!key.IsValid) logger.Log(Dbg.Level.Warn, Dbg.ReloadStateHandlingUnresolved(section.ToString(), hashText));
                }
                return key.IsValid;
            }
        }
    }
}
