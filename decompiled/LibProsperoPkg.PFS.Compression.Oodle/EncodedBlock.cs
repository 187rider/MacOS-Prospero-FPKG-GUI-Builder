namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// The result of compressing one PFS block: the section-7 payload plus the metadata the boundary
/// table needs (whether the block was split into two chunks, and the first chunk's compressed size).
/// </summary>
internal readonly struct EncodedBlock
{
	/// <summary>The bytes that land in section 7 for this block (one or two concatenated chunks).</summary>
	public readonly byte[] Payload;

	/// <summary>True when the block has two independently encoded 128-KiB halves.</summary>
	public readonly bool MultiChunk;

	/// <summary>The compressed size of the first chunk (the value stored, minus one, in the size hint).</summary>
	public readonly int FirstChunkCompSize;

	/// <summary>The authoritative PFSC boundary flag byte for both halves.</summary>
	public readonly int BoundaryFlags;

	/// <summary>The corresponding NAPS mode for the even half.</summary>
	public byte NapsEvenMode => (byte)(1 | (((BoundaryFlags & 2) != 0) ? 4 : 0) | (((BoundaryFlags & 1) != 0) ? 2 : 0));

	/// <summary>The corresponding NAPS mode for the odd half, or zero when absent.</summary>
	public byte NapsOddMode
	{
		get
		{
			if (MultiChunk)
			{
				return (byte)((((BoundaryFlags & 0x20) == 0) ? 1 : 4) | (((BoundaryFlags & 0x10) != 0) ? 2 : 0));
			}
			return 0;
		}
	}

	public EncodedBlock(byte[] payload, bool multiChunk, int firstChunkCompSize, int boundaryFlags)
	{
		Payload = payload;
		MultiChunk = multiChunk;
		FirstChunkCompSize = firstChunkCompSize;
		BoundaryFlags = boundaryFlags;
	}
}
