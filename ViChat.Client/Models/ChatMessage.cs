namespace ViChat.Client.Models;

// Một tin nhắn hiển thị trong khung chat
public class ChatMessage
{
    public string Room { get; init; } = "";
    public string From { get; init; } = "";
    public string Content { get; init; } = "";
    public string Time { get; init; } = "";
    public bool IsMine { get; init; }     // tin của mình -> bong bóng tím bên phải
    public bool IsSystem { get; init; }   // "... đã vào phòng" -> chữ xám ở giữa

    public string Initials => AvatarHelper.Initials(From);
    public string AvatarColor => AvatarHelper.ColorFor(From);
}