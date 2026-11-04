using System.Collections.Concurrent;
using System.Text;
using ViChat.Shared;

namespace ViChat.Server
{
    public static class PrivateChat
    {
        // Danh sách người đang online: key = username (không phân biệt hoa/thường)
        public static readonly ConcurrentDictionary<string, ClientSession> OnlineUsers =
            new(StringComparer.OrdinalIgnoreCase);

        // Hàm tiện ích tạo packet nhanh
        public static Packet MakePacket(string type, params (string key, string value)[] fields)
        {
            var p = new Packet { Type = type, Data = new Dictionary<string, string>() };
            foreach (var (k, v) in fields) p.Data[k] = v;
            return p;
        }

        // Gửi packet an toàn (có try-catch đề phòng client ngắt kết nối đột ngột)
        public static async Task<bool> TrySendAsync(ClientSession s, Packet p)
        {
            try { await s.SendAsync(p); return true; }
            catch (Exception) { return false; }
        }

        // Gửi thông báo lỗi về client
        public static Task SendErrorAsync(ClientSession s, string code, string message) =>
            TrySendAsync(s, MakePacket("ERROR", ("code", code), ("message", message)));

        // Phát sóng (broadcast) cho tất cả user online trừ một người ngoại lệ
        public static async Task BroadcastAsync(Packet p, ClientSession? except = null)
        {
            var tasks = OnlineUsers.Values
                .Where(s => s != except)
                .Select(s => TrySendAsync(s, p))
                .ToList();
            await Task.WhenAll(tasks);
        }

        // --- 1. XỬ LÝ ONLINE / OFFLINE & ONLINE_LIST, USER_STATUS ---

        public static async Task<bool> TryGoOnlineAsync(ClientSession session)
        {
            var name = session.Username!;
            if (!OnlineUsers.TryAdd(name, session)) return false;

            await TrySendAsync(session, MakePacket("ONLINE_LIST",
                ("users", string.Join(",", OnlineUsers.Keys))));

            await BroadcastAsync(MakePacket("USER_STATUS",
                ("username", name), ("status", "online")), except: session);
            return true;
        }

        public static async Task GoOfflineAsync(ClientSession session)
        {
            if (session.Username == null) return;

            if (OnlineUsers.TryRemove(new KeyValuePair<string, ClientSession>(session.Username, session)))
            {
                await BroadcastAsync(MakePacket("USER_STATUS",
                    ("username", session.Username), ("status", "offline")), except: session);
            }
        }

        public static Task HandleOnlineListAsync(ClientSession session) =>
            TrySendAsync(session, MakePacket("ONLINE_LIST",
                ("users", string.Join(",", OnlineUsers.Keys))));


        // --- 2. XỬ LÝ NHẮN TIN RIÊNG 1-1 (PRIVATE_MSG) ---

        public static async Task HandlePrivateMsgAsync(ClientSession from, Packet p)
        {
            if (!p.Data.TryGetValue("to", out var to) ||
                !p.Data.TryGetValue("content", out var content) ||
                string.IsNullOrWhiteSpace(content))
            {
                await SendErrorAsync(from, "BAD_REQUEST", "Thiếu 'to' hoặc 'content'.");
                return;
            }

            if (content.Length > 4000)
            {
                await SendErrorAsync(from, "MSG_TOO_LONG", "Tin nhắn quá dài (tối đa 4000 ký tự).");
                return;
            }

            if (string.Equals(to, from.Username, StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorAsync(from, "BAD_REQUEST", "Không thể nhắn cho chính mình.");
                return;
            }

            if (!OnlineUsers.TryGetValue(to, out var target))
            {
                await SendErrorAsync(from, "USER_OFFLINE", $"{to} không online.");
                return;
            }

            var msg = MakePacket("PRIVATE_MSG",
                ("from", from.Username!),
                ("to", target.Username!),
                ("content", content),
                ("msgId", Guid.NewGuid().ToString("N")),
                ("time", DateTime.Now.ToString("HH:mm")));

            if (!await TrySendAsync(target, msg))
            {
                await SendErrorAsync(from, "USER_OFFLINE", $"{to} vừa ngắt kết nối.");
                return;
            }

            await TrySendAsync(from, msg);
        }


        // --- 3. TRẠNG THÁI ĐANG SOẠN TIN (TYPING) ---

        public static async Task HandleTypingAsync(ClientSession from, Packet p)
        {
            if (!p.Data.TryGetValue("to", out var to)) return;
            if (!OnlineUsers.TryGetValue(to, out var target)) return;

            p.Data.TryGetValue("isTyping", out var typing);
            await TrySendAsync(target, MakePacket("TYPING",
                ("from", from.Username!),
                ("isTyping", typing == "false" ? "false" : "true")));
        }


        // --- 4. TRẠNG THÁI ĐÃ XEM (SEEN) ---

        public static async Task HandleSeenAsync(ClientSession from, Packet p)
        {
            if (!p.Data.TryGetValue("to", out var to)) return;
            if (!OnlineUsers.TryGetValue(to, out var target)) return;

            p.Data.TryGetValue("msgId", out var msgId);
            await TrySendAsync(target, MakePacket("SEEN",
                ("from", from.Username!),
                ("msgId", msgId ?? "")));
        }


        // --- 5. XỬ LÝ TRUYỀN FILE / ẢNH / VIDEO (FILE_START, FILE_CHUNK, FILE_END) ---

        public static async Task HandleFilePacketAsync(ClientSession from, Packet p)
        {
            if (!p.Data.TryGetValue("to", out var to))
            {
                await SendErrorAsync(from, "BAD_REQUEST", "Thiếu thông tin người nhận ('to').");
                return;
            }

            if (!OnlineUsers.TryGetValue(to, out var target))
            {
                await SendErrorAsync(from, "USER_OFFLINE", $"{to} không online.");
                return;
            }

            if (string.Equals(to, from.Username, StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorAsync(from, "BAD_REQUEST", "Không thể gửi file cho chính mình.");
                return;
            }

            // Gắn thông tin 'from' từ server để chống giả mạo danh tính
            p.Data["from"] = from.Username!;

            // Chuyển tiếp gói tin file sang người nhận
            if (!await TrySendAsync(target, p))
            {
                await SendErrorAsync(from, "USER_OFFLINE", $"{to} vừa ngắt kết nối.");
                return;
            }
        }
    }
}
