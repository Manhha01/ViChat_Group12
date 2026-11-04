using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;

const int Port = 5000;

// Danh sách client đang kết nối. ConcurrentDictionary an toàn khi nhiều luồng cùng thêm/xóa.
var clients = new ConcurrentDictionary<Guid, ClientSession>();

// IPAddress.Any: nhận kết nối từ MỌI máy trong mạng, không chỉ máy này
var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"Server đang lắng nghe cổng {Port}...");

while (true)
{
    // Chờ client mới. Trong lúc chờ, luồng không bị chiếm (nhờ await).
    TcpClient tcp = await listener.AcceptTcpClientAsync();

    var session = new ClientSession(tcp);
    clients[session.Id] = session;
    Console.WriteLine($"+ Kết nối từ {tcp.Client.RemoteEndPoint}. Đang có {clients.Count} client.");

    // Giao client này cho một Task riêng, KHÔNG await,
    // để vòng lặp quay lại nhận client tiếp theo ngay.
    _ = HandleClientAsync(session);
}

// Xử lý MỘT client: đọc gói liên tục cho tới khi client thoát
async Task HandleClientAsync(ClientSession session)
{
    try
    {
        while (true)
        {
            Packet? packet = await PacketIO.ReadAsync(session.Stream);
            if (packet == null) break;   // client đã đóng kết nối

            Console.WriteLine($"  Nhận {packet.Type} từ {session.Tcp.Client.RemoteEndPoint}");

            if (packet.Type == "SEND_MSG")
            {
                var outPacket = new Packet
                {
                    Type = "NEW_MSG",
                    Data = new()
                    {
                        ["from"] = packet.Data.GetValueOrDefault("from", "Ẩn danh"),
                        ["content"] = packet.Data.GetValueOrDefault("content", ""),
                        ["time"] = DateTime.Now.ToString("HH:mm")
                    }
                };
                await BroadcastAsync(outPacket);
            }
        }
    }
    catch (Exception ex)
    {
        // Rút dây mạng, tắt ngang client... sẽ rơi vào đây
        Console.WriteLine($"  Lỗi với client: {ex.Message}");
    }
    finally
    {
        // Dù thoát kiểu gì cũng phải dọn dẹp
        clients.TryRemove(session.Id, out _);
        session.Tcp.Close();
        Console.WriteLine($"- Một client đã thoát. Còn {clients.Count} client.");
    }
}

// Gửi một gói tới TẤT CẢ client
async Task BroadcastAsync(Packet packet)
{
    foreach (var client in clients.Values)
    {
        try
        {
            await client.SendAsync(packet);
        }
        catch
        {
            // Client này có thể vừa mất kết nối; bỏ qua, phần finally của nó sẽ dọn dẹp
        }
    }
}