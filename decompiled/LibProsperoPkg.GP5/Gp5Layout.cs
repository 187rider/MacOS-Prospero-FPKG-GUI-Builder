namespace LibProsperoPkg.GP5;

/// <summary>
/// The structural style a GP5 project uses to describe its contents. The reference tool understands
/// two equivalent layouts:
/// <list type="bullet">
/// <item><see cref="F:LibProsperoPkg.GP5.Gp5Layout.Normal" /> — a single <c>&lt;rootdir&gt;</c> with a <c>src_path</c> (and optional
/// exclude masks) that the tool walks recursively, preceded by <c>&lt;global_exclude&gt;</c>.</item>
/// <item><see cref="F:LibProsperoPkg.GP5.Gp5Layout.Flat" /> — an explicit top-level <c>&lt;files&gt;</c> (and optional
/// <c>&lt;folders&gt;</c>) listing that maps each source path to a package destination path, with no
/// <c>&lt;rootdir&gt;</c> / <c>&lt;global_exclude&gt;</c>.</item>
/// </list>
/// </summary>
public enum Gp5Layout
{
	/// <summary>A single recursively-walked <c>&lt;rootdir src_path&gt;</c> (with <c>&lt;global_exclude&gt;</c>).</summary>
	Normal,
	/// <summary>An explicit top-level <c>&lt;files&gt;</c> / <c>&lt;folders&gt;</c> listing.</summary>
	Flat
}
