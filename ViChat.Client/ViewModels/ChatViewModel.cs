using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViChat.Client.Models;
using ViChat.Shared;

namespace ViChat.Client.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    // Tin nhắn lưu theo khóa: tên phòng, hoặc "@username" cho chat riêng
    private readonly Dictionary<string, ObservableCollection<ChatMessage>> _messages =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _refreshTimer;   // hỏi lại danh sách phòng mỗi 5 giây
    private readonly DispatcherTimer _noticeTimer;    // tự ẩn thông báo lỗi sau 4 giây
    private readonly DispatcherTimer _typingTimer;    // 3 giây không gõ -> báo "ngừng nhập"

    // ===== Truyền file =====
    private readonly FileReceiver _receiver;          // nhận file (code của Thùy)
    // fileId -> bong bóng tương ứng. Concurrent vì luồng gửi file (Task.Run) cũng ghi vào đây.
    private readonly ConcurrentDictionary<string, ChatMessage> _fileMessages = new();

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    // ===== Lịch sử tin nhắn (CSDL) =====
    // Phòng / chat riêng đã tải lịch sử rồi thì không tải lại
    private readonly HashSet<string> _historyLoaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions HistoryJson = new(JsonSerializerDefaults.Web);

    // Một tin trong gói HISTORY / PRIVATE_HISTORY (khớp với ChatRepository.HistoryItem của server)
    private record HistoryDto(string MsgId, string From, string Content, string Time,
                              string Kind, string? FileName, long FileSize);

    // ===== Đang nhập (chat riêng) =====
    private bool _typingSent;        // đã báo "đang nhập" cho người kia chưa
    private string _typingTo = "";   // đang báo cho ai

    // Cột trái: chứa CẢ phòng chat lẫn chat riêng; RoomsView lọc theo tab đang chọn
    public ObservableCollection<RoomItem> Rooms { get; } = new();
    public ICollectionView RoomsView { get; }

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

    // ===== Tab cột trái: "Phòng" / "Mọi người" =====
    private bool _showPeople;
    public bool ShowPeople
    {
        get => _showPeople;
        set
        {
            if (!SetProperty(ref _showPeople, value)) return;
            OnPropertyChanged(nameof(ShowRooms));
            RoomsView.Refresh();

            // Đổi tab -> chọn mục đầu tiên của tab mới (nếu mục đang chọn không thuộc tab này)
            if (SelectedRoom == null || SelectedRoom.IsPrivate != value)
                SelectedRoom = Rooms.FirstOrDefault(r => r.IsPrivate == value && r.IsJoined);
        }
    }
    public bool ShowRooms
    {
        get => !_showPeople;
        set => ShowPeople = !value;
    }

    // Tổng tin riêng chưa đọc -> hiện chấm số trên tab "Mọi người"
    public int PrivateUnreadTotal => Rooms.Where(r => r.IsPrivate).Sum(r => r.UnreadCount);
    public bool HasPrivateUnread => PrivateUnreadTotal > 0;

    public event Action? MessagesChanged;   // báo cửa sổ cuộn xuống tin mới nhất
    public event Action? LogoutRequested;

    public ChatViewModel()
    {
        RoomsView = CollectionViewSource.GetDefaultView(Rooms);
        RoomsView.Filter = o => o is RoomItem r &&
            r.IsPrivate == ShowPeople &&                         // chỉ hiện mục của tab đang chọn
            (SearchText.Trim().Length == 0 ||
             r.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        App.Network.PacketReceived += OnPacket;

        // File nhận về lưu ở: Downloads\ViChat\<tên mình>
        string safeName = string.Concat(UserName.Split(Path.GetInvalidFileNameChars()));
        if (safeName.Length == 0) safeName = "User";
        string saveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ViChat", safeName);
        _receiver = new FileReceiver(saveDir);
        _receiver.Progress += OnReceiveProgress;
        _receiver.Completed += OnReceiveCompleted;

        // Tạo timer "đang nhập" TRƯỚC khi chọn phòng (OnSelectedRoomChanged có dùng tới nó)
        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _typingTimer.Tick += (_, _) => StopTyping();

        // Server tự cho client mới vào phòng General
        var general = GetOrAddRoom("General");
        general.IsJoined = true;
        SelectedRoom = general;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => _ = SafeSendAsync("ROOM_LIST");
        _refreshTimer.Start();

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _noticeTimer.Tick += (_, _) => { Notice = ""; _noticeTimer.Stop(); };

        // Đã đăng nhập xong: hỏi danh sách phòng, danh sách người online và lịch sử phòng General
        _ = SafeSendAsync("ROOM_LIST");
        _ = SafeSendAsync("ONLINE_LIST");
        RequestRoomHistory("General");
    }

    // Gọi khi đóng cửa sổ: hủy đăng ký sự kiện, dừng timer, xóa file nhận dở
    public void Cleanup()
    {
        App.Network.PacketReceived -= OnPacket;
        _receiver.Progress -= OnReceiveProgress;
        _receiver.Completed -= OnReceiveCompleted;
        _receiver.AbortAll();
        _refreshTimer.Stop();
        _noticeTimer.Stop();
        _typingTimer.Stop();
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
                ShowPeople = false;               // chuyển về tab "Phòng"
                SelectedRoom = joined;
                _ = SafeSendAsync("ROOM_LIST");   // cập nhật lại số người
                RequestRoomHistory(joined.Name);  // tải tin cũ của phòng
                break;

            case "LEAVE_ROOM":
                var left = GetOrAddRoom(room);
                left.IsJoined = false;
                left.UnreadCount = 0;
                left.LastMessage = "";
                left.LastTime = "";
                GetMessages(left.Key).Clear();
                _historyLoaded.Remove(left.Key);  // vào lại phòng thì tải lại lịch sử
                if (SelectedRoom == left)
                    SelectedRoom = Rooms.FirstOrDefault(r => !r.IsPrivate && r.IsJoined);
                _ = SafeSendAsync("ROOM_LIST");
                break;

            case "NEW_MSG":
                AddMessage(p);
                break;

            // ----- Lịch sử tin nhắn từ CSDL -----
            case "HISTORY":
                AddHistory(GetOrAddRoom(room), p.Data.GetValueOrDefault("messages", "[]"));
                break;

            case "PRIVATE_HISTORY":
                string with = p.Data.GetValueOrDefault("with", "");
                if (with.Length > 0)
                    AddHistory(GetOrAddUser(with), p.Data.GetValueOrDefault("messages", "[]"));
                break;

            // ----- Chat riêng, online, đang nhập (phần server của Đạt) -----
            case "ONLINE_LIST":
                UpdateOnlineList(p.Data.GetValueOrDefault("users", ""));
                break;

            case "USER_STATUS":
                string who = p.Data.GetValueOrDefault("username", "");
                if (who.Length > 0 && !IsMe(who))
                {
                    var u = GetOrAddUser(who);
                    u.IsOnline = p.Data.GetValueOrDefault("status") == "online";
                    if (!u.IsOnline) u.IsTyping = false;
                }
                break;

            case "PRIVATE_MSG":
                AddPrivateMessage(p);
                break;

            case "TYPING":
                string typer = p.Data.GetValueOrDefault("from", "");
                if (typer.Length > 0 && !IsMe(typer))
                    GetOrAddUser(typer).IsTyping = p.Data.GetValueOrDefault("isTyping") == "true";
                break;

            // ----- Nhận file -----
            case PacketTypes.FileStart:
                OnFileStart(p);
                break;
            case PacketTypes.FileChunk:
                _receiver.HandleChunk(p);   // tiến trình báo qua sự kiện Progress
                break;
            case PacketTypes.FileEnd:
                _receiver.HandleEnd(p);     // kiểm tra SHA-256, kết quả báo qua sự kiện Completed
                break;
            case PacketTypes.FileResult:
                OnFileResult(p);
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

    // "An,Binh,Chi" -> ai có trong danh sách là online, còn lại là offline
    private void UpdateOnlineList(string list)
    {
        var names = new HashSet<string>(
            list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        foreach (var r in Rooms.Where(r => r.IsPrivate).ToList())
            r.IsOnline = names.Contains(r.Name);

        foreach (string n in names)
            if (!IsMe(n)) GetOrAddUser(n).IsOnline = true;
    }

    private void AddMessage(Packet p)
    {
        string from = p.Data.GetValueOrDefault("from", "");
        string roomName = p.Data.GetValueOrDefault("room", "General");
        var msg = new ChatMessage
        {
            Room = roomName,
            MsgId = p.Data.GetValueOrDefault("msgId", ""),
            From = from,
            Content = p.Data.GetValueOrDefault("content", ""),
            Time = p.Data.GetValueOrDefault("time", ""),
            IsSystem = from == "Hệ thống",
            IsMine = IsMe(from)
        };
        AddToRoom(GetOrAddRoom(roomName), msg);
    }

    // PRIVATE_MSG: server gửi cho người nhận VÀ bản sao cho người gửi
    private void AddPrivateMessage(Packet p)
    {
        string from = p.Data.GetValueOrDefault("from", "");
        string to = p.Data.GetValueOrDefault("to", "");
        bool mine = IsMe(from);
        string peer = mine ? to : from;          // người kia trong cuộc trò chuyện
        if (peer.Length == 0) return;

        var chat = GetOrAddUser(peer);
        if (!mine) chat.IsTyping = false;        // đã gửi tin -> hết "đang nhập"

        var msg = new ChatMessage
        {
            Room = chat.Key,
            MsgId = p.Data.GetValueOrDefault("msgId", ""),
            From = from,
            Content = p.Data.GetValueOrDefault("content", ""),
            Time = p.Data.GetValueOrDefault("time", ""),
            IsMine = mine
        };
        AddToRoom(chat, msg);
    }

    // Thêm một bong bóng vào phòng/chat riêng + cập nhật dòng tin cuối, số chưa đọc
    private void AddToRoom(RoomItem room, ChatMessage msg)
    {
        GetMessages(room.Key).Add(msg);

        room.LastMessage = msg.IsSystem ? msg.Content
            : msg.IsMine ? $"Bạn: {msg.Content}"
            : room.IsPrivate ? msg.Content                 // chat riêng: không cần ghi tên người kia
            : $"{msg.From}: {msg.Content}";
        room.LastTime = msg.Time;

        if (room == SelectedRoom) MessagesChanged?.Invoke();  // đang xem -> cuộn xuống
        else room.UnreadCount++;                            // không xem -> tăng số chưa đọc

        OnPropertyChanged(nameof(PrivateUnreadTotal));
        OnPropertyChanged(nameof(HasPrivateUnread));
    }

    // ================== NHẬN FILE ==================
    private void OnFileStart(Packet p)
    {
        string fileId = p.Data.GetValueOrDefault("fileId", "");
        string name = p.Data.GetValueOrDefault("name", "file");
        string kind = p.Data.GetValueOrDefault("kind", FileKinds.File);
        long.TryParse(p.Data.GetValueOrDefault("size", "0"), out long size);
        string from = p.Data.GetValueOrDefault("from", "?");

        // Chat riêng: hiện trong cuộc trò chuyện với NGƯỜI GỬI. Phòng: hiện trong phòng "target".
        RoomItem chat = p.Data.GetValueOrDefault("targetType") == "user"
            ? GetOrAddUser(from)
            : GetOrAddRoom(p.Data.GetValueOrDefault("target", "General"));

        var msg = new ChatMessage
        {
            Room = chat.Key,
            From = from,
            Kind = kind == FileKinds.Image ? FileKinds.Image : FileKinds.File,
            FileId = fileId,
            FileName = name,
            FileSize = size,
            Content = kind == FileKinds.Image ? "🖼 Ảnh" : $"📎 {name}",
            Time = DateTime.Now.ToString("HH:mm"),
            IsTransferring = true,
            Status = "Đang nhận..."
        };

        // Ghi vào từ điển TRƯỚC khi gọi HandleStart, vì nếu lỗi thì Completed được gọi ngay
        _fileMessages[fileId] = msg;
        AddToRoom(chat, msg);
        _receiver.HandleStart(p);
    }

    private void OnReceiveProgress(FileProgressInfo i)
    {
        if (_fileMessages.TryGetValue(i.FileId, out var msg))
            msg.Progress = i.Percent;
    }

    private void OnReceiveCompleted(FileResultInfo r)
    {
        if (!_fileMessages.TryGetValue(r.FileId, out var msg) || msg.IsMine) return;

        msg.IsTransferring = false;
        if (r.Ok)
        {
            msg.Progress = 100;
            msg.Status = "";
            msg.SavedPath = r.SavedPath;   // là ảnh thì tự hiện ảnh; bấm vào để mở
        }
        else
        {
            msg.Status = "Lỗi: " + r.Message;
        }
    }

    private void OnFileResult(Packet p)
    {
        string fileId = p.Data.GetValueOrDefault("fileId", "");
        bool ok = p.Data.GetValueOrDefault("ok") == "true";
        string message = p.Data.GetValueOrDefault("message", "");

        if (!_fileMessages.TryGetValue(fileId, out var msg))
        {
            if (!ok) _receiver.Abort(fileId);
            return;
        }

        msg.IsTransferring = false;
        if (msg.IsMine)
        {
            msg.Progress = ok ? 100 : msg.Progress;
            msg.Status = ok ? "Đã gửi" : "Lỗi: " + message;
        }
        else if (!ok)
        {
            _receiver.Abort(fileId);
            msg.Status = "Đã bị hủy: " + message;
        }
    }

    // ================== GỬI FILE / ẢNH ==================
    [RelayCommand]
    private Task AttachFileAsync() =>
        PickAndSendAsync("Chọn file để gửi",
            "Tất cả file|*.*|Ảnh|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp");

    [RelayCommand]
    private Task AttachImageAsync() =>
        PickAndSendAsync("Chọn ảnh để gửi",
            "Ảnh|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp");

    private async Task PickAndSendAsync(string title, string filter)
    {
        if (SelectedRoom is not { IsJoined: true } room) return;


        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        if (dlg.ShowDialog() != true) return;

        await SendFileAsync(dlg.FileName, room);
    }

    private async Task SendFileAsync(string path, RoomItem room)
    {
        var info = new FileInfo(path);
        if (info.Length > ProtocolLimits.MaxFileBytes)
        {
            ShowNotice($"File lớn hơn {ProtocolLimits.MaxFileBytes / (1024 * 1024)} MB, không gửi được.");
            return;
        }

        bool isImage = ImageExtensions.Contains(info.Extension);

        // Server không gửi lại file cho người gửi, nên tự tạo bong bóng của mình ngay
        var msg = new ChatMessage
        {
            Room = room.Key,
            From = UserName,
            IsMine = true,
            Kind = isImage ? FileKinds.Image : FileKinds.File,
            FileName = info.Name,
            FileSize = info.Length,
            Content = isImage ? "🖼 Ảnh" : $"📎 {info.Name}",
            Time = DateTime.Now.ToString("HH:mm"),
            IsTransferring = true,
            Status = "Đang gửi..."
        };
        if (isImage) msg.SavedPath = path;   // xem trước ảnh ngay bằng file gốc
        AddToRoom(room, msg);

        var progress = new Progress<int>(pct => msg.Progress = pct);

        try
        {
            await Task.Run(() => FileSender.SendFileAsync(
                path, room.IsPrivate ? "user" : FileKinds.TargetRoom,   // chat riêng: target = username người nhận,
                room.Name,
                async pk =>
                {
                    if (pk.Type == PacketTypes.FileStart)
                    {
                        msg.FileId = pk.Data["fileId"];
                        _fileMessages[msg.FileId] = msg;
                    }
                    await App.Network.SendAsync(pk.Type, pk.Data);
                },
                progress));

            if (msg.Status == "Đang gửi...")
                msg.Status = "Đang chờ server kiểm tra...";
        }
        catch (Exception ex)
        {
            msg.IsTransferring = false;
            msg.Status = "Lỗi: " + ex.Message;
        }
    }

    // ================== EMOJI ==================
    public string[] Emojis { get; } =
    {
        "😀", "😂", "🤣", "😅", "😉", "😊", "😍", "🥰",
        "😘", "😎", "🤔", "😴", "😢", "😭", "😡", "😱",
        "🤗", "👍", "👎", "👏", "🙏", "💪", "👋", "🙌",
        "❤️", "🔥", "🎉", "💯", "✅", "❌", "⭐", "☕"
    };

    private bool _isEmojiOpen;
    public bool IsEmojiOpen
    {
        get => _isEmojiOpen;
        set => SetProperty(ref _isEmojiOpen, value);
    }

    [RelayCommand]
    private void OpenEmoji() => IsEmojiOpen = true;

    [RelayCommand]
    private void InsertEmoji(string emoji) => MessageText += emoji;

    // ================== ĐANG NHẬP (CHAT RIÊNG) ==================
    // Thư viện tự gọi khi MessageText đổi
    partial void OnMessageTextChanged(string value)
    {
        if (SelectedRoom is not { IsPrivate: true } chat) return;

        if (value.Length == 0)
        {
            StopTyping();
            return;
        }

        // Chỉ gửi "đang nhập" MỘT lần khi bắt đầu gõ, không gửi mỗi phím
        if (!_typingSent)
        {
            _typingSent = true;
            _typingTo = chat.Name;
            _ = SafeSendAsync("TYPING", new() { ["to"] = chat.Name, ["isTyping"] = "true" });
        }
        _typingTimer.Stop();   // gõ tiếp -> đếm lại 3 giây
        _typingTimer.Start();
    }

    private void StopTyping()
    {
        _typingTimer.Stop();
        if (!_typingSent) return;
        _typingSent = false;
        _ = SafeSendAsync("TYPING", new() { ["to"] = _typingTo, ["isTyping"] = "false" });
    }

    // ================== KHI CHỌN PHÒNG / TÌM KIẾM ==================
    partial void OnSelectedRoomChanged(RoomItem? value)
    {
        StopTyping();   // đổi cuộc trò chuyện -> báo ngừng nhập với người cũ

        if (value == null)
        {
            CurrentMessages = new();
            return;
        }
        CurrentMessages = GetMessages(value.Key);
        value.UnreadCount = 0;
        OnPropertyChanged(nameof(PrivateUnreadTotal));
        OnPropertyChanged(nameof(HasPrivateUnread));
        MessagesChanged?.Invoke();

        // Bấm vào phòng chưa tham gia -> tự vào phòng
        if (!value.IsPrivate && !value.IsJoined)
            _ = SafeSendAsync("JOIN_ROOM", new() { ["room"] = value.Name });

        // Mở cuộc trò chuyện riêng lần đầu -> tải tin cũ
        if (value.IsPrivate)
            RequestPrivateHistory(value.Name);
    }

    partial void OnSearchTextChanged(string value) => RoomsView.Refresh();

    // ================== CÁC NÚT BẤM ==================
    [RelayCommand]
    private async Task SendMessageAsync()
    {
        string text = MessageText.Trim();
        if (text.Length == 0 || SelectedRoom is not { IsJoined: true } room) return;

        MessageText = "";   // cũng tự báo "ngừng nhập" (OnMessageTextChanged)

        // Không tự thêm tin vào khung chat: server gửi lại cho cả người gửi
        if (room.IsPrivate)
            await SafeSendAsync("PRIVATE_MSG", new() { ["to"] = room.Name, ["content"] = text });
        else
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
        if (SelectedRoom is not { CanLeave: true } room) return;
        await SafeSendAsync("LEAVE_ROOM", new() { ["room"] = room.Name });
    }

    [RelayCommand]
    private void Logout() => LogoutRequested?.Invoke();

    // ================== LỊCH SỬ TIN NHẮN ==================
    private void RequestRoomHistory(string roomName)
    {
        var room = GetOrAddRoom(roomName);
        if (!_historyLoaded.Add(room.Key)) return;   // đã tải rồi
        _ = SafeSendAsync("GET_HISTORY", new() { ["room"] = room.Name });
    }

    private void RequestPrivateHistory(string username)
    {
        var chat = GetOrAddUser(username);
        if (!_historyLoaded.Add(chat.Key)) return;
        _ = SafeSendAsync("GET_PRIVATE_HISTORY", new() { ["with"] = chat.Name });
    }

    // Chèn tin cũ lên ĐẦU khung chat (trước các tin mới vừa nhận), bỏ qua tin đã có
    private void AddHistory(RoomItem room, string json)
    {
        List<HistoryDto>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<HistoryDto>>(json, HistoryJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (items == null || items.Count == 0) return;

        var list = GetMessages(room.Key);
        var existing = new HashSet<string>(list.Select(m => m.MsgId).Where(id => id.Length > 0));

        int index = 0;
        foreach (var h in items)
        {
            if (existing.Contains(h.MsgId)) continue;   // tin này đã nhận trực tiếp rồi
            list.Insert(index++, ToChatMessage(room, h));
        }

        // Phòng chưa có "tin cuối" -> lấy tin cuối trong lịch sử
        if (room.LastMessage.Length == 0)
        {
            var last = items[^1];
            room.LastMessage = IsMe(last.From) ? $"Bạn: {last.Content}"
                : room.IsPrivate ? last.Content
                : $"{last.From}: {last.Content}";
            room.LastTime = last.Time;
        }

        if (room == SelectedRoom) MessagesChanged?.Invoke();
    }

    private ChatMessage ToChatMessage(RoomItem room, HistoryDto h)
    {
        bool isFile = h.Kind == FileKinds.File || h.Kind == FileKinds.Image;
        return new ChatMessage
        {
            Room = room.Key,
            MsgId = h.MsgId,
            From = h.From,
            Content = h.Content,
            Time = h.Time,
            IsMine = IsMe(h.From),
            Kind = isFile ? h.Kind : "text",
            FileName = h.FileName ?? "",
            FileSize = h.FileSize,
            // File cũ nằm trên server, máy này chưa có bản sao để mở
            Status = isFile ? "File cũ, đang lưu trên server" : ""
        };
    }

    // ================== HÀM PHỤ ==================
    private bool IsMe(string name) => name.Equals(UserName, StringComparison.OrdinalIgnoreCase);

    // Tìm hoặc tạo PHÒNG theo tên
    private RoomItem GetOrAddRoom(string name)
    {
        var room = Rooms.FirstOrDefault(r => !r.IsPrivate &&
            r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (room == null)
        {
            room = new RoomItem(name);
            Rooms.Add(room);
        }
        return room;
    }

    // Tìm hoặc tạo CUỘC TRÒ CHUYỆN RIÊNG với một người
    private RoomItem GetOrAddUser(string username)
    {
        var chat = Rooms.FirstOrDefault(r => r.IsPrivate &&
            r.Name.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (chat == null)
        {
            chat = new RoomItem(username, isPrivate: true);
            Rooms.Add(chat);
        }
        return chat;
    }

    private ObservableCollection<ChatMessage> GetMessages(string key)
    {
        if (!_messages.TryGetValue(key, out var list))
        {
            list = new ObservableCollection<ChatMessage>();
            _messages[key] = list;
        }
        return list;
    }

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