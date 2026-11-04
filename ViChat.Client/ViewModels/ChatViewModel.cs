using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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

    // ===== MỚI: truyền file =====
    private readonly FileReceiver _receiver;          // nhận file (code của Thùy)
    // fileId -> bong bóng tương ứng. Concurrent vì luồng gửi file (Task.Run) cũng ghi vào đây.
    private readonly ConcurrentDictionary<string, ChatMessage> _fileMessages = new();

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

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

        // File nhận về lưu ở: Downloads\ViChat\<tên mình>
        // (mỗi tên một thư mục để chạy 2-3 client trên cùng máy không ghi đè lên nhau)
        string safeName = string.Concat(UserName.Split(Path.GetInvalidFileNameChars()));
        if (safeName.Length == 0) safeName = "User";
        string saveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ViChat", safeName);
        _receiver = new FileReceiver(saveDir);
        _receiver.Progress += OnReceiveProgress;
        _receiver.Completed += OnReceiveCompleted;

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

    // Gọi khi đóng cửa sổ: hủy đăng ký sự kiện, dừng timer, xóa file nhận dở
    public void Cleanup()
    {
        App.Network.PacketReceived -= OnPacket;
        _receiver.Progress -= OnReceiveProgress;
        _receiver.Completed -= OnReceiveCompleted;
        _receiver.AbortAll();
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

            // ----- MỚI: nhận file -----
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

    private void AddMessage(Packet p)
    {
        string from = p.Data.GetValueOrDefault("from", "");
        var msg = new ChatMessage
        {
            Room = p.Data.GetValueOrDefault("room", "General"),
            From = from,
            Content = p.Data.GetValueOrDefault("content", ""),
            Time = p.Data.GetValueOrDefault("time", ""),
            IsSystem = from == "Hệ thống",
            IsMine = from == UserName
        };
        AddToRoom(msg);
    }

    // Thêm một bong bóng vào phòng + cập nhật dòng tin cuối, số chưa đọc (dùng chung cho chữ và file)
    private void AddToRoom(ChatMessage msg)
    {
        GetMessages(msg.Room).Add(msg);

        var room = GetOrAddRoom(msg.Room);
        room.LastMessage = msg.IsSystem ? msg.Content : $"{(msg.IsMine ? "Bạn" : msg.From)}: {msg.Content}";
        room.LastTime = msg.Time;

        if (room == SelectedRoom) MessagesChanged?.Invoke();  // đang xem -> cuộn xuống
        else room.UnreadCount++;                            // không xem -> tăng số chưa đọc
    }

    // ================== NHẬN FILE ==================
    // FILE_START: tạo bong bóng "đang nhận" rồi giao cho FileReceiver mở file tạm
    private void OnFileStart(Packet p)
    {
        string fileId = p.Data.GetValueOrDefault("fileId", "");
        string name = p.Data.GetValueOrDefault("name", "file");
        string kind = p.Data.GetValueOrDefault("kind", FileKinds.File);
        long.TryParse(p.Data.GetValueOrDefault("size", "0"), out long size);
        // target = tên phòng. Bản tạm: server gửi file cho MỌI client, nên phòng có thể mình chưa vào.
        string roomName = p.Data.GetValueOrDefault("target", "General");

        var msg = new ChatMessage
        {
            Room = roomName,
            From = p.Data.GetValueOrDefault("from", "?"),
            Kind = kind == FileKinds.Image ? FileKinds.Image : FileKinds.File,
            FileId = fileId,
            FileName = name,
            FileSize = size,
            Content = kind == FileKinds.Image ? "🖼 Ảnh" : $"📎 {name}",
            Time = DateTime.Now.ToString("HH:mm"),
            IsTransferring = true,
            Status = "Đang nhận..."
        };

        // Ghi vào từ điển TRƯỚC khi gọi HandleStart, vì nếu lỗi thì Completed được gọi ngay lập tức
        _fileMessages[fileId] = msg;
        AddToRoom(msg);
        _receiver.HandleStart(p);
    }

    // FileReceiver báo % (chạy trên luồng giao diện vì OnPacket đã ở luồng giao diện)
    private void OnReceiveProgress(FileProgressInfo i)
    {
        if (_fileMessages.TryGetValue(i.FileId, out var msg))
            msg.Progress = i.Percent;
    }

    // FileReceiver báo nhận xong (đúng SHA-256) hoặc lỗi
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

    // FILE_RESULT: với người gửi = server đã nhận xong/lỗi; với người nhận ok=false = file bị hủy
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
            _receiver.Abort(fileId);       // xóa file tạm đang nhận dở
            msg.Status = "Đã bị hủy: " + message;
        }
    }

    // ================== GỬI FILE / ẢNH ==================
    // Nút 📎: gửi mọi loại file
    [RelayCommand]
    private Task AttachFileAsync() =>
        PickAndSendAsync("Chọn file để gửi",
            "Tất cả file|*.*|Ảnh|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp");

    // Nút 🖼: chỉ cho chọn ảnh
    [RelayCommand]
    private Task AttachImageAsync() =>
        PickAndSendAsync("Chọn ảnh để gửi",
            "Ảnh|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp");

    // Hàm dùng chung: mở hộp chọn file rồi gửi vào phòng đang mở
    private async Task PickAndSendAsync(string title, string filter)
    {
        if (SelectedRoom is not { IsJoined: true } room) return;

        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        if (dlg.ShowDialog() != true) return;

        await SendFileAsync(dlg.FileName, room.Name);
    }

    // ================== EMOJI ==================
    // Danh sách emoji hiển thị trong bảng chọn (thêm/bớt tùy ý)
    public string[] Emojis { get; } =
    {
        "😀", "😂", "🤣", "😅", "😉", "😊", "😍", "🥰",
        "😘", "😎", "🤔", "😴", "😢", "😭", "😡", "😱",
        "🤗", "👍", "👎", "👏", "🙏", "💪", "👋", "🙌",
        "❤️", "🔥", "🎉", "💯", "✅", "❌", "⭐", "☕"
    };

    // Bảng emoji đang mở? (viết tay, SetProperty tự báo cho giao diện cập nhật)
    private bool _isEmojiOpen;
    public bool IsEmojiOpen
    {
        get => _isEmojiOpen;
        set => SetProperty(ref _isEmojiOpen, value);
    } // bảng emoji đang mở?

    [RelayCommand]
    private void OpenEmoji() => IsEmojiOpen = true;

    // Bấm một emoji -> thêm vào cuối ô nhập tin (bảng vẫn mở để chọn tiếp)
    [RelayCommand]
    private void InsertEmoji(string emoji) => MessageText += emoji;

    private async Task SendFileAsync(string path, string roomName)
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
            Room = roomName,
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
        if (isImage) msg.SavedPath = path;   // xem trước ảnh ngay bằng file gốc trên máy
        AddToRoom(msg);

        // Progress tạo trên luồng giao diện -> hàm báo % tự chạy về luồng giao diện
        var progress = new Progress<int>(pct => msg.Progress = pct);

        try
        {
            // Task.Run: đọc file, tính SHA-256, mã hóa Base64 chạy nền -> giao diện không bị đơ
            await Task.Run(() => FileSender.SendFileAsync(
                path, FileKinds.TargetRoom, roomName,
                async pk =>
                {
                    // Lấy fileId từ gói FILE_START để sau này khớp với FILE_RESULT
                    if (pk.Type == PacketTypes.FileStart)
                    {
                        msg.FileId = pk.Data["fileId"];
                        _fileMessages[msg.FileId] = msg;
                    }
                    await App.Network.SendAsync(pk.Type, pk.Data);   // có SemaphoreSlim bên trong
                },
                progress));

            // FILE_RESULT có thể về trước dòng này, nên chỉ đổi khi vẫn đang "Đang gửi..."
            if (msg.Status == "Đang gửi...")
                msg.Status = "Đang chờ server kiểm tra...";
        }
        catch (Exception ex)
        {
            msg.IsTransferring = false;
            msg.Status = "Lỗi: " + ex.Message;
        }
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