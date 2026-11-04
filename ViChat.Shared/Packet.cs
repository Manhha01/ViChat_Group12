namespace ViChat.Shared;

public class Packet
{
    // Loại gói, ví dụ "SEND_MSG", "NEW_MSG", sau này thêm "LOGIN"...
    public string Type { get; set; } = "";

    // Dữ liệu đi kèm dạng khóa - giá trị, ví dụ ["content"] = "Xin chào"
    public Dictionary<string, string> Data { get; set; } = new();
}