namespace LibProsperoPkg.PFS;

/// <summary>
/// Data structure used in signed 32 bit PFS images
/// </summary>
public struct block_sig
{
	public byte[] sig;

	public int block;
}
