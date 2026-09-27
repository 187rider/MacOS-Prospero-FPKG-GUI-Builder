using System;

namespace LibProsperoPkg.PFS;

/// <summary>
/// PFS mode flags.
/// </summary>
[Flags]
public enum PfsMode : ushort
{
	None = 0,
	Signed = 1,
	Is64Bit = 2,
	Encrypted = 4,
	UnknownFlagAlwaysSet = 8,
	/// <summary>PPR direct-offset inode profile used by publisher NAPS images.</summary>
	PprDirectOffsets = 0x10
}
