using System.Net.Sockets;
using System.Text;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// Mặc định 127.0.0.1 – KHÔNG để IP thật trong code khi push
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
string currentRoom = "General";   // phòng đang gõ tin vào (phần của Hà + Khánh)

// Bộ nhận file (phần của Thùy): lưu vào thư mục "Downloads" cạnh file chạy
var receiver = new FileReceiver("Downloads");
receiver.Progress += i => Console.Write($"\rNhận {i.Name}: {i.Percent}%   ");
receiver.Completed += r => Console.WriteLine(
    r.Ok ? $"\nNhận xong: {r.SavedPath}" : $"\nLỗi nhận file: {r.Message}");

Console.WriteLine("Đã kết nối! Bạn đang ở phòng General.");
Console.WriteLine("Lệnh: /rooms  /create <tên>  /join <tên>  /leave <tên>  /room <tên>");
Console.WriteLine("      /file <đường dẫn>  (gửi file vào phòng hiện tại)   /quit");

// ===== LUỒNG NHẬN =====
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
                // --- Phòng chat (Hà + Khánh) ---
                case PacketTypes.NewMsg:
                    Console.WriteLine(
                        $"[{p.Data.GetValueOrDefault("room", "?")}] [{p.Data.GetValueOrDefault("time", "")}] " +
                        $"{p.Data.GetValueOrDefault("from", "?")}: {p.Data.GetValueOrDefault("content", "")}");
                    break;
                case "ROOM_LIST":
                    Console.WriteLine("Danh sách phòng (tên:số người): " + p.Data.GetValueOrDefault("rooms", ""));
                    break;
                case "CREATE_ROOM":
                case "JOIN_ROOM":
                case "LEAVE_ROOM":
                    Console.WriteLine($"* {p.Type} '{p.Data.GetValueOrDefault("room", "")}' thành công");
                    break;
                case "ERROR":
                    Console.WriteLine(
                        $"! Lỗi {p.Data.GetValueOrDefault("code", "")}: {p.Data.GetValueOrDefault("message", "")}");
                    break;

                // --- Truyền file (Thùy): START → CHUNK... → END ---
                case PacketTypes.FileStart:
                    Console.WriteLine(
                        $"\n{p.Data.GetValueOrDefault("from", "?")} đang gửi file " +
                        $"{p.Data.GetValueOrDefault("name", "")} ({p.Data.GetValueOrDefault("size", "?")} byte)...");
                    receiver.HandleStart(p);
                    break;
                case PacketTypes.FileChunk:
                    receiver.HandleChunk(p);
                    break;
                case PacketTypes.FileEnd:
                    receiver.HandleEnd(p);
                    break;

                // Server báo kết quả file mình gửi (hoặc báo file đang nhận bị hủy)
                case PacketTypes.FileResult:
                    if (p.Data.GetValueOrDefault("ok") == "false")
                        receiver.Abort(p.Data.GetValueOrDefault("fileId", ""));
                    Console.WriteLine(
                        $"\n[FILE_RESULT] {p.Data.GetValueOrDefault("name", "")}: " +
                        $"ok={p.Data.GetValueOrDefault("ok")} {p.Data.GetValueOrDefault("message", "")}");
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

// ===== LUỒNG CHÍNH =====
while (true)
{
    string? text = Console.ReadLine();
    if (text == null || text == "/quit") break;
    if (text.Length == 0) continue;

    // Gửi file (Thùy), ví dụ: /file D:\anh.png  (có hoặc không có dấu ngoặc kép)
    if (text.StartsWith("/file "))
    {
        string path = text[6..].Trim().Trim('"');
        try
        {
            var progress = new Progress<int>(pct => Console.Write($"\rGửi: {pct}%   "));
            // Gửi vào phòng đang đứng (server hiện tại chuyển cho mọi client khác)
            await FileSender.SendFileAsync(
                path, FileKinds.TargetRoom, currentRoom,
                pk => PacketIO.WriteAsync(stream, pk), progress);
            Console.WriteLine("\nĐã gửi xong, chờ server kiểm tra...");
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("Không tìm thấy file. Kiểm tra lại đường dẫn.");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Không gửi được: {ex.Message}");
        }
        continue;
    }

    // Lệnh phòng (Hà + Khánh)
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

    // Tin nhắn thường: gửi vào phòng hiện tại (định dạng của server hiện tại)
    await Send(PacketTypes.SendMsg, new() { ["room"] = currentRoom, ["content"] = text });
}

// Thoát: xóa các file nhận dở (nếu có)
receiver.AbortAll();