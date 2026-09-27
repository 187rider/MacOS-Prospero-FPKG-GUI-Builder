namespace LibProsperoPkg.Content;

/// <summary>SELF extended information (0x40 bytes) that follows the ELF program headers.</summary>
/// <param name="AuthorityId">Program authority id (PAID). A fake-self uses the 0x31.. prefix.</param>
/// <param name="ProgramType">Program type (PTYPE).</param>
/// <param name="AppVersion">Application version.</param>
/// <param name="FirmwareVersion">Firmware version.</param>
/// <param name="Digest">SHA-256 of the original ELF file.</param>
public sealed record SelfExtInfo(ulong AuthorityId, ulong ProgramType, ulong AppVersion, ulong FirmwareVersion, byte[] Digest);
