namespace LibProsperoPkg.PFS;

/// <summary>The PFS superblock fields relevant to image encryption.</summary>
public sealed class ProsperoPfsImageInfo
{
	/// <summary>Superblock version (2 = PS5).</summary>
	public required long Version { get; init; }

	/// <summary>The raw PFS mode flag word from the superblock.</summary>
	public required ushort Mode { get; init; }

	/// <summary>Filesystem block size in bytes (typically 0x10000).</summary>
	public required uint BlockSize { get; init; }

	/// <summary>The 16-byte header crypto seed (all-zero when the image carries none).</summary>
	public required byte[] Seed { get; init; }

	/// <summary>True when the superblock declares the image AES-XTS encrypted.</summary>
	public bool Encrypted => (Mode & 4) != 0;

	/// <summary>True when the superblock declares the image HMAC signed.</summary>
	public bool Signed => (Mode & 1) != 0;
}
