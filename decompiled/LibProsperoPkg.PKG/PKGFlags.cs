using System;

namespace LibProsperoPkg.PKG;

[Flags]
public enum PKGFlags : uint
{
	BASE_PACKAGE_PROFILE = 1u,
	VER_1 = 0x1000000u,
	VER_2 = 0x2000000u,
	INTERNAL = 0x40000000u,
	FINALIZED = 0x80000000u
}
