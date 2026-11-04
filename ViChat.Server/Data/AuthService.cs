using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace ViChat.Server.Data;

// Mọi việc liên quan tới tài khoản nằm ở đây, Program.cs chỉ việc gọi
public static class AuthService
{
    // 3-30 ký tự: chữ thường không dấu, số, dấu chấm, gạch dưới
    private static readonly Regex UsernameRule = new("^[a-z0-9._]{3,30}$");

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    // Trả về (thành công hay không, thông báo)
    public static async Task<(bool Ok, string Message)> RegisterAsync(
        string username, string password, string displayName)
    {
        username = Normalize(username);
        displayName = displayName.Trim();
        if (displayName.Length == 0) displayName = username;

        if (!UsernameRule.IsMatch(username))
            return (false, "Tên đăng nhập 3-30 ký tự, chỉ gồm chữ thường không dấu, số, dấu . và _");
        if (password.Length < 6)
            return (false, "Mật khẩu tối thiểu 6 ký tự");

        await using var db = new ViChatDbContext();

        if (await db.Users.AnyAsync(u => u.Username == username))
            return (false, "Tên đăng nhập đã tồn tại");

        db.Users.Add(new UserEntity
        {
            Username = username,
            // Băm mật khẩu: CSDL chỉ lưu chuỗi băm, không ai đọc lại được mật khẩu gốc
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            DisplayName = displayName
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Hai người đăng ký cùng tên đúng một lúc: chỉ mục UNIQUE trong CSDL chặn người thứ hai
            return (false, "Tên đăng nhập đã tồn tại");
        }

        return (true, "Đăng ký thành công");
    }

    // Đúng tên và mật khẩu -> trả về user; sai -> null
    public static async Task<UserEntity?> CheckLoginAsync(string username, string password)
    {
        username = Normalize(username);

        await using var db = new ViChatDbContext();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user == null) return null;

        // So mật khẩu nhập vào với chuỗi băm đã lưu
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
    }
}