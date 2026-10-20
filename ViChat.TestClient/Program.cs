using System.Net.Sockets;
using System.Text;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

Console.Write("IP server (Enter = 127.0.0.1): ");
string? ip = Console.ReadLine();
if (string.IsNullOrWhiteSpace(ip)) ip = "127.0.0.1";

Console.Write("Tên của bạn: ");
string name = Console.ReadLine() ?? "Ẩn danh";

using var tcp = new TcpClient();
try
{
    await tcp.ConnectAsync(ip, 5000);
}
catch (SocketException)
{
    Console.WriteLine($"Không kết nối được tới {ip}:5000. Kiểm tra server, IP và tường lửa.");
    return;
}

NetworkStream stream = tcp.GetStream();
Console.WriteLine("Đã kết nối! Gõ tin nhắn rồi Enter. Gõ /quit để thoát.");

// LUỒNG NHẬN: chạy song song, liên tục chờ tin từ server và in ra
_ = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            Packet? p = await PacketIO.ReadAsync(stream);
            if (p == null)
            {
                Console.WriteLine("Server đã đóng kết nối.");
                break;
            }
            if (p.Type == "NEW_MSG")
                Console.WriteLine($"[{p.Data["time"]}] {p.Data["from"]}: {p.Data["content"]}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Mất kết nối: {ex.Message}");
    }
});

// LUỒNG CHÍNH: đọc bàn phím và gửi đi
while (true)
{
    string? text = Console.ReadLine();
    if (text == null || text == "/quit") break;
    if (text.Length == 0) continue;

    await PacketIO.WriteAsync(stream, new Packet
    {
        Type = "SEND_MSG",
        Data = new() { ["from"] = name, ["content"] = text }
    });
}