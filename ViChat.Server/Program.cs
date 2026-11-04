using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ViChat.Server;
using ViChat.Server.Data;
using ViChat.Shared;

Console.OutputEncoding = Encoding.UTF8;

// ===================== KHỞI TẠO CSDL =====================
// Chưa có CSDL thì tạo, thiếu bảng thì bổ sung (EF Core Migrations)
try
{
    DbInitializer.Initialize();
}
catch (Exception ex)
{
    Console.WriteLine("Không khởi tạo được CSDL: " + ex.Message);
    Console.WriteLine("Kiểm tra LocalDB bằng lệnh: sqllocaldb info");
    return;
}

const int Port = 5000;

// Danh sách client đang kết nối (an toàn khi nhiều luồng cùng thêm/xóa)
var clients = new ConcurrentDictionary<Guid, ClientSession>();

// Khóa dùng khi kiểm tra "tài khoản đang online ở máy khác"
var loginLock = new object();

// Quản lý các phòng chat (có sẵn phòng "General") – Khánh
var rooms = new RoomManager();

// Truyền file (FILE_START / FILE_CHUNK / FILE_END) – Thùy
var fileRelay = FileRelay.CreateDefault("Uploads", clients);

// ===================== NHẬN KẾT NỐI =====================
// IPAddress.Any: nhận kết nối từ MỌI máy trong mạng
var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"Server đang lắng nghe cổng {Port}...");

while (true)
{
    TcpClient tcp = await listener.AcceptTcpClientAsync();

    var session = new ClientSession(tcp);
    clients[session.Id] = session;

    // Chưa cho vào phòng nào: phải đăng nhập xong mới vào General (xem HandleLogin)
    Console.WriteLine($"+ Kết nối từ {tcp.Client.RemoteEndPoint}. Đang có {clients.Count} client.");

    // Mỗi client chạy trong một Task riêng, KHÔNG await để quay lại nhận client tiếp theo
    _ = HandleClientAsync(session);
}

// ===================== XỬ LÝ MỘT CLIENT =====================
async Task HandleClientAsync(ClientSession session)
{
    try
    {
        while (true)
        {
            Packet? packet = await PacketIO.ReadAsync(session.Stream);
            if (packet == null) break;   // client đã đóng kết nối

            // Không in log cho FILE_CHUNK vì một file lớn có hàng trăm chunk
            if (packet.Type != PacketTypes.FileChunk)
                Console.WriteLine($"  Nhận {packet.Type} từ {session.Tcp.Client.RemoteEndPoint}");

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
        await fileRelay.OnDisconnectedAsync(session);                          // 1) hủy file đang nhận dở (Thùy)
        rooms.LeaveAll(session);                                               // 2) rời mọi phòng (Khánh)
        if (session.IsLoggedIn) await PrivateChat.GoOfflineAsync(session);     // 3) báo offline cho mọi người (Đạt)
        clients.TryRemove(session.Id, out _);                                  // 4) xóa khỏi danh sách, đóng kết nối
        session.Tcp.Close();
        Console.WriteLine($"- Một client đã thoát. Còn {clients.Count} client.");
    }
}

// ===================== PHÂN LOẠI GÓI TIN =====================
async Task ProcessPacketAsync(ClientSession s, Packet p)
{
    // Hai gói được phép gửi khi chưa đăng nhập
    if (p.Type == "REGISTER") { await HandleRegister(s, p); return; }
    if (p.Type == "LOGIN") { await HandleLogin(s, p); return; }

    // Mọi gói khác: phải đăng nhập rồi mới được dùng
    if (!s.IsLoggedIn)
    {
        await SendError(s, "NOT_LOGGED_IN", "Bạn cần đăng nhập trước");
        return;
    }

    switch (p.Type)
    {
        // --- Phòng chat (Khánh) ---
        case "CREATE_ROOM": await HandleCreateRoom(s, p); break;
        case "JOIN_ROOM": await HandleJoinRoom(s, p); break;
        case "LEAVE_ROOM": await HandleLeaveRoom(s, p); break;
        case "ROOM_LIST": await HandleRoomList(s); break;
        case PacketTypes.SendMsg: await HandleSendMsg(s, p); break;

        // --- Nhắn riêng 1-1, online, đang nhập, đã xem (Đạt) ---
        case "PRIVATE_MSG": await PrivateChat.HandlePrivateMsgAsync(s, p); break;
        case "ONLINE_LIST": await PrivateChat.HandleOnlineListAsync(s); break;
        case "TYPING": await PrivateChat.HandleTypingAsync(s, p); break;
        case "SEEN": await PrivateChat.HandleSeenAsync(s, p); break;

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

// ===================== ĐĂNG KÝ, ĐĂNG NHẬP (CSDL) =====================
async Task HandleRegister(ClientSession s, Packet p)
{
    var (ok, message) = await AuthService.RegisterAsync(
        p.Data.GetValueOrDefault("username", ""),
        p.Data.GetValueOrDefault("password", ""),
        p.Data.GetValueOrDefault("displayName", ""));

    await s.SendAsync(new Packet
    {
        Type = "REGISTER_RESULT",
        Data = new() { ["success"] = ok ? "true" : "false", ["message"] = message }
    });
}

async Task HandleLogin(ClientSession s, Packet p)
{
    if (s.IsLoggedIn)
    {
        await SendLoginResult(s, false, "Bạn đã đăng nhập rồi");
        return;
    }

    // 1) Kiểm tra tên và mật khẩu trong CSDL
    var user = await AuthService.CheckLoginAsync(
        p.Data.GetValueOrDefault("username", ""),
        p.Data.GetValueOrDefault("password", ""));

    if (user == null)
    {
        await SendLoginResult(s, false, "Sai tên đăng nhập hoặc mật khẩu");
        return;
    }

    // 2) Chặn đăng nhập trùng. Kiểm tra và ghi nhận trong cùng một khóa:
    //    nhiều luồng cùng đăng nhập một tài khoản thì chỉ một luồng thắng
    bool alreadyOnline;
    lock (loginLock)
    {
        alreadyOnline = clients.Values.Any(c => c.UserId == user.Id);
        if (!alreadyOnline)
        {
            s.UserId = user.Id;
            s.Username = user.Username;
        }
    }

    if (alreadyOnline)
    {
        await SendLoginResult(s, false, "Tài khoản đang đăng nhập ở nơi khác");
        return;
    }

    // 3) Đưa vào danh sách online và báo cho mọi người (Đạt)
    if (!await PrivateChat.TryGoOnlineAsync(s))
    {
        // Không vào được danh sách online -> hủy đăng nhập vừa ghi nhận
        s.UserId = null;
        s.Username = "";
        await SendLoginResult(s, false, "Tài khoản đang đăng nhập ở nơi khác");
        return;
    }

    // 4) Vào phòng General và báo thành công
    rooms.Join("General", s);
    await s.SendAsync(new Packet
    {
        Type = "LOGIN_RESULT",
        Data = new()
        {
            ["success"] = "true",
            ["username"] = user.Username,
            ["displayName"] = user.DisplayName
        }
    });
    Console.WriteLine($"  {user.Username} đã đăng nhập. Đang có {PrivateChat.OnlineUsers.Count} người online.");
}

Task SendLoginResult(ClientSession s, bool ok, string message) =>
    s.SendAsync(new Packet
    {
        Type = "LOGIN_RESULT",
        Data = new() { ["success"] = ok ? "true" : "false", ["message"] = message }
    });

// ===================== PHÒNG CHAT (Khánh) =====================
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
            ["from"] = NameOf(s),   // tên do server xác nhận lúc đăng nhập, không lấy từ client
            ["content"] = p.Data.GetValueOrDefault("content", ""),
            ["time"] = DateTime.Now.ToString("HH:mm")
        }
    };
    await room.BroadcastAsync(outPacket);   // chỉ gửi cho người trong phòng
}

// ===================== HÀM TIỆN ÍCH =====================
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