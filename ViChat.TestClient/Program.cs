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
string currentRoom = "General";   // phòng đang gõ tin vào

Console.WriteLine("Đã kết nối! Bạn đang ở phòng General.");
Console.WriteLine("Lệnh: /rooms  /create <tên>  /join <tên>  /leave <tên>  /room <tên>  /quit");

// LUỒNG NHẬN
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

            switch (p.Type)
            {
                case "NEW_MSG":
                    Console.WriteLine($"[{p.Data["room"]}] [{p.Data["time"]}] {p.Data["from"]}: {p.Data["content"]}");
                    break;
                case "ROOM_LIST":
                    Console.WriteLine("Danh sách phòng (tên:số người): " + p.Data["rooms"]);
                    break;
                case "CREATE_ROOM":
                case "JOIN_ROOM":
                case "LEAVE_ROOM":
                    Console.WriteLine($"* {p.Type} '{p.Data["room"]}' thành công");
                    break;
                case "ERROR":
                    Console.WriteLine($"! Lỗi {p.Data["code"]}: {p.Data["message"]}");
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Mất kết nối: {ex.Message}");
    }
});

// Gửi một gói; luôn kèm "from" để server biết tên (tạm thời, đến khi có LOGIN)
async Task Send(string type, Dictionary<string, string> data)
{
    data["from"] = name;
    await PacketIO.WriteAsync(stream, new Packet { Type = type, Data = data });
}

// LUỒNG CHÍNH
while (true)
{
    string? text = Console.ReadLine();
    if (text == null || text == "/quit") break;
    if (text.Length == 0) continue;

    if (text.StartsWith('/'))
    {
        string[] parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        string cmd = parts[0];
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "/rooms":
                await Send("ROOM_LIST", new());
                break;
            case "/create":
                await Send("CREATE_ROOM", new() { ["room"] = arg });
                currentRoom = arg;
                break;
            case "/join":
                await Send("JOIN_ROOM", new() { ["room"] = arg });
                currentRoom = arg;
                break;
            case "/leave":
                await Send("LEAVE_ROOM", new() { ["room"] = arg });
                break;
            case "/room":
                currentRoom = arg;
                Console.WriteLine($"Giờ bạn gõ tin vào phòng {currentRoom}");
                break;
            default:
                Console.WriteLine("Lệnh không có.");
                break;
        }
        continue;
    }

    await Send("SEND_MSG", new() { ["room"] = currentRoom, ["content"] = text });
}