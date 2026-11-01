using System.Net.Sockets;
using ViChat.Shared;
using System.Collections.Concurrent;   // thêm ở đầu file
public class ClientSession
{
    // Khóa để đảm bảo mỗi lúc chỉ 1 luồng được ghi vào stream của client này
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();
    public TcpClient Tcp { get; }
    public NetworkStream Stream { get; }

    // ===== THÊM MỚI =====
    // Tên người dùng. Tạm lấy từ TestClient; sau này thành viên 3 gán khi LOGIN thành công.
    public string Username { get; set; } = "";

    // Các phòng client này đang tham gia (key = tên phòng, value không dùng).
    public ConcurrentDictionary<string, byte> Rooms { get; } = new();
    // =====================
    public ClientSession(TcpClient tcp)
    {
        Tcp = tcp;
        Stream = tcp.GetStream();
    }

    public async Task SendAsync(Packet packet)
    {
        await _sendLock.WaitAsync();       // xếp hàng chờ đến lượt
        try
        {
            await PacketIO.WriteAsync(Stream, packet);
        }
        finally
        {
            _sendLock.Release();           // xong thì nhả khóa cho luồng khác
        }
    }
}