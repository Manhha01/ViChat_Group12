using System.IO;
using System.Net.Sockets;
using System.Windows;
using ViChat.Shared;

namespace ViChat.Client.Services;

public class NetworkClient
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    // Khóa ghi: tránh hai thao tác gửi chen vào nhau
    // (ví dụ vừa bấm gửi tin vừa tự động gửi trạng thái "đang nhập")
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string UserName { get; private set; } = "";
    public bool IsConnected => _stream != null;

    // Giao diện "đăng ký nghe" các sự kiện này.
    // Cả hai luôn được gọi trên LUỒNG GIAO DIỆN, nên bên nhận cập nhật UI thoải mái.
    public event Action<Packet>? PacketReceived;
    public event Action<string>? Disconnected;

    // Kết nối tới server, quá thời gian chờ thì báo lỗi (không treo giao diện)
    public async Task ConnectAsync(string host, int port, string userName, int timeoutMs = 5000)
    {
        Disconnect(); // nếu đang có kết nối cũ thì đóng trước

        var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException($"Hết {timeoutMs / 1000} giây vẫn không kết nối được tới {host}:{port}.");
        }
        catch
        {
            tcp.Dispose();
            throw; // SocketException: server chưa chạy, sai IP, bị tường lửa chặn...
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
        UserName = userName;

        // Chạy vòng nhận gói ở luồng nền, KHÔNG await, để hàm này trả về ngay
        NetworkStream stream = _stream;
        _ = Task.Run(() => ReceiveLoopAsync(stream));
    }

    // Gửi một gói. Tạm thời luôn kèm "from" để server biết tên (đến khi có LOGIN thật).
    public async Task SendAsync(string type, Dictionary<string, string>? data = null)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("Chưa kết nối tới server.");

        data ??= new Dictionary<string, string>();
        data["from"] = UserName;

        await _sendLock.WaitAsync();
        try
        {
            await PacketIO.WriteAsync(stream, new Packet { Type = type, Data = data });
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Chủ động ngắt kết nối (đăng xuất, đóng ứng dụng)
    public void Disconnect()
    {
        _stream = null;   // đặt null TRƯỚC để vòng nhận biết đây là ngắt chủ động
        _tcp?.Close();
        _tcp = null;
    }

    // Vòng nhận gói: chạy suốt thời gian kết nối, ở luồng nền
    private async Task ReceiveLoopAsync(NetworkStream stream)
    {
        string reason = "Server đã đóng kết nối.";
        try
        {
            while (true)
            {
                Packet? packet = await PacketIO.ReadAsync(stream);
                if (packet == null) break;

                // Đang ở luồng nền -> chuyển về luồng giao diện rồi mới báo
                RunOnUi(() => PacketReceived?.Invoke(packet));
            }
        }
        catch (Exception ex)
        {
            reason = "Mất kết nối: " + ex.Message;
        }

        // Chỉ báo "mất kết nối" nếu đây vẫn là kết nối hiện tại.
        // Nếu ta tự gọi Disconnect() thì _stream đã là null -> không báo.
        if (ReferenceEquals(stream, _stream))
        {
            Disconnect();
            RunOnUi(() => Disconnected?.Invoke(reason));
        }
    }

    // WPF chỉ cho luồng giao diện sửa giao diện. Dispatcher là "người đưa thư" sang luồng đó.
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public string DisplayName { get; private set; } = "";

    // Gọi sau khi server xác nhận đăng nhập: dùng đúng tên server trả về
    public void SetLoggedInUser(string username, string displayName)
    {
        UserName = username;
        DisplayName = displayName;
    }

    // Chờ server trả về một loại gói (hoặc ERROR), quá thời gian thì báo lỗi.
    // Dùng cho kiểu "gửi yêu cầu - chờ trả lời" như LOGIN -> LOGIN_RESULT.
    public Task<Packet> WaitForPacketAsync(string type, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<Packet>();

        void Handler(Packet p)
        {
            if (p.Type != type && p.Type != "ERROR") return;
            PacketReceived -= Handler;
            tcs.TrySetResult(p);
        }

        PacketReceived += Handler;

        _ = Task.Delay(timeoutMs).ContinueWith(_ =>
        {
            PacketReceived -= Handler;
            tcs.TrySetException(new TimeoutException("Server không phản hồi."));
        });

        return tcs.Task;
    }
}