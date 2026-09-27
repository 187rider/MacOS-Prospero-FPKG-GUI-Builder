namespace LibProsperoPkg.Content;

/// <summary>One named blob inside a <see cref="T:LibProsperoPkg.Content.ProsperoUcp" /> archive.</summary>
/// <param name="Name">The entry name (no path separators; at most 32 bytes).</param>
/// <param name="Data">The raw entry bytes.</param>
public sealed record UcpEntry(string Name, byte[] Data);
