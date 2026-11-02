using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViChat.Client.Models;
using ViChat.Shared;

namespace ViChat.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    // Tin nhắn lưu riêng theo từng phòng (tên phòng không phân biệt hoa thường, giống server)
    private readonly Dictionary<string, ObservableCollection<ChatMessage>> _messages =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _refreshTimer;   // hỏi lại danh sách phòng mỗi 5 giây
    private readonly DispatcherTimer _noticeTimer;    // tự ẩn thông báo lỗi sau 4 giây

    // ObservableCollection: thêm/xóa phần tử là giao diện tự cập nhật
    public ObservableCollection<RoomItem> Rooms { get; } = new();
    public ICollectionView RoomsView { get; }          // Rooms có lọc theo ô tìm kiếm

    public string UserName => App.Network.UserName;
    public string UserInitials => AvatarHelper.Initials(UserName);
    public string UserColor => AvatarHelper.ColorFor(UserName);

    [ObservableProperty] private RoomItem? selectedRoom;
    [ObservableProperty] private ObservableCollection<ChatMessage> currentMessages = new();
    [ObservableProperty] private string messageText = "";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isCreatingRoom;
    [ObservableProperty] private string newRoomName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string notice = "";
    public bool HasNotice => Notice.Length > 0;

    public event Action? MessagesChanged;   // báo cửa sổ cuộn xuống tin mới nhất
    public event Action? LogoutRequested;

    public ChatViewModel()
    {
        RoomsView = CollectionViewSource.GetDefaultView(Rooms);
        RoomsView.Filter = o => o is RoomItem r &&
            (SearchText.Trim().Length == 0 ||
             r.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        App.Network.PacketReceived += OnPacket;

        // Server tự cho client mới vào phòng General
        var general = GetOrAddRoom("General");
        general.IsJoined = true;
        SelectedRoom = general;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => _ = SafeSendAsync("ROOM_LIST");
        _refreshTimer.Start();

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) => { Notice = ""; _noticeTimer.Stop(); };

        // Gói đầu tiên: lấy danh sách phòng, đồng thời để server biết tên mình (qua "from")
        _ = SafeSendAsync("ROOM_LIST");
    }

    // Gọi khi đóng cửa sổ: hủy đăng ký sự kiện, dừng timer
    public void Cleanup()
    {
        App.Network.PacketReceived -= OnPacket;
        _refreshTimer.Stop();
        _noticeTimer.Stop();
    }

    // ================== NHẬN GÓI TỪ SERVER ==================
    // NetworkClient đã chuyển về luồng giao diện nên ở đây sửa dữ liệu thoải mái
    private void OnPacket(Packet p)
    {
        string room = p.Data.GetValueOrDefault("room", "");

        switch (p.Type)
        {
            case "ROOM_LIST":
                UpdateRoomList(p.Data.GetValueOrDefault("rooms", ""));
                break;

            case "CREATE_ROOM":
            case "JOIN_ROOM":
                var joined = GetOrAddRoom(room);
                joined.IsJoined = true;
                SelectedRoom = joined;
                _ = SafeSendAsync("ROOM_LIST");   // cập nhật lại số người
                break;

            case "LEAVE_ROOM":
                var left = GetOrAddRoom(room);
                left.IsJoined = false;
                left.UnreadCount = 0;
                left.LastMessage = "";
                left.LastTime = "";
                GetMessages(room).Clear();
                if (SelectedRoom == left)
                    SelectedRoom = Rooms.FirstOrDefault(r => r.IsJoined);
                _ = SafeSendAsync("ROOM_LIST");
                break;

            case "NEW_MSG":
                AddMessage(p);
                break;

            case "ERROR":
                ShowNotice(p.Data.GetValueOrDefault("message", "Có lỗi xảy ra"));
                break;
        }
    }

    // "General:3,Nhom12:2" -> cập nhật từng phòng
    private void UpdateRoomList(string list)
    {
        foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.LastIndexOf(':');
            if (colon <= 0) continue;
            string name = part[..colon];
            int.TryParse(part[(colon + 1)..], out int count);
            GetOrAddRoom(name).MemberCount = count;
        }
    }

    private void AddMessage(Packet p)
    {
        string roomName = p.Data.GetValueOrDefault("room", "General");
        string from = p.Data.GetValueOrDefault("from", "");

        var msg = new ChatMessage
        {
            Room = roomName,
            From = from,
            Content = p.Data.GetValueOrDefault("content", ""),
            Time = p.Data.GetValueOrDefault("time", ""),
            IsSystem = from == "Hệ thống",
            IsMine = from == UserName
        };
        GetMessages(roomName).Add(msg);

        var room = GetOrAddRoom(roomName);
        room.LastMessage = msg.IsSystem ? msg.Content : $"{(msg.IsMine ? "Bạn" : msg.From)}: {msg.Content}";
        room.LastTime = msg.Time;

        if (room == SelectedRoom) MessagesChanged?.Invoke();  // đang xem -> cuộn xuống
        else room.UnreadCount++;                            // không xem -> tăng số chưa đọc
    }

    // ================== KHI CHỌN PHÒNG / TÌM KIẾM ==================
    // Thư viện tự gọi hàm này mỗi khi SelectedRoom đổi
    partial void OnSelectedRoomChanged(RoomItem? value)
    {
        if (value == null)
        {
            CurrentMessages = new();
            return;
        }
        CurrentMessages = GetMessages(value.Name);
        value.UnreadCount = 0;
        MessagesChanged?.Invoke();

        // Bấm vào phòng chưa tham gia -> tự vào phòng
        if (!value.IsJoined)
            _ = SafeSendAsync("JOIN_ROOM", new() { ["room"] = value.Name });
    }

    partial void OnSearchTextChanged(string value) => RoomsView.Refresh();

    // ================== CÁC NÚT BẤM ==================
    [RelayCommand]
    private async Task SendMessageAsync()
    {
        string text = MessageText.Trim();
        if (text.Length == 0 || SelectedRoom is not { IsJoined: true } room) return;

        MessageText = "";
        // Không tự thêm tin vào khung chat: server sẽ gửi NEW_MSG về cho cả người gửi
        await SafeSendAsync("SEND_MSG", new() { ["room"] = room.Name, ["content"] = text });
    }

    [RelayCommand]
    private void ToggleCreateRoom()
    {
        IsCreatingRoom = !IsCreatingRoom;
        NewRoomName = "";
    }

    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        string name = NewRoomName.Trim();
        if (name.Length == 0)
        {
            ShowNotice("Nhập tên phòng.");
            return;
        }
        if (name.Contains(',') || name.Contains(':'))
        {
            ShowNotice("Tên phòng không được chứa dấu , hoặc :");
            return;
        }
        IsCreatingRoom = false;
        NewRoomName = "";
        await SafeSendAsync("CREATE_ROOM", new() { ["room"] = name });
    }

    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        if (SelectedRoom is not { IsJoined: true } room) return;
        await SafeSendAsync("LEAVE_ROOM", new() { ["room"] = room.Name });
    }

    [RelayCommand]
    private void Logout() => LogoutRequested?.Invoke();

    // ================== HÀM PHỤ ==================
    private RoomItem GetOrAddRoom(string name)
    {
        var room = Rooms.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (room == null)
        {
            room = new RoomItem(name);
            Rooms.Add(room);
        }
        return room;
    }

    private ObservableCollection<ChatMessage> GetMessages(string room)
    {
        if (!_messages.TryGetValue(room, out var list))
        {
            list = new ObservableCollection<ChatMessage>();
            _messages[room] = list;
        }
        return list;
    }

    // Gửi gói, lỗi thì hiện thông báo chứ không làm sập ứng dụng
    private async Task SafeSendAsync(string type, Dictionary<string, string>? data = null)
    {
        try
        {
            await App.Network.SendAsync(type, data);
        }
        catch (Exception ex)
        {
            ShowNotice("Không gửi được: " + ex.Message);
        }
    }

    private void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }
}