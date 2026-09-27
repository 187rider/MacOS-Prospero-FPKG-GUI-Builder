using System;
using System.Collections.Generic;

namespace LibProsperoPkg.PFS.Compression;

/// <summary>Controls the runtime-I/O tradeoff of a PFSC v2/Kraken stream.</summary>
public sealed class PprPfsKrakenWriteOptions
{
	/// <summary>Kraken level written into the PFSC header.</summary>
	public int Level { get; set; } = 8;

	/// <summary>Decode and compare every emitted Kraken group while building.</summary>
	public bool VerifyBlocks { get; set; }

	/// <summary>
	/// Minimum percentage of logical bytes Kraken must save for a group to remain compressed.
	/// Groups below this threshold are stored raw to avoid decompression for negligible I/O gain.
	/// </summary>
	public int MinimumSavingsPercent { get; set; }

	/// <summary>Logical ranges whose intersecting 256 KiB groups are forced to raw storage.</summary>
	public IReadOnlyCollection<PprPfsRawRange> RawRanges { get; set; } = Array.Empty<PprPfsRawRange>();

	/// <summary>
	/// Maximum number of independent 256-KiB Kraken groups compressed concurrently.
	/// The default is 1 for low-level API compatibility.
	/// </summary>
	public int MaxDegreeOfParallelism { get; set; } = 1;
}
