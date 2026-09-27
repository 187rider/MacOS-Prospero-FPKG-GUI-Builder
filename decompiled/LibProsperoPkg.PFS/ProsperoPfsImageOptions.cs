namespace LibProsperoPkg.PFS;

/// <summary>Options controlling a PFS image encrypt/decrypt operation.</summary>
public sealed class ProsperoPfsImageOptions
{
	/// <summary>
	/// The 32-byte EKPFS. When <c>null</c> the all-zero EKPFS is used — the standard
	/// package key that pairs with the all-zero passcode.
	/// </summary>
	public byte[]? Ekpfs { get; set; }

	/// <summary>
	/// The 16-byte header seed. When <c>null</c> the seed already present in the image
	/// header is used (encrypt of an already-prepared image); if the header has no seed a
	/// fresh cryptographically-random one is generated and written into the header.
	/// </summary>
	public byte[]? Seed { get; set; }

	/// <summary>
	/// When encryption needs a new seed and <see cref="P:LibProsperoPkg.PFS.ProsperoPfsImageOptions.Seed" /> is null, derive it reproducibly
	/// from EKPFS instead of using the operating-system RNG.
	/// </summary>
	public bool DeterministicSeed { get; set; }

	/// <summary>
	/// When true, derive the encryption key from <c>HMAC(EKPFS, seed)</c> first (the
	/// <c>new_crypt</c> path / the <c>newCrypt</c> scheme). Defaults to the classic path.
	/// </summary>
	public bool NewCrypt { get; set; }
}
