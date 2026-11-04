using System.Security.Cryptography;

namespace ViChat.Shared;

/// <summary>
/// Gửi một file bằng chuỗi gói FILE_START → FILE_CHUNK... → FILE_END.
/// Không nạp cả file vào RAM: đọc từng chunk 64 KB.
/// Dùng chung cho Client (WPF) và TestClient.
/// </summary>
public static class FileSender
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    /// <summary>
    /// Gửi file. Trả về fileId để sau này khớp với FILE_RESULT.
    /// </summary>
    /// <param name="path">Đường dẫn file trên máy.</param>
    /// <param name="targetType">"room" hoặc "user" (xem FileKinds).</param>
    /// <param name="target">roomId hoặc username người nhận.</param>
    /// <param name="send">
    /// Hàm gửi một gói. Ví dụ ở client: p => PacketIO.WriteAsync(stream, p)
    /// (nếu nhiều luồng cùng ghi thì phải có khóa bên trong hàm này).
    /// </param>
    /// <param name="progress">Nhận phần trăm 0..100 (có thể null).</param>
    public static async Task<string> SendFileAsync(
        string path,
        string targetType,
        string target,
        Func<Packet, Task> send,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Không tìm thấy file.", path);
        if (info.Length > ProtocolLimits.MaxFileBytes)
            throw new InvalidOperationException(
                $"File lớn hơn {ProtocolLimits.MaxFileBytes / (1024 * 1024)} MB.");

        string fileId = Guid.NewGuid().ToString();
        string kind = ImageExtensions.Contains(info.Extension) ? FileKinds.Image : FileKinds.File;

        // 1) Tính SHA-256 trước (đọc theo luồng, không tốn RAM).
        string hash;
        await using (var hs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         81920, useAsync: true))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(hs, ct));
        }

        // 2) FILE_START: thông tin file.
        await send(new Packet
        {
            Type = PacketTypes.FileStart,
            Data = new Dictionary<string, string>
            {
                ["fileId"] = fileId,
                ["name"] = info.Name,
                ["size"] = info.Length.ToString(),
                ["sha256"] = hash,
                ["kind"] = kind,
                ["targetType"] = targetType,
                ["target"] = target
            }
        });

        // 3) FILE_CHUNK: từng phần 64 KB.
        var buffer = new byte[ProtocolLimits.FileChunkBytes];
        long sent = 0;
        int index = 0;
        int lastPercent = -1;

        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         81920, useAsync: true))
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // ReadAsync có thể trả ít hơn buffer.Length; luôn dùng đúng số byte n đọc được.
                int n = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (n == 0) break;

                await send(new Packet
                {
                    Type = PacketTypes.FileChunk,
                    Data = new Dictionary<string, string>
                    {
                        ["fileId"] = fileId,
                        ["index"] = index.ToString(),
                        ["data"] = Convert.ToBase64String(buffer, 0, n)
                    }
                });

                sent += n;
                index++;

                int percent = (int)(sent * 100 / info.Length);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Report(percent);
                }
            }
        }

        // 4) FILE_END: báo kết thúc, kèm số chunk để bên nhận đối chiếu.
        await send(new Packet
        {
            Type = PacketTypes.FileEnd,
            Data = new Dictionary<string, string>
            {
                ["fileId"] = fileId,
                ["totalChunks"] = index.ToString()
            }
        });

        if (info.Length == 0) progress?.Report(100);
        return fileId;
    }
}