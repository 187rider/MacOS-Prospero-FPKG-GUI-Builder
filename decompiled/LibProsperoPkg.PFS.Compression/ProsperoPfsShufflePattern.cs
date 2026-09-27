namespace LibProsperoPkg.PFS.Compression;

/// <summary>
/// Pre-compression shuffle pattern recorded in the container metadata.
/// A shuffle groups bytes at given positions within fixed 8-byte or 16-byte vectors together
/// (a structure-of-arrays de-interleave) so that similar bytes become adjacent and compress better.
/// </summary>
public enum ProsperoPfsShufflePattern
{
	/// <summary>An invalid shuffle pattern.</summary>
	Invalid = -1,
	/// <summary>No shuffle is applied.</summary>
	None,
	/// <summary>Tap 2x 4 bytes with an 8 byte stride.</summary>
	Shuffle44,
	/// <summary>Tap 2x 2 bytes then 4 bytes with an 8 byte stride.</summary>
	Shuffle224,
	/// <summary>Tap 2x 1 bytes then 6 bytes with an 8 byte stride.</summary>
	Shuffle116,
	/// <summary>Tap 8x 1 bytes with an 8 byte stride.</summary>
	Shuffle11111111,
	/// <summary>Tap 8 bytes, 2x 2 bytes then 4 bytes with a 16 byte stride.</summary>
	Shuffle8224,
	/// <summary>Tap 2x 1 bytes, 6 bytes, 2x 2 bytes then 4 bytes with a 16 byte stride.</summary>
	Shuffle116224,
	/// <summary>Tap 2x 1 bytes, 6 bytes, 2x 1 bytes then 6 bytes with a 16 byte stride.</summary>
	Shuffle116116,
	/// <summary>Tap 4x 4 bytes with a 16 byte stride.</summary>
	Shuffle4444,
	/// <summary>Tap 2x 8 bytes with a 16 byte stride.</summary>
	Shuffle88,
	/// <summary>Tap 8 bytes then 2x 4 bytes with a 16 byte stride.</summary>
	Shuffle844,
	/// <summary>Tap 2 bytes then 6 bytes with an 8 byte stride.</summary>
	Shuffle26,
	/// <summary>Tap 2 bytes, 6 bytes, 2 bytes then 6 bytes with a 16 byte stride.</summary>
	Shuffle2626
}
