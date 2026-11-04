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

// Quản lý các phòng chat (có sẵn phòng "General") – Khánh
var rooms = new RoomManager();

// Truyền file (FILE_START / FILE_CHUNK / FILE_END) – Thùy.
// Bản tạm: gửi file cho mọi client khác.
var fileRelay = FileRelay.CreateDefault("Uploads", clients);

// IPAddress.Any: nhận kết nối từ MỌI máy trong mạng
var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"Server đang lắng nghe cổng {Port}...");

while (true)
{
    TcpClient tcp = await listener.AcceptTcpClientAsync();

    var session = new ClientSession(tcp);
    clients[session.Id] = session;

    // Client mới tự vào phòng General
    rooms.Join("General", session);

    Console.WriteLine($"+ Kết nối từ {tcp.Client.RemoteEndPoint}. Đang có {clients.Count} client.");
    _ = HandleClientAsync(session);
}

async Task HandleClientAsync(ClientSession session)
{
    bool isOnline = false;   // đã vào danh sách online của PrivateChat chưa

    try
    {
        while (true)
        {
            Packet? packet = await PacketIO.ReadAsync(session.Stream);
            if (packet == null) break;

            // Không in log cho FILE_CHUNK vì một file lớn có hàng trăm chunk
            if (packet.Type != PacketTypes.FileChunk)
                Console.WriteLine($"  Nhận {packet.Type} từ {session.Tcp.Client.RemoteEndPoint}");

            // TẠM THỜI: lấy tên từ trường "from" (XÓA khi Kiên làm xong LOGIN).
            // Chỉ lấy LẦN ĐẦU, không cho đổi tên giữa chừng (tránh lệch danh sách online).
            if (!isOnline)
            {
                string from = packet.Data.GetValueOrDefault("from", "").Trim();
                if (from.Length > 0)
                {
                    session.Username = from;

                    // Đưa vào danh sách online (Đạt). Trùng tên -> báo lỗi rồi ngắt.
                    if (!await PrivateChat.TryGoOnlineAsync(session))
                    {
                        await SendError(session, "ALREADY_LOGGED_IN", $"Tên '{from}' đang được dùng.");
                        break;
                    }
                    isOnline = true;
                    Console.WriteLine($"  {from} đã online. Đang có {PrivateChat.OnlineUsers.Count} người online.");
                }
            }

            await ProcessPacketAsync(session, packet);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Lỗi với client: {ex.Message}");
    }
    finally
    {
        // Dù thoát kiểu gì cũng phải dọn dẹp:
        await fileRelay.OnDisconnectedAsync(session);              // 1) hủy file đang nhận dở (Thùy)
        rooms.LeaveAll(session);                                   // 2) rời mọi phòng (Khánh)
        if (isOnline) await PrivateChat.GoOfflineAsync(session);   // 3) báo offline cho mọi người (Đạt)
        clients.TryRemove(session.Id, out _);                      // 4) xóa khỏi danh sách, đóng kết nối
        session.Tcp.Close();
        Console.WriteLine($"- Một client đã thoát. Còn {clients.Count} client.");
    }
}

// Phân loại gói tin
async Task ProcessPacketAsync(ClientSession s, Packet p)
{
    switch (p.Type)
    {
        // --- Phòng chat (Khánh) ---
        case "CREATE_ROOM": await HandleCreateRoom(s, p); break;
        case "JOIN_ROOM": await HandleJoinRoom(s, p); break;
        case "LEAVE_ROOM": await HandleLeaveRoom(s, p); break;
        case "ROOM_LIST": await HandleRoomList(s); break;
        case PacketTypes.SendMsg: await HandleSendMsg(s, p); break;

        // --- Nhắn riêng 1-1, online, đang nhập, đã xem (Đạt) ---
        case "PRIVATE_MSG":
        case "ONLINE_LIST":
        case "TYPING":
        case "SEEN":
            if (string.IsNullOrEmpty(s.Username))
            {
                await SendError(s, "NOT_LOGGED_IN", "Chưa có tên người dùng.");
                break;
            }
            if (p.Type == "PRIVATE_MSG") await PrivateChat.HandlePrivateMsgAsync(s, p);
            else if (p.Type == "ONLINE_LIST") await PrivateChat.HandleOnlineListAsync(s);
            else if (p.Type == "TYPING") await PrivateChat.HandleTypingAsync(s, p);
            else await PrivateChat.HandleSeenAsync(s, p);
            break;

        // --- Truyền file (Thùy): giao hết cho FileRelay ---
        case PacketTypes.FileStart:
        case PacketTypes.FileChunk:
        case PacketTypes.FileEnd:
            await fileRelay.HandleAsync(s, p);
            break;

        default:
            await SendError(s, "UNKNOWN_TYPE", $"Không hỗ trợ gói {p.Type}");
            break;
    }
}

async Task HandleCreateRoom(ClientSession s, Packet p)
{
    string name = p.Data.GetValueOrDefault("room", "").Trim();
    // Cấm dấu , và : vì ROOM_LIST dùng chúng để ngăn cách
    if (name.Length == 0 || name.Contains(',') || name.Contains(':'))
    {
        await SendError(s, "BAD_ROOM_NAME", "Tên phòng không hợp lệ");
        return;
    }

    if (rooms.Create(name, NameOf(s)) == null)
    {
        await SendError(s, "ROOM_EXISTS", "Phòng đã tồn tại");
        return;
    }

    rooms.Join(name, s);   // người tạo tự vào phòng
    await s.SendAsync(new Packet { Type = "CREATE_ROOM", Data = new() { ["room"] = name, ["ok"] = "true" } });
}

async Task HandleJoinRoom(ClientSession s, Packet p)
{
    string name = p.Data.GetValueOrDefault("room", "").Trim();
    if (!rooms.Join(name, s))
    {
        await SendError(s, "ROOM_NOT_FOUND", "Phòng không tồn tại");
        return;
    }

    var room = rooms.Get(name)!;
    await s.SendAsync(new Packet { Type = "JOIN_ROOM", Data = new() { ["room"] = room.Name, ["ok"] = "true" } });
    await room.BroadcastAsync(SystemMsg(room.Name, $"{NameOf(s)} đã vào phòng"), exceptId: s.Id);
}

async Task HandleLeaveRoom(ClientSession s, Packet p)
{
    string name = p.Data.GetValueOrDefault("room", "").Trim();
    var room = rooms.Get(name);
    if (room == null || !room.Members.ContainsKey(s.Id))
    {
        await SendError(s, "NOT_IN_ROOM", "Bạn không ở trong phòng này");
        return;
    }

    rooms.Leave(name, s);
    await s.SendAsync(new Packet { Type = "LEAVE_ROOM", Data = new() { ["room"] = room.Name, ["ok"] = "true" } });
    await room.BroadcastAsync(SystemMsg(room.Name, $"{NameOf(s)} đã rời phòng"));
}

Task HandleRoomList(ClientSession s) =>
    s.SendAsync(new Packet { Type = "ROOM_LIST", Data = new() { ["rooms"] = rooms.ListAsString() } });

async Task HandleSendMsg(ClientSession s, Packet p)
{
    string roomName = p.Data.GetValueOrDefault("room", "General");
    var room = rooms.Get(roomName);

    if (room == null)
    {
        await SendError(s, "ROOM_NOT_FOUND", "Phòng không tồn tại");
        return;
    }
    if (!room.Members.ContainsKey(s.Id))
    {
        await SendError(s, "NOT_IN_ROOM", "Bạn chưa vào phòng này");
        return;
    }

    var outPacket = new Packet
    {
        Type = PacketTypes.NewMsg,
        Data = new()
        {
            ["room"] = room.Name,
            ["msgId"] = Guid.NewGuid().ToString(),
            ["from"] = NameOf(s),
            ["content"] = p.Data.GetValueOrDefault("content", ""),
            ["time"] = DateTime.Now.ToString("HH:mm")
        }
    };
    await room.BroadcastAsync(outPacket);   // chỉ gửi cho người trong phòng
}

// ===== Hàm tiện ích =====
string NameOf(ClientSession s) => string.IsNullOrEmpty(s.Username) ? "Ẩn danh" : s.Username;

Task SendError(ClientSession s, string code, string message) =>
    s.SendAsync(new Packet { Type = "ERROR", Data = new() { ["code"] = code, ["message"] = message } });

// Thông báo hệ thống dùng chính gói NEW_MSG nên client hiển thị được ngay
Packet SystemMsg(string room, string text) => new()
{
    Type = PacketTypes.NewMsg,
    Data = new()
    {
        ["room"] = room,
        ["msgId"] = Guid.NewGuid().ToString(),
        ["from"] = "Hệ thống",
        ["content"] = text,
        ["time"] = DateTime.Now.ToString("HH:mm")
    }
};