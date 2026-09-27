namespace LibProsperoPkg.PFS;

/// <summary>
/// Data structure used in signed 64 bit PFS images, and in any signed PFS header.
/// </summary>
public struct block_sig64
{
	public byte[] sig;

	public long block;
}
