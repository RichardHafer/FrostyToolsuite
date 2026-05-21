using AssetBankPlugin.Ant;
using AssetBankPlugin.Enums;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using System;
using System.Collections.Generic;
using System.Text;

namespace AssetBankPlugin.GenericData
{
    /// <summary>
    /// Best-guess metadata for one Dead Space Remake animation block.
    /// We split the bank file into N equal slices (N = header field off04)
    /// and look for the canonical "00 01 02 03 ..." LUT inside each slice.
    /// </summary>
    public class DeadSpaceAnimBlock
    {
        public int Index;
        public long BlockStart;
        public long BlockEnd;
        public long LutOffset;   // -1 if not found
        public int LutLength;    // best-guess bone/channel count
        public Guid SyntheticId;
        public int BundleId;
        public long StreamLength;
        // The raw resource bytes are kept around so a later phase can extract
        // keyframes without re-reading from the AssetManager.
        public byte[] BankBytes;
    }

    public class Bank
    {
        // Not an enum because it differs between games.
        public uint PackagingType { get; set; }
        // Raw Sections
        public List<Section> Sections { get; set; } = new List<Section>();
        // Deserialized Sections
        public Dictionary<uint, GenericClass> Classes { get; set; } = new Dictionary<uint, GenericClass>();
        public Dictionary<string, Guid> DataNames { get; set; } = new Dictionary<string, Guid>();

        // Dead Space Remake: synthetic-Guid -> block descriptor (populated by ParseDeadSpaceBank).
        // The map is static so the export pipeline can look blocks up by Guid later.
        public static Dictionary<Guid, DeadSpaceAnimBlock> DeadSpaceBlocks { get; }
            = new Dictionary<Guid, DeadSpaceAnimBlock>();

        public Bank(NativeReader r, int bundleId)
        {
            PackagingType = r.ReadUInt(Endian.Big);

            App.Logger.Log("[AntBank] PackagingType=0x{0:X8}, StreamLength={1} bytes",
                PackagingType, r.BaseStream.Length);

            // Dead Space Remake (and potentially other newer Frostbite 2023 games) use a
            // fundamentally different binary bank format — not the GD.XXXX section layout.
            // Run diagnostics, then a best-guess parser that splits the file into N equal
            // animation blocks and registers them in DataNames.
            if ((ProfileVersion)ProfilesLibrary.DataVersion == ProfileVersion.DeadSpace)
            {
                App.Logger.Log("[AntBank] Dead Space Remake bank format (PackagingType=0x{0:X8}) — running diagnostics + best-guess parser.", PackagingType);
                DiagnoseDeadSpaceBank(r);
                ParseDeadSpaceBank(r, bundleId);
                return;
            }

            // Dump the first 128 bytes for format analysis when the bank format is unknown.
            LogHexDump(r, 0, 128);

            switch ((ProfileVersion)ProfilesLibrary.DataVersion)
            {
                case ProfileVersion.PlantsVsZombiesGardenWarfare2:
                case ProfileVersion.PlantsVsZombiesGardenWarfare:
                case ProfileVersion.Battlefield4:
                case ProfileVersion.Battlefield1:
                    {
                        // AnimationSets have special AntRef mappings (Guid followed by AntRefId).
                        if (PackagingType == 3)
                        {
                            r.BaseStream.Position = 56;
                            uint antRefMapCount = r.ReadUInt(Endian.Big) / 20;

                            for (int i = 0; i < antRefMapCount; i++)
                            {
                                Guid a = r.ReadGuid();
                                byte[] bytes = new byte[16];
                                bytes[0] = r.ReadByte();
                                bytes[1] = r.ReadByte();
                                bytes[2] = r.ReadByte();
                                bytes[3] = r.ReadByte();

                                Guid b = new Guid(bytes);
                                AntRefTable.InternalRefs[a] = b;
                                Cache.AntRefMap[a] = b;
                            }
                            r.BaseStream.Position = 4;
                        }
                    }
                    break;
            }

            uint headerStart = (uint)r.BaseStream.Position;
            uint headerSize;

            // Some AntPackages seem to have no header. In that case, jump directly to reading the sections.
            string str = r.ReadSizedString(3);
            bool hasHeader = str != "GD.";
            r.BaseStream.Position -= 3;
            if (hasHeader)
                headerSize = r.ReadUInt(Endian.Big);
            else
                headerSize = 0;

            App.Logger.Log("[AntBank] hasHeader={0}, headerStart={1}, headerSize={2}, sectionsStart={3}",
                hasHeader, headerStart, headerSize, headerStart + headerSize);

            r.BaseStream.Position = headerStart + headerSize;

            // Read all sections.
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                Section s = Section.ReadSection(r);
                if (s != null)
                    Sections.Add(s);
            }

            // Get the data out of all sections.
            // Theoretically there should only be one STRM and one REFL section but if we have multiple, just take the last one.
            for (int i = 0; i < Sections.Count; i++)
            {
                var section = Sections[i];
                if (section is SectionStrm strmSection)
                {
                    
                }
                else if (section is SectionRefl reflSection)
                {
                    Classes = reflSection.Classes;
                }
                else if (section is SectionData dataSection)
                {
                    var asset = AntAsset.Deserialize(r, dataSection, Classes, this);
                    if (asset != null)
                    {
                        int index = 0;
                        string name = asset.Name;
                        while (DataNames.ContainsKey(name))
                        {
                            name = asset.Name + " [" + index + "]";
                            index++;
                        }
                        DataNames.Add(name, asset.ID);
                        Cache.AntStateBundleIndices[asset.ID] = bundleId;
                    }
                }
            }
        }

        /// <summary>
        /// Extended diagnostic dump for Dead Space Remake's unknown bank format.
        /// Logs the first 512 bytes, interprets header fields, then dumps 128 bytes at
        /// several evenly-spaced offsets so we can spot repeating section boundaries.
        /// Stream position is fully restored afterwards.
        /// </summary>
        private static void DiagnoseDeadSpaceBank(NativeReader r)
        {
            long savedPos = r.BaseStream.Position;
            long len = r.BaseStream.Length;

            // --- 1. First 512 bytes ---
            LogHexDump(r, 0, 512);

            // --- 2. Interpret known header fields (all big-endian guesses) ---
            uint f4 = 0, f8 = 0, f12 = 0, f16 = 0, f20 = 0;
            if (len >= 24)
            {
                r.BaseStream.Position = 4;
                f4  = r.ReadUInt(Endian.Big);  // offset 4
                f8  = r.ReadUInt(Endian.Big);  // offset 8
                f12 = r.ReadUInt(Endian.Big);  // offset 12
                f16 = r.ReadUInt(Endian.Big);  // offset 16
                f20 = r.ReadUInt(Endian.Big);  // offset 20
                App.Logger.Log("{0}", $"[AntBank][DSR] Header fields (BE): " +
                    $"off04=0x{f4:X8}({f4}) off08=0x{f8:X8}({f8}) off0C=0x{f12:X8}({f12}) " +
                    $"off10=0x{f16:X8}({f16}) off14=0x{f20:X8}({f20})");
            }

            // --- 3. Probe the header field VALUES as if they were offsets ---
            // Header fields 1145/614/2290 are very likely offsets to sub-structures.
            // Dump 128 bytes at each so we can identify what they point to.
            foreach (uint candidate in new[] { f4, f8, f12, f16, f20 })
            {
                if (candidate >= 24 && candidate + 16 < len)
                    LogHexDump(r, candidate, 128);
            }

            // --- 4. Find the first ASCII string of length >= 4 — likely a string table ---
            FindAndLogFirstStringTable(r, len);

            // --- 5. Dump 128 bytes at 5 evenly-spaced positions through the file ---
            long[] probeOffsets = new long[]
            {
                len / 5,
                len * 2 / 5,
                len * 3 / 5,
                len * 4 / 5,
                len - 128 < 0 ? 0 : len - 128,
            };
            foreach (long off in probeOffsets)
            {
                if (off >= 0 && off < len)
                    LogHexDump(r, off, 128);
            }

            r.BaseStream.Position = savedPos;
        }

        /// <summary>
        /// Scans the stream for the first run of >=4 consecutive printable ASCII bytes
        /// (terminated by NUL or non-printable), then dumps 256 bytes from that location.
        /// This typically lands on the string table (bone names, animation names).
        /// </summary>
        private static void FindAndLogFirstStringTable(NativeReader r, long len)
        {
            const int Chunk = 65536;
            long savedPos = r.BaseStream.Position;
            long pos = 0;
            byte[] buf = new byte[Chunk];

            while (pos < len)
            {
                r.BaseStream.Position = pos;
                int toRead = (int)Math.Min(Chunk, len - pos);
                int read = r.BaseStream.Read(buf, 0, toRead);
                if (read <= 0) break;

                int run = 0;
                int runStart = -1;
                for (int i = 0; i < read; i++)
                {
                    byte b = buf[i];
                    bool printable = (b >= 0x20 && b < 0x7F);
                    if (printable)
                    {
                        if (run == 0) runStart = i;
                        run++;
                        if (run >= 6) // require 6+ chars to skip incidental junk
                        {
                            long absolute = pos + runStart;
                            App.Logger.Log("[AntBank][DSR] First long ASCII run found at offset 0x{0:X8}", absolute);
                            r.BaseStream.Position = savedPos;
                            LogHexDump(r, absolute, 256);
                            return;
                        }
                    }
                    else
                    {
                        run = 0;
                        runStart = -1;
                    }
                }
                pos += read;
            }

            App.Logger.Log("[AntBank][DSR] No ASCII string run found in file.");
            r.BaseStream.Position = savedPos;
        }

        /// <summary>
        /// Dead Space Remake best-guess parser (Phase 1).
        /// 1. Reads animation count from header field at offset 4 (big-endian uint32).
        /// 2. Splits the bank stream into N equal slices.
        /// 3. In each slice, searches for the canonical "00 01 02 03 ..." LUT pattern
        ///    (we have observed this LUT in every animation block in the diagnostic dump).
        /// 4. Registers each block in <see cref="DataNames"/> with a deterministic
        ///    synthetic GUID so the asset shows up in the editor and can be retrieved
        ///    later for export.
        /// </summary>
        private void ParseDeadSpaceBank(NativeReader r, int bundleId)
        {
            long len = r.BaseStream.Length;
            if (len < 32)
            {
                App.Logger.LogWarning("[AntBank][DSR] Bank too small ({0} bytes) — skipping parser.", len);
                return;
            }

            // Header field off04 = animation count (confirmed by diagnostic analysis).
            r.BaseStream.Position = 4;
            int animCount = (int)r.ReadUInt(Endian.Big);
            if (animCount <= 0 || animCount > 1024)
            {
                App.Logger.LogWarning("[AntBank][DSR] Implausible anim count {0} from header off04 — aborting parser.", animCount);
                return;
            }

            App.Logger.Log("[AntBank][DSR] Detected {0} animation blocks. Splitting file ({1} bytes) into equal slices.", animCount, len);

            // Snapshot the bank bytes once. The stream is consumed inside the Bank
            // constructor's caller and may not be re-readable, so we keep a copy for
            // the later keyframe-extraction phase.
            long savedPos = r.BaseStream.Position;
            r.BaseStream.Position = 0;
            byte[] bankBytes = r.ReadBytes((int)Math.Min(len, int.MaxValue));
            r.BaseStream.Position = savedPos;

            long sliceSize = len / animCount;

            for (int i = 0; i < animCount; i++)
            {
                long blockStart = i * sliceSize;
                long blockEnd = (i == animCount - 1) ? len : blockStart + sliceSize;

                long lutOffset = FindCanonicalLut(bankBytes, blockStart, blockEnd);
                int lutLength = lutOffset >= 0 ? MeasureLutLength(bankBytes, lutOffset, blockEnd) : 0;

                Guid syntheticId = MakeSyntheticDsrGuid(bundleId, i);

                var block = new DeadSpaceAnimBlock
                {
                    Index = i,
                    BlockStart = blockStart,
                    BlockEnd = blockEnd,
                    LutOffset = lutOffset,
                    LutLength = lutLength,
                    SyntheticId = syntheticId,
                    BundleId = bundleId,
                    StreamLength = len,
                    BankBytes = bankBytes, // shared across blocks; same backing buffer
                };

                string name = $"DSR_Anim_{i:D2}";
                int dupIdx = 0;
                string finalName = name;
                while (DataNames.ContainsKey(finalName))
                    finalName = $"{name} [{dupIdx++}]";

                DataNames[finalName] = syntheticId;
                DeadSpaceBlocks[syntheticId] = block;
                Cache.AntStateBundleIndices[syntheticId] = bundleId;

                App.Logger.Log(
                    "[AntBank][DSR] Block {0}: range=[0x{1:X8}..0x{2:X8}] lut=0x{3} lutLen={4} guid={5} name={6}",
                    i,
                    blockStart,
                    blockEnd,
                    lutOffset >= 0 ? $"{lutOffset:X8}" : "------",
                    lutLength,
                    syntheticId,
                    finalName);
            }
        }

        /// <summary>
        /// Searches for the canonical "00 01 02 03 04 05 06 07" lookup-table pattern
        /// in [start, end) and returns the absolute offset of the first match, or -1.
        /// Requires at least 8 sequential bytes to avoid false positives.
        /// </summary>
        private static long FindCanonicalLut(byte[] buf, long start, long end)
        {
            const int MinSeq = 8;
            long limit = Math.Min(end, buf.LongLength) - MinSeq;
            for (long i = start; i < limit; i++)
            {
                bool match = true;
                for (int j = 0; j < MinSeq; j++)
                {
                    if (buf[i + j] != (byte)j) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// Starting at <paramref name="lutOffset"/>, counts how many consecutive bytes
        /// equal their index (i.e. how far the "00 01 02 03 ..." sequence extends).
        /// Returns the LUT length in bytes (== best-guess bone/channel count).
        /// </summary>
        private static int MeasureLutLength(byte[] buf, long lutOffset, long maxEnd)
        {
            long maxLen = Math.Min(maxEnd - lutOffset, 1024);
            int n = 0;
            while (n < maxLen && lutOffset + n < buf.LongLength && buf[lutOffset + n] == (byte)n)
                n++;
            return n;
        }

        /// <summary>
        /// Builds a deterministic synthetic GUID for a DSR animation block so the
        /// same block in the same bundle always gets the same id across loads.
        /// Layout: [bundleId:4][index:4][0:4][magic 'DEAD5B05':4]
        /// </summary>
        private static Guid MakeSyntheticDsrGuid(int bundleId, int index)
        {
            byte[] g = new byte[16];
            BitConverter.GetBytes(bundleId).CopyTo(g, 0);
            BitConverter.GetBytes(index).CopyTo(g, 4);
            g[12] = 0xDE;
            g[13] = 0xAD;
            g[14] = 0x5B;
            g[15] = 0x05;
            return new Guid(g);
        }

        /// <summary>
        /// Logs <paramref name="count"/> bytes starting at absolute stream offset
        /// <paramref name="offset"/> as a hex dump. Stream position is restored afterwards.
        /// </summary>
        private static void LogHexDump(NativeReader r, long offset, int count)
        {
            long savedPos = r.BaseStream.Position;
            r.BaseStream.Position = offset;
            int toRead = (int)Math.Min(count, r.BaseStream.Length - offset);
            if (toRead <= 0) { r.BaseStream.Position = savedPos; return; }

            byte[] buf = r.ReadBytes(toRead);
            r.BaseStream.Position = savedPos;

            var sb = new StringBuilder();
            sb.AppendLine($"[AntBank] Hex dump at offset 0x{offset:X8}, {toRead} bytes:");
            for (int i = 0; i < toRead; i += 16)
            {
                sb.Append($"  {offset + i:X8}: ");
                int lineLen = Math.Min(16, toRead - i);
                for (int j = 0; j < lineLen; j++)
                    sb.Append($"{buf[i + j]:X2} ");
                for (int j = lineLen; j < 16; j++)
                    sb.Append("   "); // pad short last line
                sb.Append("  ");
                for (int j = 0; j < lineLen; j++)
                    sb.Append(buf[i + j] >= 0x20 && buf[i + j] < 0x7F ? (char)buf[i + j] : '.');
                sb.AppendLine();
            }
            App.Logger.Log("{0}", sb.ToString());
        }
    }
}
