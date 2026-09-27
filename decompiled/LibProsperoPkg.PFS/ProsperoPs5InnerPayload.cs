using System;

namespace LibProsperoPkg.PFS;

/// <summary>One inner-image payload (a file's data or the metadata block) with its resolved on-disk placement.</summary>
public sealed class ProsperoPs5InnerPayload
{
	/// <summary>The uncompressed payload bytes.</summary>
	public byte[] Data = Array.Empty<byte>();

	/// <summary>When true the payload is stored raw (never compressed) and is placed block-aligned.</summary>
	public bool StoreRaw;

	/// <summary>When true the payload is placed at the next 64 KiB block boundary; otherwise packed contiguously.</summary>
	public bool BlockAligned;

	/// <summary>When true the on-disk cursor is advanced to the next 64 KiB block boundary <em>after</em> this
	/// payload, so it occupies whole blocks and the following payload starts block-aligned. Used for the
	/// sce_sys subtree, which forms a fully block-aligned region.</summary>
	public bool BlockAlignedAfter;
}
