using System.Collections.Generic;

namespace LibProsperoPkg.PKG;

public static class EntryNames
{
	public static Dictionary<string, EntryId> NameToId;

	public static Dictionary<EntryId, string> IdToName;

	static EntryNames()
	{
		NameToId = new Dictionary<string, EntryId>();
		IdToName = new Dictionary<EntryId, string>
		{
			{
				EntryId.DIGESTS,
				".digests"
			},
			{
				EntryId.ENTRY_KEYS,
				".entry_keys"
			},
			{
				EntryId.IMAGE_KEY,
				".image_key"
			},
			{
				EntryId.GENERAL_DIGESTS,
				".general_digests"
			},
			{
				EntryId.METAS,
				".metas"
			},
			{
				EntryId.ENTRY_NAMES,
				".entry_names"
			},
			{
				EntryId.LICENSE_DAT,
				"license.dat"
			},
			{
				EntryId.LICENSE_INFO,
				"license.info"
			},
			{
				EntryId.NPTITLE_DAT,
				"nptitle.dat"
			},
			{
				EntryId.NPBIND_DAT,
				"npbind.dat"
			},
			{
				EntryId.SELFINFO_DAT,
				"selfinfo.dat"
			},
			{
				EntryId.IMAGEINFO_DAT,
				"imageinfo.dat"
			},
			{
				EntryId.TARGET_DELTAINFO_DAT,
				"target-deltainfo.dat"
			},
			{
				EntryId.ORIGIN_DELTAINFO_DAT,
				"origin-deltainfo.dat"
			},
			{
				EntryId.PSRESERVED_DAT,
				"psreserved.dat"
			},
			{
				EntryId.PARAM_SFO,
				"param.sfo"
			},
			{
				EntryId.PLAYGO_CHUNK_DAT,
				"playgo-chunk.dat"
			},
			{
				EntryId.PLAYGO_CHUNK_SHA,
				"playgo-chunk.sha"
			},
			{
				EntryId.PLAYGO_MANIFEST_XML,
				"playgo-manifest.xml"
			},
			{
				EntryId.PRONUNCIATION_SIG,
				"pronunciation.sig"
			},
			{
				EntryId.PRONUNCIATION_XML,
				"pronunciation.xml"
			},
			{
				EntryId.PIC1_PNG,
				"pic1.png"
			},
			{
				EntryId.PUBTOOLINFO_DAT,
				"pubtoolinfo.dat"
			},
			{
				EntryId.APP__PLAYGO_CHUNK_DAT,
				"app/playgo-chunk.dat"
			},
			{
				EntryId.APP__PLAYGO_CHUNK_SHA,
				"app/playgo-chunk.sha"
			},
			{
				EntryId.APP__PLAYGO_MANIFEST_XML,
				"app/playgo-manifest.xml"
			},
			{
				EntryId.SHAREPARAM_JSON,
				"shareparam.json"
			},
			{
				EntryId.SHAREOVERLAYIMAGE_PNG,
				"shareoverlayimage.png"
			},
			{
				EntryId.SAVE_DATA_PNG,
				"save_data.png"
			},
			{
				EntryId.SHAREPRIVACYGUARDIMAGE_PNG,
				"shareprivacyguardimage.png"
			},
			{
				EntryId.ICON0_PNG,
				"icon0.png"
			},
			{
				EntryId.PIC0_PNG,
				"pic0.png"
			},
			{
				EntryId.SND0_AT9,
				"snd0.at9"
			},
			{
				EntryId.CHANGEINFO__CHANGEINFO_XML,
				"changeinfo/changeinfo.xml"
			},
			{
				EntryId.ICON0_DDS,
				"icon0.dds"
			},
			{
				EntryId.PIC0_DDS,
				"pic0.dds"
			},
			{
				EntryId.PIC1_DDS,
				"pic1.dds"
			}
		};
		IdToName.Add((EntryId)5248u, "trophy2/trophy00.ucp");
		IdToName.Add((EntryId)5280u, "uds/uds00.ucp");
		IdToName.Add((EntryId)8224u, "uds/npbind.dat");
		IdToName.Add((EntryId)8225u, "trophy2/npbind.dat");
		for (int i = 0; i < 31; i++)
		{
			IdToName.Add((EntryId)(4609 + i), $"icon0_{i:d2}.png");
			IdToName.Add((EntryId)(4737 + i), $"icon0_{i:d2}.dds");
			IdToName.Add((EntryId)(4673 + i), $"pic1_{i:d2}.png");
			IdToName.Add((EntryId)(4801 + i), $"pic1_{i:d2}.dds");
			IdToName.Add((EntryId)(4705 + i), $"changeinfo/changeinfo_{i:d2}.xml");
			if (i < 10)
			{
				IdToName.Add((EntryId)(5632 + i), $"keymap_rp/0{i + 1:d2}.png");
			}
			for (int j = 0; j < 10; j++)
			{
				IdToName.Add((EntryId)(5648 + 16 * i + j), $"keymap_rp/{i:d2}/0{j + 1:d2}.png");
			}
		}
		for (int k = 0; k < 100; k++)
		{
			IdToName.Add((EntryId)(5120 + k), $"trophy/trophy{k:d2}.trp");
		}
		foreach (KeyValuePair<EntryId, string> item in IdToName)
		{
			NameToId.Add(item.Value, item.Key);
		}
	}
}
