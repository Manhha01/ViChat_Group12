/////////// TỰ TẠO CSDL KHI ĐANG CHẠY

using Microsoft.EntityFrameworkCore;

namespace ViChat.Server.Data;

public static class DbInitializer
{
    public static void Initialize()
    {
        using var db = new ViChatDbContext();

        // Chưa có CSDL thì tạo; thiếu bảng (migration mới) thì bổ sung. Chạy lại nhiều lần không sao.
        db.Database.Migrate();

        // Phòng mặc định
        if (!db.Rooms.Any(r => r.Name == "General"))
        {
            db.Rooms.Add(new RoomEntity { Name = "General" });
            db.SaveChanges();
        }

        Console.WriteLine($"CSDL sẵn sàng: {db.Users.Count()} tài khoản, " +
                          $"{db.Rooms.Count()} phòng, {db.Messages.Count()} tin nhắn.");
    }
}