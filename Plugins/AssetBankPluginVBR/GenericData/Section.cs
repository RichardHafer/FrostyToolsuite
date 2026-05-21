using Frosty.Core;
using FrostySdk.IO;

namespace AssetBankPlugin.GenericData
{
    public abstract class Section
    {
        public abstract Endian Endianness { get; set; }
        public abstract uint DataSize { get; set; }
        public abstract uint DataOffset { get; set; }

        /// <summary>
        /// Reads a <see cref="Section"/> of an AntPackage asset bank and returns it.
        /// </summary>
        /// <param name="r"></param>
        /// <returns></returns>
        public static Section ReadSection(NativeReader r)
        {
            // Read type and go back to the beginning of the block.
            string blockType = r.ReadSizedString(7);
            Endian endian = r.ReadSizedString(1) == "b" ? Endian.Big : Endian.Little;
            r.BaseStream.Position -= 8;

            // Read the section.
            Section result = null;
            switch (blockType)
            {
                case SectionStrm.Identifier:
                    result = new SectionStrm(r, endian);
                    break;
                case SectionRefl.Identifier:
                    result = new SectionRefl(r, endian);
                    break;
                case SectionData.Identifier:
                    result = new SectionData(r, endian);
                    break;
                default:
                    // Unknown section type (e.g. Dead Space Remake uses additional section types).
                    // All sections share the same 16-byte header: 8 bytes identifier+endian, 4 bytes total
                    // DataSize (from section start), 4 bytes extra. Skip past this section using DataSize.
                    App.Logger.LogWarning("AntBank: Unknown section type '{0}' at offset {1} — skipping.",
                        blockType, r.BaseStream.Position);
                    try
                    {
                        long sectionStart = r.BaseStream.Position;
                        _ = r.ReadSizedString(8); // type (7) + endian (1)
                        uint skipSize = r.ReadUInt(endian);
                        if (skipSize > 0 && sectionStart + skipSize <= r.BaseStream.Length)
                            r.BaseStream.Position = sectionStart + skipSize;
                        else
                            r.BaseStream.Position = r.BaseStream.Length; // abort
                    }
                    catch
                    {
                        r.BaseStream.Position = r.BaseStream.Length; // abort on any read error
                    }
                    break;
            }

            return result;
        }
    }
}
