using CommunityToolkit.Mvvm.ComponentModel;

namespace ViChat.Client.Models;

// Một dòng trong danh sách phòng bên trái.
// Kế thừa ObservableObject vì các giá trị (số người, tin cuối, số tin chưa đọc) thay đổi liên tục.
public partial class RoomItem : ObservableObject
{
    public RoomItem(string name) => Name = name;

    public string Name { get; }
    public string Initials => AvatarHelper.Initials(Name);
    public string AvatarColor => AvatarHelper.ColorFor(Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private int memberCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private bool isJoined;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string lastMessage = "";

    [ObservableProperty] private string lastTime = "";

    public bool HasUnread => UnreadCount > 0;

    // Dòng chữ nhỏ dưới tên phòng
    public string Subtitle =>
        !IsJoined ? $"Chưa tham gia · {MemberCount} người"
        : LastMessage.Length > 0 ? LastMessage
        : $"{MemberCount} người trong phòng";
}