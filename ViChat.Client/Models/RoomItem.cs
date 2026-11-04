using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Documents;

namespace ViChat.Client.Models;

// Một dòng ở cột trái: có thể là PHÒNG CHAT hoặc CUỘC TRÒ CHUYỆN RIÊNG với một người
public partial class RoomItem : ObservableObject
{
    public RoomItem(string name, bool isPrivate = false)
    {
        Name = name;
        IsPrivate = isPrivate;
        if (isPrivate) IsJoined = true;   // chat riêng thì luôn "đã tham gia" -> ô nhập tin dùng được
    }

    public string Name { get; }            // tên phòng, hoặc username người kia
    public bool IsPrivate { get; }         // true = chat riêng 1-1

    // Khóa để lưu tin nhắn: thêm "@" cho chat riêng, tránh trùng với phòng cùng tên
    public string Key => IsPrivate ? "@" + Name : Name;

    public string Initials => AvatarHelper.Initials(Name);
    public string AvatarColor => AvatarHelper.ColorFor(Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(HeaderSubtitle))]
    private int memberCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(CanLeave))]
    private bool isJoined;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int unreadCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string lastMessage = "";

    [ObservableProperty] private string lastTime = "";

    // Chỉ dùng cho chat riêng
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(HeaderSubtitle))]
    private bool isOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(HeaderSubtitle))]
    private bool isTyping;

    public bool HasUnread => UnreadCount > 0;
    public bool CanLeave => IsJoined && !IsPrivate;   // chat riêng không có nút "Rời phòng"

    // Dòng chữ nhỏ dưới tên ở cột trái
    public string Subtitle
    {
        get
        {
            if (IsPrivate)
            {
                if (IsTyping) return "Đang nhập...";
                if (LastMessage.Length > 0) return LastMessage;
                return IsOnline ? "Đang hoạt động" : "Ngoại tuyến";
            }
            if (LastMessage.Length > 0) return LastMessage;
            return IsJoined ? $"{MemberCount} thành viên" : $"{MemberCount} thành viên · Bấm để vào";
        }
    }

    // Dòng chữ nhỏ dưới tên ở header khung chat
    public string HeaderSubtitle =>
        IsPrivate
            ? (IsTyping ? "Đang nhập..." : IsOnline ? "Đang hoạt động" : "Ngoại tuyến")
            : $"{MemberCount} người trong phòng";
}