namespace LibProsperoPkg.Content;

/// <summary>Options for <see cref="M:LibProsperoPkg.Content.ProsperoFself.MakeFself(System.Byte[],LibProsperoPkg.Content.FselfOptions)" />.</summary>
public sealed class FselfOptions
{
	/// <summary>Application version written to the extended info.</summary>
	public ulong AppVersion { get; init; }

	/// <summary>Firmware version written to the extended info.</summary>
	public ulong FirmwareVersion { get; init; }

	/// <summary>
	/// Overrides the authority id. When null, the id is derived from the ELF type and the ex-info byte.
	/// </summary>
	public ulong? AuthorityId { get; init; }

	/// <summary>
	/// Optional library name used to synthesize a single <c>.sceversion</c> record when the input
	/// ELF is stripped. The package builder supplies the PRX file name without its extension.
	/// </summary>
	public string? SceVersionName { get; init; }

	/// <summary>
	/// Eight-byte SDK record copied from the application's real <c>.sceversion</c> section. It is
	/// written twice in a synthesized publisher library record.
	/// </summary>
	public byte[]? SceVersionRecord { get; init; }
}
