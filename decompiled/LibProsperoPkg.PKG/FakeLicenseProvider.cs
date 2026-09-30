using System;
using System.Text;

namespace LibProsperoPkg.PKG;

public sealed class FakeLicenseProvider : IProsperoLicenseProvider
{
	private static readonly byte[] RifSigTemplate = Convert.FromHexString("888B59C768C1B4440B2F8F2A3E1E852711592948E94D07C47504DA0449A98B54381A394E74A9426E83A5AFB6E36FF8AE4F9AED7328029B7B19FE60CA64CF9B68134E7CAADA695BEB7E49733483D9CB83CACCE9877B22DEC66172E60B7EBA1A179BCE8DC376AE03FB3D9C7BC987AD4075C35E5FF4CC0B29C1B9EDE5B3CD881E9D722A900ECBDCC2EF9D85E3DF26248DE8587CEA77B80F0962B36D22FC4F23319B2F597C0F6A7EB7D450505C487958C6DA723E964E34B13793D89E869F55CC0117B8A6968A45CC5C87B06BE06AD4A35066A13F6A40135C88B6FC901CB8A1D4234D6C65FFE8E9BDD615884B5C2ABE8C38F04C64EA4FA3E5B5F3436721A7898FBF84A3F1C015360C44FCC2EE43630195F8BE73750DD0131D38AF398C0B76D9BDDFA38ECB58CDAF8B78F9A92313C9110FCFB096324A6A64640B6F6895CA69F561B9C590D3ABA8C6DF4F9FF64499C4B4124DF15395004CC2D59D8006148C1F2D36F2B638313C27DF68FD9C48E7DDEDFB428EB77BC29F3FD27C312D35B745E1D326082AFEB25C049CD6CA26C5A5D7E0010BED10D7B52C1E408A1E766CE525818AF588AF");

	public ProsperoLicenseArtifacts GetLicense(ProsperoLicenseRequest request)
	{
		byte[] array = new byte[1024];
		array[0] = 82;
		array[1] = 73;
		array[2] = 70;
		array[3] = 0;
		array[4] = 0;
		array[5] = 1;
		array[6] = byte.MaxValue;
		array[7] = byte.MaxValue;
		array[20] = 81;
		array[21] = 80;
		array[22] = 97;
		array[23] = 67;
		for (int i = 24; i < 32; i++)
		{
			array[i] = byte.MaxValue;
		}
		array[24] = 127;
		Encoding.ASCII.GetBytes(request.ContentId, array.AsSpan(32, 36));
		byte[] array2 = new byte[24]
		{
			2, 0, 0, 16, 0, 32, 0, 3, 0, 0,
			0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
			0, 0, 0, 1
		};
		array2.CopyTo(array.AsSpan(76, array2.Length));
		RifSigTemplate.CopyTo(array.AsSpan(608, RifSigTemplate.Length));
		byte[] array3 = new byte[512];
		Encoding.ASCII.GetBytes(request.ContentId, array3.AsSpan(0, 36));
		array3[68] = 0;
		array3[69] = 0;
		array3[70] = 0;
		array3[71] = 32;
		array3[76] = 0;
		array3[77] = 0;
		array3[78] = 0;
		array3[79] = 1;
		if (request.EntitlementKey != null && request.EntitlementKey.Length == 16)
		{
			request.EntitlementKey.CopyTo(array3.AsSpan(48, 16));
		}
		return new ProsperoLicenseArtifacts
		{
			LicenseDat = array,
			LicenseInfo = array3
		};
	}
}
