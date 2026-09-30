namespace LibProsperoPkg.PKG;

public struct Header
{
	public string CNTMagic;

	public PKGFlags flags;

	public uint ps5_profile_marker;

	public uint header_profile_code;

	public uint entry_count;

	public ushort sc_entry_count;

	public ushort entry_count_2;

	public uint entry_table_offset;

	public uint main_ent_data_size;

	public ulong body_offset;

	public ulong body_size;

	public ulong mandatory_size;

	public string content_id;

	public uint drm_type;

	public uint content_type;

	public ContentFlags content_flags;

	public uint promote_size;

	public uint version_date;

	public uint version_hash;

	public uint delta_patch_metadata_0;

	public uint delta_patch_metadata_1;

	public uint delta_patch_metadata_2;

	public uint delta_patch_metadata_3;

	public IROTag iro_tag;

	public uint ekc_version;

	public byte[] sc_entries1_hash;

	public byte[] sc_entries2_hash;

	public byte[] digest_table_hash;

	public byte[] body_digest;

	public uint pfs_descriptor_presence;

	public uint pfs_image_count;

	public ulong pfs_flags;

	public ulong pfs_image_offset;

	public ulong pfs_image_size;

	public ulong mount_image_offset;

	public ulong mount_image_size;

	public ulong package_size;

	public uint pfs_signed_size;

	public uint pfs_cache_size;

	public byte[] pfs_image_digest;

	public byte[] pfs_signed_digest;

	public ulong pfs_split_size_nth_0;

	public ulong pfs_split_size_nth_1;

	public byte[] image_seed;

	public ulong cnt_region_offset;

	public ulong cnt_region_size;

	public uint desc_image_key_offset;

	public uint desc_image_key_size;

	public uint desc_mandatory_offset;

	public uint desc_mandatory_size;

	public byte[] desc_digest;
}
