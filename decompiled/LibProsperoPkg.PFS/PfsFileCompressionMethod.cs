namespace LibProsperoPkg.PFS;

/// <summary>Per-file compression used while laying out a PFS image.</summary>
public enum PfsFileCompressionMethod
{
	/// <summary>Store every file verbatim.</summary>
	None,
	/// <summary>Classic PFSC/zlib blocks, readable by the classic pfs driver.</summary>
	Zlib,
	/// <summary>PFSC v2/Kraken blocks, readable by ppr_pfs.</summary>
	Kraken
}
