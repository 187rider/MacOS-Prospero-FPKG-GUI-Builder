namespace LibProsperoPkg.PFS;

/// <summary>One intentionally empty AFID slot represented by a logical 256-KiB zero extent.</summary>
public readonly record struct ProsperoPs5SparseAfidHole(uint Afid, long LogicalOffset, long Size);
