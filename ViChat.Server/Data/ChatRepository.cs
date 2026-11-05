using Microsoft.EntityFrameworkCore;
using ViChat.Shared;

namespace ViChat.Server.Data;

// Mọi việc đọc/ghi phòng chat và tin nhắn vào CSDL nằm ở đây.
// Mỗi hàm tự tạo một ViChatDbContext mới rồi bỏ đi (an toàn khi nhiều client cùng gọi).
public static class ChatRepository
{
    // Một tin trong lịch sử, gửi về client dạng JSON.
    // Kind: "text" | "file" | "image". FileName/FileSize chỉ có ý nghĩa với file và ảnh.
    public record HistoryItem(string MsgId, string From, string Content, string Time,
                              string Kind, string FileName, long FileSize);

    // ===================== PHÒNG =====================

    // Tên tất cả phòng đã lưu. Gọi lúc server khởi động để nạp lại vào RoomManager.
    public static List<string> GetAllRoomNames()
    {
        using var db = new ViChatDbContext();
        return db.Rooms.Select(r => r.Name).ToList();
    }

    // Lưu phòng mới, người tạo là thành viên và là quản trị phòng
    public static async Task SaveRoomAsync(string roomName, int creatorId)
    {
        await using var db = new ViChatDbContext();
        if (await db.Rooms.AnyAsync(r => r.Name == roomName)) return;

        var room = new RoomEntity { Name = roomName, CreatedById = creatorId };
        room.Members.Add(new RoomMemberEntity { UserId = creatorId, IsAdmin = true });

        db.Rooms.Add(room);
        await db.SaveChangesAsync();
    }

    // Ghi nhận một người là thành viên của phòng (nếu chưa có)
    public static async Task AddMemberAsync(string roomName, int userId)
    {
        await using var db = new ViChatDbContext();
        int? roomId = await FindRoomIdAsync(db, roomName);
        if (roomId == null) return;

        bool exists = await db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId);
        if (exists) return;

        db.RoomMembers.Add(new RoomMemberEntity { RoomId = roomId.Value, UserId = userId });
        await db.SaveChangesAsync();
    }

    // Xóa một người khỏi danh sách thành viên của phòng
    public static async Task RemoveMemberAsync(string roomName, int userId)
    {
        await using var db = new ViChatDbContext();
        int? roomId = await FindRoomIdAsync(db, roomName);
        if (roomId == null) return;

        var member = await db.RoomMembers.FirstOrDefaultAsync(m => m.RoomId == roomId && m.UserId == userId);
        if (member == null) return;

        db.RoomMembers.Remove(member);
        await db.SaveChangesAsync();
    }

    // ===================== LƯU TIN NHẮN =====================

    // Tin chữ trong phòng. Trả về Id của tin trong CSDL (0 nếu phòng không tồn tại).
    public static async Task<long> SaveRoomMessageAsync(string roomName, int senderId, string content)
    {
        await using var db = new ViChatDbContext();
        int? roomId = await FindRoomIdAsync(db, roomName);
        if (roomId == null) return 0;

        var message = new MessageEntity { RoomId = roomId, SenderId = senderId, Content = content };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;   // EF Core tự điền Id sau khi lưu
    }

    // Tin chữ riêng 1-1. Trả về Id của tin trong CSDL.
    public static async Task<long> SavePrivateMessageAsync(int senderId, int receiverId, string content)
    {
        await using var db = new ViChatDbContext();
        var message = new MessageEntity { ReceiverId = receiverId, SenderId = senderId, Content = content };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    // File hoặc ảnh đã gửi xong và kiểm tra SHA-256 thành công (thông tin lấy từ FileRelay).
    // Chỉ lưu THÔNG TIN file; nội dung file nằm trong thư mục Uploads của server.
    public static async Task SaveFileMessageAsync(int senderId, IReadOnlyDictionary<string, string> info)
    {
        string targetType = info.GetValueOrDefault("targetType", "");
        string target = info.GetValueOrDefault("target", "");
        string name = info.GetValueOrDefault("name", "file");
        string kind = info.GetValueOrDefault("kind", "") == FileKinds.Image ? FileKinds.Image : FileKinds.File;
        long.TryParse(info.GetValueOrDefault("size", "0"), out long size);
        string sha = info.GetValueOrDefault("sha256", "");

        await using var db = new ViChatDbContext();

        var message = new MessageEntity
        {
            SenderId = senderId,
            MessageType = kind,
            Content = kind == FileKinds.Image ? "🖼 Ảnh" : $"📎 {name}",
            FileName = name,
            FileSize = size,
            FileSha256 = sha.Length > 0 ? sha : null,
            StoredPath = info.GetValueOrDefault("path", "")
        };

        if (targetType == FileKinds.TargetRoom)
        {
            message.RoomId = await FindRoomIdAsync(db, target);
            if (message.RoomId == null) return;
        }
        else
        {
            message.ReceiverId = await FindUserIdAsync(db, target);
            if (message.ReceiverId == null) return;
        }

        db.Messages.Add(message);
        await db.SaveChangesAsync();
    }

    // ===================== LỊCH SỬ =====================

    // "count" tin gần nhất của một phòng, từ cũ đến mới
    public static async Task<List<HistoryItem>> GetRoomHistoryAsync(string roomName, int count = 50)
    {
        await using var db = new ViChatDbContext();
        int? roomId = await FindRoomIdAsync(db, roomName);
        if (roomId == null) return new List<HistoryItem>();

        var query = db.Messages.Where(m => m.RoomId == roomId);
        return await ToHistoryAsync(query, count);
    }

    // "count" tin gần nhất giữa mình và một người khác, từ cũ đến mới
    public static async Task<List<HistoryItem>> GetPrivateHistoryAsync(int myId, string otherUsername, int count = 50)
    {
        await using var db = new ViChatDbContext();
        int? otherId = await FindUserIdAsync(db, otherUsername);
        if (otherId == null) return new List<HistoryItem>();

        // Tin mình gửi cho người đó HOẶC người đó gửi cho mình
        var query = db.Messages.Where(m =>
            (m.SenderId == myId && m.ReceiverId == otherId) ||
            (m.SenderId == otherId && m.ReceiverId == myId));
        return await ToHistoryAsync(query, count);
    }

    // ===================== HÀM PHỤ =====================

    // Lấy "count" tin mới nhất của một truy vấn, đổi sang HistoryItem, sắp xếp cũ -> mới
    private static async Task<List<HistoryItem>> ToHistoryAsync(IQueryable<MessageEntity> query, int count)
    {
        var rows = await query
            .OrderByDescending(m => m.Id)          // mới nhất trước...
            .Take(count)                           // ...lấy đủ "count" tin
            .Select(m => new
            {
                m.Id,
                Sender = m.Sender.Username,
                m.Content,
                m.SentAt,
                m.MessageType,
                m.FileName,
                m.FileSize
            })
            .ToListAsync();

        rows.Reverse();                            // đảo lại: cũ -> mới, đúng thứ tự hiển thị

        return rows.Select(m => new HistoryItem(
            m.Id.ToString(), m.Sender, m.Content, FormatTime(m.SentAt),
            m.MessageType, m.FileName ?? "", m.FileSize ?? 0)).ToList();
    }

    private static async Task<int?> FindRoomIdAsync(ViChatDbContext db, string roomName) =>
        await db.Rooms.Where(r => r.Name == roomName).Select(r => (int?)r.Id).FirstOrDefaultAsync();

    private static async Task<int?> FindUserIdAsync(ViChatDbContext db, string username)
    {
        string name = username.Trim().ToLowerInvariant();
        return await db.Users.Where(u => u.Username == name).Select(u => (int?)u.Id).FirstOrDefaultAsync();
    }

    // Tin hôm nay chỉ hiện giờ, tin ngày khác hiện cả ngày tháng
    private static string FormatTime(DateTime time) =>
        time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("dd/MM HH:mm");
}