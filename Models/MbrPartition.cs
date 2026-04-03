using System;

namespace NetImage.Models
{
    public class MbrPartition
    {
        public int Index { get; set; }
        public bool IsBootable { get; set; }
        public byte Type { get; set; }
        public uint StartSector { get; set; }
        public uint SectorCount { get; set; }

        public double SizeMB => SectorCount * 512.0 / (1024.0 * 1024.0);
        public string TypeName => GetTypeName(Type);

        public MbrPartition(int index, bool isBootable, byte type, uint startSector, uint sectorCount)
        {
            Index = index;
            IsBootable = isBootable;
            Type = type;
            StartSector = startSector;
            SectorCount = sectorCount;
        }

        private static string GetTypeName(byte type)
        {
            return type switch
            {
                0x00 => "Empty",
                0x01 => "FAT12",
                0x04 => "FAT16 <32M",
                0x05 => "Extended",
                0x06 => "FAT16 >=32M",
                0x07 => "NTFS/exFAT/HPFS",
                0x0B => "FAT32",
                0x0C => "FAT32 (LBA)",
                0x0E => "FAT16 (LBA)",
                0x0F => "Extended (LBA)",
                0x14 => "Hidden FAT16 <32M",
                0x16 => "Hidden FAT16 >=32M",
                0x1B => "Hidden FAT32",
                0x1C => "Hidden FAT32 (LBA)",
                0x1E => "Hidden FAT16 (LBA)",
                0x42 => "Windows Dynamic (SFS)",
                0x82 => "Linux swap",
                0x83 => "Linux native",
                0x8E => "Linux LVM",
                0xA5 => "FreeBSD",
                0xA6 => "OpenBSD",
                0xA8 => "macOS UFS",
                0xA9 => "NetBSD",
                0xAB => "macOS boot",
                0xAF => "macOS HFS/HFS+",
                0xEB => "BeOS BFS",
                0xEE => "EFI GPT",
                0xEF => "EFI FAT",
                _ => $"Unknown (0x{type:X2})"
            };
        }
    }
}
