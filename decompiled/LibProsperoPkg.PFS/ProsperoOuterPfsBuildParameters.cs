namespace LibProsperoPkg.PFS;

/// <summary>
/// Build parameters for the outer-PFS structure generator. The timestamp/seed defaults define the
/// default image parameters; a fresh build supplies the current time and a fresh seed.
/// </summary>
public sealed class ProsperoOuterPfsBuildParameters
{
	/// <summary>POSIX seconds stamped into every inode time field.</summary>
	public long TimestampSeconds { get; init; } = 1781638585L;

	/// <summary>Nanosecond fraction stamped into every inode time field.</summary>
	public uint TimestampNanoseconds { get; init; } = 350000000u;

	/// <summary>
	/// The 16-byte native crypt seed written at superblock+0x370 and used for key derivation.
	/// PlaintextNoAuth replaces it with its fixed profile marker.
	/// </summary>
	public byte[]? Seed { get; init; }

	/// <summary>
	/// Runtime representation of the outer image. Both profiles use the kernel-supported
	/// superblock mode <c>0x0D</c>; plaintext/no-auth is identified by a dedicated seed marker and
	/// skips the final transform. This remains independent from the staging transform switch.
	/// </summary>
	public ProsperoPublisherImageMode ImageMode { get; init; }
}
