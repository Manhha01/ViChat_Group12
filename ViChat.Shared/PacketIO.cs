using System.Buffers.Binary;
using System.Text.Json;

namespace ViChat.Shared;

public static class PacketIO
{
    // Tên thuộc tính trong JSON viết kiểu camelCase: "type", "data"
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Giới hạn kích thước 1 gói để tránh client gửi số độ dài bậy làm sập server
    private const int MaxPacketSize = 10 * 1024 * 1024; // 10 MB

    // GHI một gói: đổi Packet thành JSON, ghi 4 byte độ dài, rồi ghi phần JSON
    public static async Task WriteAsync(Stream stream, Packet packet)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);

        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    // ĐỌC một gói. Trả về null nếu bên kia đã đóng kết nối.
    public static async Task<Packet?> ReadAsync(Stream stream)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header)) return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxPacketSize)
            throw new InvalidDataException($"Độ dài gói không hợp lệ: {length}");

        byte[] body = new byte[length];
        if (!await ReadExactAsync(stream, body)) return null;

        return JsonSerializer.Deserialize<Packet>(body, JsonOptions);
    }

    // Đọc cho ĐỦ số byte cần thiết.
    // Lý do: TCP là dòng byte, một lần ReadAsync có thể chỉ trả về một phần dữ liệu.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset));
            if (n == 0) return false;   // 0 byte = bên kia đã đóng kết nối
            offset += n;
        }
        return true;
    }
}