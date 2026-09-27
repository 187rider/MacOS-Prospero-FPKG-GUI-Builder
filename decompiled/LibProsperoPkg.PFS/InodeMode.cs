using System;

namespace LibProsperoPkg.PFS;

/// <summary>
/// Inode mode flags including user/group/other permissions.
/// </summary>
[Flags]
public enum InodeMode : ushort
{
	o_read = 1,
	o_write = 2,
	o_execute = 4,
	g_read = 8,
	g_write = 0x10,
	g_execute = 0x20,
	u_read = 0x40,
	u_write = 0x80,
	u_execute = 0x100,
	dir = 0x4000,
	file = 0x8000
}
