namespace ViChat.Shared;

/// <summary>
/// Hằng số tên loại gói tin (Packet.Type). Luôn dùng các hằng số này
/// thay vì viết chuỗi "SEND_MSG" bằng tay để tránh gõ sai.
/// Mô tả chi tiết từng gói: xem docs/protocol.md.
/// </summary>
public static class PacketTypes
{
    // ---- Xác thực ----
    public const string Register = "REGISTER";
    public const string Login = "LOGIN";
    public const string LoginResult = "LOGIN_RESULT";
    public const string Logout = "LOGOUT";

    // ---- Người dùng online ----
    public const string OnlineList = "ONLINE_LIST";
    public const string UserStatus = "USER_STATUS";

    // ---- Phòng chat ----
    public const string CreateRoom = "CREATE_ROOM";
    public const string JoinRoom = "JOIN_ROOM";
    public const string LeaveRoom = "LEAVE_ROOM";
    public const string RoomList = "ROOM_LIST";
    public const string RoomMembers = "ROOM_MEMBERS";
    public const string KickMember = "KICK_MEMBER";

    // ---- Tin nhắn ----
    public const string SendMsg = "SEND_MSG";
    public const string NewMsg = "NEW_MSG";
    public const string PrivateMsg = "PRIVATE_MSG";
    public const string Typing = "TYPING";
    public const string Seen = "SEEN";
    public const string GetHistory = "GET_HISTORY";
    public const string History = "HISTORY";

    // ---- Truyền file / ảnh ----
    public const string FileStart = "FILE_START";
    public const string FileChunk = "FILE_CHUNK";
    public const string FileEnd = "FILE_END";
    public const string FileResult = "FILE_RESULT";

    // ---- Hệ thống ----
    public const string Error = "ERROR";
    public const string Ping = "PING";
    public const string Pong = "PONG";

    /// <summary>Các gói client chưa đăng nhập vẫn được gửi.</summary>
    public static bool AllowedBeforeLogin(string type) =>
        type == Register || type == Login || type == Ping;
}

/// <summary>Mã lỗi đặt trong Data["code"] của gói ERROR.</summary>
public static class ErrorCodes
{
    public const string NotLoggedIn = "NOT_LOGGED_IN";
    public const string BadCredentials = "BAD_CREDENTIALS";
    public const string UserExists = "USER_EXISTS";
    public const string AlreadyLoggedIn = "ALREADY_LOGGED_IN";
    public const string RoomNotFound = "ROOM_NOT_FOUND";
    public const string RoomExists = "ROOM_EXISTS";
    public const string NotInRoom = "NOT_IN_ROOM";
    public const string NotAdmin = "NOT_ADMIN";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string BadRequest = "BAD_REQUEST";
    public const string UnknownType = "UNKNOWN_TYPE";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileCorrupt = "FILE_CORRUPT";
    public const string FileUnknown = "FILE_UNKNOWN";
    public const string ServerError = "SERVER_ERROR";
}

/// <summary>Các giới hạn dùng chung của giao thức.</summary>
public static class ProtocolLimits
{
    /// <summary>Kích thước tối đa của phần JSON một gói (byte). PacketIO nên chặn vượt mức này.</summary>
    public const int MaxPacketBytes = 1 * 1024 * 1024; // 1 MB

    /// <summary>Kích thước một chunk file (byte, trước khi Base64).</summary>
    public const int FileChunkBytes = 64 * 1024; // 64 KB

    /// <summary>Kích thước file tối đa cho phép gửi.</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024; // 50 MB
}

/// <summary>Giá trị cho Data["kind"] và Data["targetType"] của FILE_START.</summary>
public static class FileKinds
{
    public const string Image = "image";
    public const string File = "file";
    public const string TargetRoom = "room";
    public const string TargetUser = "user";
}