using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViChat.Server;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;

const int Port = 5000;

// Danh sách client đang kết nối
var clients = new ConcurrentDictionary<Guid, ClientSession>();

var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"Server đang lắng nghe cổng {Port}...");

while (true)
{
    TcpClient tcp = await listener.AcceptTcpClientAsync();

    // Chặn ngay từ tầng kết nối nếu đã đủ 2 client
    if (clients.Count >= 2)
    {
        Console.WriteLine($"- Từ chối kết nối từ {tcp.Client.RemoteEndPoint} vì server đã đủ 2 người.");
        tcp.Close();
        continue;
    }

    var session = new ClientSession(tcp);
    clients[session.Id] = session;
    Console.WriteLine($"+ Kết nối từ {tcp.Client.RemoteEndPoint}. Đang có {clients.Count} client.");

    _ = HandleClientAsync(session);
}

// Xử lý MỘT client
async Task HandleClientAsync(ClientSession session)
{
    try
    {
        while (true)
        {
            Packet? packet = await PacketIO.ReadAsync(session.Stream);
            if (packet == null) break;

            Console.WriteLine($"  Nhận {packet.Type} từ {session.Tcp.Client.RemoteEndPoint}");

            // Các gói tin nhạy cảm bắt buộc phải đăng nhập trước
            if (session.Username == null &&
                packet.Type is "PRIVATE_MSG" or "TYPING" or "SEEN" or "ONLINE_LIST" or "FILE_START" or "FILE_CHUNK" or "FILE_END")
            {
                await PrivateChat.SendErrorAsync(session, "NOT_LOGGED_IN", "Bạn chưa đăng nhập.");
                continue;
            }

            switch (packet.Type)
            {
                case "SEND_MSG":
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
                        break;
                    }

                case "LOGIN":
                    {
                        if (session.Username != null)
                        {
                            await PrivateChat.SendErrorAsync(session, "BAD_REQUEST", "Bạn đã đăng nhập rồi.");
                            break;
                        }

                        // Kiểm tra giới hạn tối đa 2 người đăng nhập
                        if (PrivateChat.OnlineUsers.Count >= 2)
                        {
                            await PrivateChat.SendErrorAsync(session, "SERVER_FULL", "Phòng chat đã đủ 2 người, không nhận thêm.");
                            session.Tcp.Close();
                            return;
                        }

                        var name = packet.Data.GetValueOrDefault("username")?.Trim();
                        if (string.IsNullOrEmpty(name) || name.Contains(','))
                        {
                            await PrivateChat.SendErrorAsync(session, "BAD_REQUEST", "Tên không hợp lệ (không được rỗng hoặc chứa dấu phẩy).");
                            break;
                        }
                        session.Username = name;
                        if (await PrivateChat.TryGoOnlineAsync(session))
                        {
                            await PrivateChat.TrySendAsync(session,
                                PrivateChat.MakePacket("LOGIN_RESULT", ("ok", "true")));
                        }
                        else
                        {
                            session.Username = null;
                            await PrivateChat.SendErrorAsync(session, "ALREADY_ONLINE", "Tài khoản đang online ở nơi khác.");
                            session.Tcp.Close();
                            return;
                        }
                        break;
                    }

                case "PRIVATE_MSG": await PrivateChat.HandlePrivateMsgAsync(session, packet); break;
                case "TYPING": await PrivateChat.HandleTypingAsync(session, packet); break;
                case "SEEN": await PrivateChat.HandleSeenAsync(session, packet); break;
                case "ONLINE_LIST": await PrivateChat.HandleOnlineListAsync(session); break;

                // Xử lý chuyển tiếp truyền file, ảnh, video 1-1
                case "FILE_START":
                case "FILE_CHUNK":
                case "FILE_END":
                    await PrivateChat.HandleFilePacketAsync(session, packet);
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Lỗi với client: {ex.Message}");
    }
    finally
    {
        await PrivateChat.GoOfflineAsync(session);
        clients.TryRemove(session.Id, out _);
        session.Tcp.Close();
        Console.WriteLine($"- Một client đã thoát. Còn {clients.Count} client.");
    }
}

async Task BroadcastAsync(Packet packet)
{
    var tasks = clients.Values.Select(async client =>
    {
        try
        {
            await client.SendAsync(packet);
        }
        catch
        {
        }
    });

    await Task.WhenAll(tasks);
}