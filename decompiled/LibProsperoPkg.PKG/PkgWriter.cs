using System.IO;
using System.Text;
using LibProsperoPkg.Util;

namespace LibProsperoPkg.PKG;

public class PkgWriter : WriterBase
{
	public PkgWriter(Stream s)
		: base(flipEndian: true, s)
	{
	}

	public void WriteBody(Pkg pkg, string contentId, string passcode)
	{
		bool publisherProfile = pkg.EntryKeys.Keys.Length != 0 && pkg.EntryKeys.Keys[0].key.Length == 384;
		foreach (Entry entry in pkg.Entries)
		{
			s.Position = entry.meta.DataOffset;
			if (entry.meta.Encrypted)
			{
				entry.WriteEncrypted(s, contentId, passcode, publisherProfile);
			}
			else
			{
				entry.Write(s);
			}
		}
	}

	public void WriteHeader(in Header hdr)
	{
		s.Position = 0L;
		Write(Encoding.ASCII.GetBytes(hdr.CNTMagic));
		s.Position = 4L;
		Write((uint)hdr.flags);
		s.Position = 8L;
		Write(hdr.ps5_profile_marker);
		s.Position = 12L;
		Write(hdr.header_profile_code);
		s.Position = 16L;
		Write(hdr.entry_count);
		s.Position = 20L;
		Write(hdr.sc_entry_count);
		s.Position = 22L;
		Write(hdr.entry_count_2);
		s.Position = 24L;
		Write(hdr.entry_table_offset);
		s.Position = 28L;
		Write(hdr.main_ent_data_size);
		s.Position = 32L;
		Write(hdr.body_offset);
		s.Position = 40L;
		Write(hdr.body_size);
		s.Position = 48L;
		Write(hdr.mandatory_size);
		s.Position = 64L;
		Write(Encoding.ASCII.GetBytes(hdr.content_id));
		s.Position = 112L;
		Write(hdr.drm_type);
		s.Position = 116L;
		Write(hdr.content_type);
		s.Position = 120L;
		Write((uint)hdr.content_flags);
		s.Position = 124L;
		Write(hdr.promote_size);
		s.Position = 128L;
		Write(hdr.version_date);
		s.Position = 132L;
		Write(hdr.version_hash);
		s.Position = 136L;
		Write(hdr.delta_patch_metadata_0);
		s.Position = 140L;
		Write(hdr.delta_patch_metadata_1);
		s.Position = 144L;
		Write(hdr.delta_patch_metadata_2);
		s.Position = 148L;
		Write(hdr.delta_patch_metadata_3);
		s.Position = 152L;
		Write((uint)hdr.iro_tag);
		s.Position = 156L;
		Write(hdr.ekc_version);
		s.Position = 256L;
		Write(hdr.sc_entries1_hash);
		s.Position = 288L;
		Write(hdr.sc_entries2_hash);
		s.Position = 320L;
		Write(hdr.digest_table_hash);
		s.Position = 352L;
		Write(hdr.body_digest);
		s.Position = 512L;
		Write(Encoding.ASCII.GetBytes(hdr.content_id));
		s.Position = 1024L;
		Write(hdr.pfs_descriptor_presence);
		s.Position = 1028L;
		Write(hdr.pfs_image_count);
		s.Position = 1032L;
		Write(hdr.pfs_flags);
		s.Position = 1040L;
		Write(hdr.pfs_image_offset);
		s.Position = 1048L;
		Write(hdr.pfs_image_size);
		s.Position = 1056L;
		Write(hdr.mount_image_offset);
		s.Position = 1064L;
		Write(hdr.mount_image_size);
		s.Position = 1072L;
		Write(hdr.package_size);
		s.Position = 1080L;
		Write(hdr.pfs_signed_size);
		s.Position = 1084L;
		Write(hdr.pfs_cache_size);
		s.Position = 1088L;
		Write(hdr.pfs_image_digest);
		s.Position = 1120L;
		Write(hdr.pfs_signed_digest);
		s.Position = 1152L;
		Write(hdr.pfs_split_size_nth_0);
		s.Position = 1160L;
		Write(hdr.pfs_split_size_nth_1);
		s.Position = 1184L;
		Write(hdr.image_seed);
		s.Position = 1200L;
		Write(hdr.cnt_region_offset);
		s.Position = 1208L;
		Write(hdr.cnt_region_size);
		s.Position = 1296L;
		Write(hdr.desc_image_key_offset);
		Write(hdr.desc_image_key_size);
		Write(hdr.desc_mandatory_offset);
		Write(hdr.desc_mandatory_size);
		s.Position = 1312L;
		Write(hdr.desc_digest);
	}
}
