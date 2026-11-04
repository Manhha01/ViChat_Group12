namespace ViChat.Server.Data;

// Mỗi class = một bảng, mỗi property = một cột, mỗi đối tượng = một dòng.
// Đuôi "Entity" để khỏi trùng tên với class Room có sẵn của server.

// Bảng Users: tài khoản
public class UserEntity
{
    public int Id { get; set; }                        // khóa chính, tự tăng
    public string Username { get; set; } = "";         // tên đăng nhập, không trùng
    public string PasswordHash { get; set; } = "";     // mật khẩu ĐÃ BĂM, không lưu mật khẩu thật
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// Bảng Rooms: phòng chat
public class RoomEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";             // tên phòng, không trùng
    public int? CreatedById { get; set; }              // null = phòng hệ thống (General)
    public UserEntity? Creator { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<RoomMemberEntity> Members { get; set; } = new();
}

// Bảng RoomMembers: ai ở phòng nào, ai là quản trị phòng
public class RoomMemberEntity
{
    public int RoomId { get; set; }
    public RoomEntity Room { get; set; } = null!;
    public int UserId { get; set; }
    public UserEntity User { get; set; } = null!;
    public bool IsAdmin { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.Now;
}

// Bảng Messages: tin trong phòng (có RoomId) HOẶC tin riêng 1-1 (có ReceiverId)
public class MessageEntity
{
    public long Id { get; set; }
    public int? RoomId { get; set; }
    public RoomEntity? Room { get; set; }
    public int SenderId { get; set; }
    public UserEntity Sender { get; set; } = null!;
    public int? ReceiverId { get; set; }
    public UserEntity? Receiver { get; set; }

    public string Content { get; set; } = "";
    public string MessageType { get; set; } = "text";  // text / image / file / system

    // Chỉ dùng khi là ảnh hoặc file
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public string? FileSha256 { get; set; }
    public string? StoredPath { get; set; }            // nơi server lưu file trên ổ đĩa

    public DateTime SentAt { get; set; } = DateTime.Now;
}