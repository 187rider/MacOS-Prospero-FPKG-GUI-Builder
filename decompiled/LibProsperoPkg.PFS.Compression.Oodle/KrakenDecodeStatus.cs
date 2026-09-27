namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>The outcome of a Kraken decode attempt.</summary>
internal enum KrakenDecodeStatus
{
	/// <summary>The block decoded successfully into the destination buffer.</summary>
	Success,
	/// <summary>The block's framing, sizes or arrays were inconsistent (corrupt or unexpected input).</summary>
	Malformed,
	/// <summary>An entropy array used a coding the managed decoder does not implement (TANS/RLE/recursive).</summary>
	UnsupportedEntropy,
	/// <summary>The chunk used an excess/control form the managed decoder does not implement.</summary>
	UnsupportedExcessMode
}
