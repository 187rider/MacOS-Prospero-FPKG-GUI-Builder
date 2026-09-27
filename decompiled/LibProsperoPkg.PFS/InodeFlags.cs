using System;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Inode flags for special PFS features
/// </summary>
[Flags]
public enum InodeFlags : uint
{
	compressed = 1u,
	unk1 = 2u,
	unk2 = 4u,
	unk3 = 8u,
	@readonly = 0x10u,
	unk4 = 0x20u,
	unk5 = 0x40u,
	unk6 = 0x80u,
	unk7 = 0x100u,
	unk8 = 0x200u,
	unk9 = 0x400u,
	unk10 = 0x800u,
	unk11 = 0x1000u,
	unk12 = 0x2000u,
	unk13 = 0x4000u,
	unk14 = 0x8000u,
	unk15 = 0x10000u,
	@internal = 0x20000u
}
