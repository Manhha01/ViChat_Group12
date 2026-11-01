using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;

const int Port = 5000;

// Danh sách client đang kết nối
var clients = new ConcurrentDictionary<Guid, ClientSession>();

// Quản lý các phòng chat (có sẵn phòng "General")
var rooms = new RoomManager();

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
    try
    {
        while (true)
        {
            Packet? packet = await PacketIO.ReadAsync(session.Stream);
            if (packet == null) break;

            Console.WriteLine($"  Nhận {packet.Type} từ {session.Tcp.Client.RemoteEndPoint}");

            // TẠM THỜI: lấy tên từ trường "from". XÓA khi thành viên 3 làm xong LOGIN.
            string from = packet.Data.GetValueOrDefault("from", "");
            if (!string.IsNullOrWhiteSpace(from)) session.Username = from;

            await ProcessPacketAsync(session, packet);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Lỗi với client: {ex.Message}");
    }
    finally
    {
        // Dù thoát kiểu gì cũng phải dọn dẹp: rời mọi phòng rồi mới xóa khỏi danh sách
        rooms.LeaveAll(session);
        clients.TryRemove(session.Id, out _);
        session.Tcp.Close();
        Console.WriteLine($"- Một client đã thoát. Còn {clients.Count} client.");
    }
}

// Phân loại gói tin
async Task ProcessPacketAsync(ClientSession s, Packet p)
{
    switch (p.Type)
    {
        case "CREATE_ROOM": await HandleCreateRoom(s, p); break;
        case "JOIN_ROOM": await HandleJoinRoom(s, p); break;
        case "LEAVE_ROOM": await HandleLeaveRoom(s, p); break;
        case "ROOM_LIST": await HandleRoomList(s); break;
        case "SEND_MSG": await HandleSendMsg(s, p); break;
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
    // Phải là thành viên mới được gửi vào phòng
    if (!room.Members.ContainsKey(s.Id))
    {
        await SendError(s, "NOT_IN_ROOM", "Bạn chưa vào phòng này");
        return;
    }

    var outPacket = new Packet
    {
        Type = "NEW_MSG",
        Data = new()
        {
            ["room"] = room.Name,
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

// Thông báo hệ thống dùng chính gói NEW_MSG nên TestClient hiển thị được ngay
Packet SystemMsg(string room, string text) => new()
{
    Type = "NEW_MSG",
    Data = new()
    {
        ["room"] = room,
        ["from"] = "Hệ thống",
        ["content"] = text,
        ["time"] = DateTime.Now.ToString("HH:mm")
    }
};