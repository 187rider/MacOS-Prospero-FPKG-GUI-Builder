namespace LibProsperoPkg.Content;

/// <summary>A decoded entry from a <see cref="T:LibProsperoPkg.Content.ProsperoFself" /> segment table.</summary>
/// <param name="Flags">Raw 64-bit flags word.</param>
/// <param name="FileOffset">Offset of the segment data within the SELF file.</param>
/// <param name="FileSize">Stored size of the segment.</param>
/// <param name="MemSize">In-memory size of the segment.</param>
public sealed record SelfSegment(ulong Flags, ulong FileOffset, ulong FileSize, ulong MemSize)
{
	/// <summary>Segment id (bits 20..35), a program-header index for data segments.</summary>
	public int Id => (int)((Flags >> 20) & 0xFFFF);

	/// <summary>Whether the segment is ordered.</summary>
	public bool Ordered => (Flags & 1) != 0;

	/// <summary>Whether the segment data is encrypted.</summary>
	public bool Encrypted => (Flags & 2) != 0;

	/// <summary>Whether the segment is covered by a signature/digest.</summary>
	public bool Signed => (Flags & 4) != 0;

	/// <summary>Whether the segment data is deflate-compressed.</summary>
	public bool Compressed => (Flags & 8) != 0;

	/// <summary>Whether the segment is stored in fixed-size blocks.</summary>
	public bool Blocked => (Flags & 0x800) != 0;
}
