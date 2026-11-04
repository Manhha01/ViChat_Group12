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

    // Giới hạn kích thước 1 gói để tránh client gửi số độ dài bậy làm sập server.
    // Giá trị dùng chung nằm trong ProtocolLimits (PacketTypes.cs), khớp docs/protocol.md.
    private const int MaxPacketSize = ProtocolLimits.MaxPacketBytes; // 1 MB

    // GHI một gói: đổi Packet thành JSON, ghi 4 byte độ dài, rồi ghi phần JSON
    public static async Task WriteAsync(Stream stream, Packet packet)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions);

        // Chặn từ phía gửi: gói quá lớn thì bên nhận cũng sẽ từ chối
        if (body.Length > MaxPacketSize)
            throw new InvalidOperationException(
                $"Gói {packet.Type} quá lớn ({body.Length} byte, tối đa {MaxPacketSize}).");

        // Ghép header (4 byte độ dài, big-endian) và body vào một mảng để ghi MỘT lần
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);

        await stream.WriteAsync(frame);
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