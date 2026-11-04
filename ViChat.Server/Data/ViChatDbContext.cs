using Microsoft.EntityFrameworkCore;

namespace ViChat.Server.Data;

// DbContext = "cầu nối" giữa code C# và CSDL.
// Mỗi lần cần đọc/ghi: tạo một cái mới, dùng xong bỏ đi.
// KHÔNG dùng chung một cái cho nhiều client cùng lúc.
public class ViChatDbContext : DbContext
{
    // ================= 1. CÁC BẢNG =================
    // Mỗi dòng = một bảng trong CSDL
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<RoomMemberEntity> RoomMembers => Set<RoomMemberEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();

    // ================= 2. KẾT NỐI =================
    // Chuỗi kết nối tới LocalDB: giống nhau trên mọi máy, không có mật khẩu
    private const string LocalDb =
        @"Server=(localdb)\MSSQLLocalDB;Database=ViChat;Trusted_Connection=True;TrustServerCertificate=True";

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // Ai dùng SQL Server khác thì đặt biến môi trường VICHAT_DB, không cần sửa code
        string connectionString = Environment.GetEnvironmentVariable("VICHAT_DB") ?? LocalDb;
        options.UseSqlServer(connectionString);
    }

    // ================= 3. QUY TẮC CHO TỪNG BẢNG =================
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUsers(modelBuilder);
        ConfigureRooms(modelBuilder);
        ConfigureRoomMembers(modelBuilder);
        ConfigureMessages(modelBuilder);
        TurnOffCascadeDelete(modelBuilder);
    }

    // Bảng Users
    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<UserEntity>();
        user.ToTable("Users");

        // Giới hạn độ dài các cột chữ
        user.Property(u => u.Username).HasMaxLength(50);
        user.Property(u => u.PasswordHash).HasMaxLength(100);
        user.Property(u => u.DisplayName).HasMaxLength(100);

        // Không cho hai tài khoản trùng tên đăng nhập
        user.HasIndex(u => u.Username).IsUnique();
    }

    // Bảng Rooms
    private static void ConfigureRooms(ModelBuilder modelBuilder)
    {
        var room = modelBuilder.Entity<RoomEntity>();
        room.ToTable("Rooms");

        room.Property(r => r.Name).HasMaxLength(50);

        // Không cho hai phòng trùng tên
        room.HasIndex(r => r.Name).IsUnique();

        // Cột CreatedById cho biết ai đã tạo phòng
        room.HasOne(r => r.Creator).WithMany().HasForeignKey(r => r.CreatedById);
    }

    // Bảng RoomMembers
    private static void ConfigureRoomMembers(ModelBuilder modelBuilder)
    {
        var member = modelBuilder.Entity<RoomMemberEntity>();
        member.ToTable("RoomMembers");

        // Khóa chính gồm 2 cột: mỗi người chỉ có 1 dòng trong mỗi phòng
        member.HasKey(m => new { m.RoomId, m.UserId });
    }

    // Bảng Messages
    private static void ConfigureMessages(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<MessageEntity>();
        message.ToTable("Messages");

        message.Property(m => m.MessageType).HasMaxLength(20);
        message.Property(m => m.FileName).HasMaxLength(260);
        message.Property(m => m.FileSha256).HasMaxLength(64);

        // Tin nhắn có 2 cột cùng trỏ tới bảng Users: người gửi và người nhận (tin 1-1)
        message.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId);
        message.HasOne(m => m.Receiver).WithMany().HasForeignKey(m => m.ReceiverId);

        // Giúp tải lịch sử của một phòng nhanh hơn
        message.HasIndex(m => new { m.RoomId, m.SentAt });
    }

    // Tắt "xóa dây chuyền" cho mọi khóa ngoại.
    // Mặc định, xóa một người dùng sẽ tự xóa luôn dữ liệu liên quan;
    // SQL Server báo lỗi khi có nhiều đường xóa dây chuyền như vậy (Messages trỏ tới Users 2 lần).
    // ViChat không xóa dữ liệu nên tắt đi cho đơn giản và an toàn.
    private static void TurnOffCascadeDelete(ModelBuilder modelBuilder)
    {
        var allForeignKeys = modelBuilder.Model.GetEntityTypes()
                                               .SelectMany(table => table.GetForeignKeys());

        foreach (var foreignKey in allForeignKeys)
            foreignKey.DeleteBehavior = DeleteBehavior.NoAction;
    }
}