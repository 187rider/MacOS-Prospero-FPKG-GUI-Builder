namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Constants for PFS Kraken compression.
/// </summary>
public static class PfsCompressionConstants
{
	/// <summary>Number of sliding-window bits used for Kraken (the only valid value).</summary>
	public const uint KrakenWindowBits = 18u;

	/// <summary>
	/// The default compression level used for PFSv3 containers (<c>"compression level": 7</c>).
	/// </summary>
	public const uint DefaultKrakenLevel = 7u;

	/// <summary>Lowest (fastest) accepted Kraken level, encoded as an unsigned value.</summary>
	public const uint KrakenLevelFastest = 4294967292u;

	/// <summary>Highest accepted Kraken level.</summary>
	public const uint KrakenLevelMax = 9u;

	/// <summary>Returns <c>true</c> when <paramref name="level" /> is an accepted Kraken level.</summary>
	public static bool IsValidKrakenLevel(uint level)
	{
		bool flag = level <= 9;
		if (!flag)
		{
			bool flag2 = (uint)((int)level - -4) <= 3u;
			flag = flag2;
		}
		return flag;
	}
}
