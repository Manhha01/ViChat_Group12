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
Console.WriteLine("Đã kết nối! Gõ tin nhắn rồi Enter.");
Console.WriteLine("Gửi file: /file <đường dẫn>   |   Thoát: /quit");

// Bộ nhận file: lưu vào thư mục "Downloads" cạnh file chạy của TestClient
var receiver = new FileReceiver("Downloads");
receiver.Progress += i => Console.Write($"\rNhận {i.Name}: {i.Percent}%   ");
receiver.Completed += r => Console.WriteLine(
    r.Ok ? $"\nNhận xong: {r.SavedPath}" : $"\nLỗi nhận file: {r.Message}");

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

            switch (p.Type)
            {
                case PacketTypes.NewMsg:
                    Console.WriteLine($"[{p.Data["time"]}] {p.Data["from"]}: {p.Data["content"]}");
                    break;

                // Có người gửi file cho mình: lần lượt START → CHUNK... → END
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

// LUỒNG CHÍNH: đọc bàn phím và gửi đi
while (true)
{
    string? text = Console.ReadLine();
    if (text == null || text == "/quit") break;
    if (text.Length == 0) continue;

    // Lệnh gửi file, ví dụ: /file D:\anh.png  (có hoặc không có dấu ngoặc kép)
    if (text.StartsWith("/file "))
    {
        string path = text[6..].Trim().Trim('"');
        try
        {
            var progress = new Progress<int>(pct => Console.Write($"\rGửi: {pct}%   "));
            // Chưa có phòng nên "1" chỉ là giá trị giữ chỗ; server hiện gửi cho mọi client khác.
            await FileSender.SendFileAsync(
                path, FileKinds.TargetRoom, "1",
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

    await PacketIO.WriteAsync(stream, new Packet
    {
        Type = PacketTypes.SendMsg,
        Data = new() { ["from"] = name, ["content"] = text }
    });
}

// Thoát: xóa các file nhận dở (nếu có)
receiver.AbortAll();