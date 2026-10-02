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

	/// <summary>
	/// Preserved or custom raw <c>.sceversion</c> trailer records to write at the end of the SELF.
	/// When provided and the ELF has no <c>.sceversion</c> section, these records are appended.
	/// </summary>
	public byte[]? SceVersionRecords { get; init; }

	/// <summary>Forces legacy Orbis FSELF container (magic 0x1D3D154F), required for PS5 FW 3.xx-4.xx jailbreak.</summary>
	public bool UseOrbisContainer { get; init; }

	/// <summary>SELF program type (default: 268435713u / 0x10000101 for app, or 0x00000101 for Orbis).</summary>
	public uint ProgramType { get; init; } = 268435713u;
}
