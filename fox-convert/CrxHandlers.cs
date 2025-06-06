using System.IO.Compression;
using System.Text;

namespace fox_convert
{
    public static class CrxHandlers
    {
        public static async Task ExtractFromCrxToFolder(string crxPath, string extractTo)
        {
            using var crxStream = new FileStream(crxPath, FileMode.Open, FileAccess.Read);
            byte[] header8 = new byte[8];
            await crxStream.ReadAsync(header8);

            if (Encoding.ASCII.GetString(header8, 0, 4) != "Cr24")
                throw new InvalidDataException("Invalid CRX file magic header");

            int version = BitConverter.ToInt32(header8, 4);
            int headerLen = version switch
            {
                2 => await GetHeaderLengthV2(crxStream),
                3 => await GetHeaderLengthV3(crxStream),
                _ => throw new InvalidDataException($"Unsupported CRX version: {version}")
            };

            crxStream.Seek(headerLen, SeekOrigin.Begin);

            using var ms = new MemoryStream();
            await crxStream.CopyToAsync(ms);
            ms.Seek(0, SeekOrigin.Begin);

            if (Directory.Exists(extractTo)) Directory.Delete(extractTo, true);
            Directory.CreateDirectory(extractTo);

            using var zipArchive = new ZipArchive(ms);
            zipArchive.ExtractToDirectory(extractTo);
        }

        static async Task<int> GetHeaderLengthV2(FileStream crxStream)
        {
            byte[] lengths = new byte[8];
            await crxStream.ReadAsync(lengths);
            int pubKeyLen = BitConverter.ToInt32(lengths, 0);
            int sigLen = BitConverter.ToInt32(lengths, 4);
            return 16 + pubKeyLen + sigLen;
        }

        static async Task<int> GetHeaderLengthV3(FileStream crxStream)
        {
            byte[] headerSizeBytes = new byte[4];
            await crxStream.ReadAsync(headerSizeBytes);
            int headerSize = BitConverter.ToInt32(headerSizeBytes, 0);
            return 12 + headerSize;
        }

        public static string GetCrxUrl(string extId) =>
            $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=133.0&acceptformat=crx2,crx3&x=id={extId}%26uc";

        public static async Task DownloadCrxAsync(string url, string path)
        {
            using HttpClient client = new();
            var data = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, data);
        }
    }
}