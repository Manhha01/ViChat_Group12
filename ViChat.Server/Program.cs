using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViChat.Server;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;

const int Port = 5000;

// Danh sách client đang kết nối. ConcurrentDictionary an toàn khi nhiều luồng cùng thêm/xóa.
var clients = new ConcurrentDictionary<Guid, ClientSession>();

// Xử lý truyền file (FILE_START / FILE_CHUNK / FILE_END). Phải khai báo SAU khi có clients.
// Bản tạm: gửi file cho mọi client khác; khi có phòng/đăng nhập sẽ thay bằng hàm thật.
var fileRelay = FileRelay.CreateDefault("Uploads", clients);

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

            // Không in log cho FILE_CHUNK vì một file lớn có hàng trăm chunk, sẽ tràn màn hình
            if (packet.Type != PacketTypes.FileChunk)
                Console.WriteLine($"  Nhận {packet.Type} từ {session.Tcp.Client.RemoteEndPoint}");

            switch (packet.Type)
            {
                case PacketTypes.SendMsg:
                    var outPacket = new Packet
                    {
                        Type = PacketTypes.NewMsg,
                        Data = new()
                        {
                            ["from"] = packet.Data.GetValueOrDefault("from", "Ẩn danh"),
                            ["content"] = packet.Data.GetValueOrDefault("content", ""),
                            ["time"] = DateTime.Now.ToString("HH:mm")
                        }
                    };
                    await BroadcastAsync(outPacket);
                    break;

                // Truyền file: giao hết cho FileRelay xử lý
                case PacketTypes.FileStart:
                case PacketTypes.FileChunk:
                case PacketTypes.FileEnd:
                    await fileRelay.HandleAsync(session, packet);
                    break;
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
        // Hủy file đang nhận dở của client này và báo cho người nhận
        await fileRelay.OnDisconnectedAsync(session);

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