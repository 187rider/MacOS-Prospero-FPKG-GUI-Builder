using System.Collections.Generic;

namespace LibProsperoPkg.Content;

/// <summary>A parsed SELF image.</summary>
/// <param name="ProgramType">SCE header program/key type field.</param>
/// <param name="HeaderSize">Size of the header region.</param>
/// <param name="MetaSize">Size of the metadata footer.</param>
/// <param name="FileSize">Total file size recorded in the header.</param>
/// <param name="Segments">Decoded segment table.</param>
/// <param name="Elf">The embedded ELF header and program headers region.</param>
/// <param name="ExtInfo">Extended info, when present.</param>
public sealed record SelfImage(uint ProgramType, int HeaderSize, int MetaSize, ulong FileSize, IReadOnlyList<SelfSegment> Segments, byte[] Elf, SelfExtInfo? ExtInfo);
