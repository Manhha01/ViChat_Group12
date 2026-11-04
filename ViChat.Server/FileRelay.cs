using System.Collections.Concurrent;
using ViChat.Shared;

namespace ViChat.Server;

/// <summary>
/// Xử lý truyền file phía server:
///  - Nhận FILE_START / FILE_CHUNK / FILE_END từ một client, lưu vào thư mục Uploads và kiểm tra SHA-256.
///  - Chuyển tiếp (relay) từng gói cho người nhận (qua ClientSession.SendAsync, có khóa).
///  - Trả FILE_RESULT cho người gửi.
///
/// Giả định ClientSession (code hiện có) có: Guid Id và Task SendAsync(Packet).
/// Việc "ai là người nhận" và "username của người gửi" do thành viên 1 và 3 cung cấp
/// qua hai hàm truyền vào constructor, nên FileRelay không phụ thuộc vào code của họ.
/// </summary>
public class FileRelay
{
    private sealed class Route
    {
        public ClientSession[] Recipients = Array.Empty<ClientSession>();
    }

    private readonly string _uploadDir;
    private readonly Func<ClientSession, string> _getUsername;
    private readonly Func<ClientSession, string, string, IReadOnlyCollection<ClientSession>> _resolveRecipients;

    // Mỗi kết nối một FileReceiver (các gói của một kết nối đến tuần tự).
    private readonly ConcurrentDictionary<Guid, FileReceiver> _receivers = new();
    // Khóa "sessionId:fileId" → danh sách người nhận đã chốt lúc FILE_START.
    private readonly ConcurrentDictionary<string, Route> _routes = new();

    /// <summary>Báo khi một file đã lưu và kiểm tra xong, để thành viên 3 ghi vào bảng Files / Messages.</summary>
    public event Action<ClientSession, FileResultInfo, IReadOnlyDictionary<string, string>>? FileSaved;

    /// <param name="uploadDir">Thư mục lưu file trên server, ví dụ "Uploads".</param>
    /// <param name="getUsername">Lấy username của người gửi (trước khi có đăng nhập có thể trả về Id rút gọn).</param>
    /// <param name="resolveRecipients">
    /// (người gửi, targetType, target) → danh sách người nhận, KHÔNG gồm người gửi.
    /// Trả về rỗng nếu không có ai (phòng/người dùng không tồn tại).
    /// </param>
    public FileRelay(
        string uploadDir,
        Func<ClientSession, string> getUsername,
        Func<ClientSession, string, string, IReadOnlyCollection<ClientSession>> resolveRecipients)
    {
        _uploadDir = uploadDir;
        _getUsername = getUsername;
        _resolveRecipients = resolveRecipients;
        Directory.CreateDirectory(_uploadDir);
    }

    /// <summary>
    /// Bản tạm khi chưa có phòng/đăng nhập: gửi cho tất cả client khác đang kết nối.
    /// Khi vai 1 và vai 3 xong, thay bằng hàm thật.
    /// </summary>
    public static FileRelay CreateDefault(string uploadDir, ConcurrentDictionary<Guid, ClientSession> sessions) =>
        new(uploadDir,
            s => s.Id.ToString("N")[..8],
            (sender, _, _) => sessions.Values.Where(x => x.Id != sender.Id).ToArray());

    /// <summary>Gọi trong HandleClientAsync khi gặp FILE_START, FILE_CHUNK hoặc FILE_END.</summary>
    public async Task HandleAsync(ClientSession sender, Packet p)
    {
        var receiver = _receivers.GetOrAdd(sender.Id, _ => new FileReceiver(_uploadDir));

        switch (p.Type)
        {
            case PacketTypes.FileStart: await OnStartAsync(sender, receiver, p); break;
            case PacketTypes.FileChunk: await OnChunkAsync(sender, receiver, p); break;
            case PacketTypes.FileEnd: await OnEndAsync(sender, receiver, p); break;
        }
    }

    /// <summary>Gọi khi một client ngắt kết nối: hủy file dở, báo người nhận.</summary>
    public async Task OnDisconnectedAsync(ClientSession s)
    {
        if (_receivers.TryRemove(s.Id, out var receiver))
            receiver.AbortAll();

        string prefix = s.Id + ":";
        foreach (var key in _routes.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            if (!_routes.TryRemove(key, out var route)) continue;
            string fileId = key[prefix.Length..];
            var fail = new FileResultInfo(fileId, "", false, ErrorCodes.FileCorrupt,
                "Người gửi mất kết nối giữa chừng.", null).ToPacket();
            foreach (var r in route.Recipients) await SafeSendAsync(r, fail);
        }
    }

    // ---------------- FILE_START ----------------
    private async Task OnStartAsync(ClientSession sender, FileReceiver receiver, Packet p)
    {
        string fileId = Get(p, "fileId");
        string targetType = Get(p, "targetType");
        string target = Get(p, "target");

        if ((targetType != FileKinds.TargetRoom && targetType != FileKinds.TargetUser) || target == "")
        {
            await SafeSendAsync(sender, new FileResultInfo(fileId, Get(p, "name"), false,
                ErrorCodes.BadRequest, "Thiếu targetType/target.", null).ToPacket());
            return;
        }

        // Chốt người nhận trước khi tạo file, để không lưu file vô ích.
        var recipients = _resolveRecipients(sender, targetType, target);
        if (recipients.Count == 0)
        {
            string code = targetType == FileKinds.TargetRoom ? ErrorCodes.RoomNotFound : ErrorCodes.UserNotFound;
            await SafeSendAsync(sender, new FileResultInfo(fileId, Get(p, "name"), false,
                code, "Không có người nhận.", null).ToPacket());
            return;
        }

        var error = receiver.HandleStart(p);
        if (error != null)
        {
            await SafeSendAsync(sender, error.ToPacket());
            return;
        }

        _routes[sender.Id + ":" + fileId] = new Route { Recipients = recipients.ToArray() };

        // Chuyển tiếp, thêm "from" để người nhận biết ai gửi.
        var fwd = Clone(p);
        fwd.Data["from"] = _getUsername(sender);
        foreach (var r in recipients) await SafeSendAsync(r, fwd);
    }

    // ---------------- FILE_CHUNK ----------------
    private async Task OnChunkAsync(ClientSession sender, FileReceiver receiver, Packet p)
    {
        string fileId = Get(p, "fileId");
        string key = sender.Id + ":" + fileId;

        var error = receiver.HandleChunk(p);
        if (error != null)
        {
            // Hủy bên người nhận luôn để họ xóa file tạm.
            if (_routes.TryRemove(key, out var dead))
                foreach (var r in dead.Recipients) await SafeSendAsync(r, error.ToPacket());
            await SafeSendAsync(sender, error.ToPacket());
            return;
        }

        if (_routes.TryGetValue(key, out var route))
            foreach (var r in route.Recipients) await SafeSendAsync(r, p);
    }

    // ---------------- FILE_END ----------------
    private async Task OnEndAsync(ClientSession sender, FileReceiver receiver, Packet p)
    {
        string fileId = Get(p, "fileId");
        string key = sender.Id + ":" + fileId;

        // Receiver đóng file, tính lại SHA-256 và cho biết file có nguyên vẹn không.
        var result = receiver.HandleEnd(p);

        if (_routes.TryRemove(key, out var route))
            foreach (var r in route.Recipients) await SafeSendAsync(r, p); // người nhận tự kiểm tra SHA-256

        await SafeSendAsync(sender, result.ToPacket());

        if (result.Ok)
        {
            var info = new Dictionary<string, string>
            {
                ["from"] = _getUsername(sender),
                ["fileId"] = result.FileId,
                ["name"] = result.Name,
                ["path"] = result.SavedPath ?? ""
            };
            FileSaved?.Invoke(sender, result, info);
        }
    }

    // ---------------- hàm phụ ----------------
    private static async Task SafeSendAsync(ClientSession s, Packet p)
    {
        try { await s.SendAsync(p); }
        catch { /* client này đã rớt; vòng đọc của nó sẽ tự dọn */ }
    }

    private static string Get(Packet p, string key) =>
        p.Data != null && p.Data.TryGetValue(key, out var v) ? v : "";

    private static Packet Clone(Packet p) => new()
    {
        Type = p.Type,
        Data = new Dictionary<string, string>(p.Data)
    };
}