# ViChat – Tóm tắt phần Cơ sở dữ liệu (CSDL)

Tài liệu này giải thích ngắn gọn phần CSDL của ViChat: **làm gì, có những gì, dùng công nghệ gì, tra cứu dữ liệu ra sao, hiển thị lên giao diện thế nào**, và **mỗi file trong code dùng để làm gì**. Đặt file này ở `docs/database.md`, cạnh `protocol.md`.

---

## 1. CSDL dùng để làm gì?

Trước khi có CSDL, mọi thứ chỉ nằm trong RAM của server: **tắt server là mất hết** tài khoản, phòng và tin nhắn. CSDL giúp lưu lại lâu dài 4 thứ:

| Lưu cái gì | Để làm gì |
|---|---|
| **Tài khoản người dùng** | Đăng ký, đăng nhập bằng tên + mật khẩu thật; chặn trùng tên |
| **Phòng chat** | Tắt server bật lại, các phòng đã tạo vẫn còn |
| **Thành viên phòng** | Biết ai ở phòng nào, ai là quản trị phòng (người tạo) |
| **Tin nhắn** | Tin trong phòng, tin riêng 1-1, thông tin file/ảnh đã gửi; mở lại app thấy được **lịch sử** |

**Nguyên tắc quan trọng:** chỉ **server** kết nối tới CSDL. Client (giao diện WPF) **không bao giờ** đọc CSDL trực tiếp, mà hỏi server qua gói tin TCP (ví dụ `GET_HISTORY`), server đọc CSDL rồi gửi kết quả về.

```
Client WPF  ──(TCP: LOGIN, GET_HISTORY...)──►  Server  ──(EF Core)──►  SQL Server LocalDB
            ◄──(LOGIN_RESULT, HISTORY...)───           ◄──────────────   (CSDL "ViChat")
```

---

## 2. Dùng những công nghệ gì?

| Thành phần | Vai trò | Hình dung |
|---|---|---|
| **SQL Server LocalDB** | Nơi thực sự chứa dữ liệu (một bản SQL Server nhỏ đi kèm Visual Studio) | Cái kho |
| **Entity Framework Core (EF Core)** | Thư viện để server C# tạo bảng, thêm, đọc, sửa dữ liệu bằng code C# thay vì viết SQL tay | Nhân viên xếp hàng vào kho |
| **EF Core Migrations** | Ghi lại cấu trúc bảng thành file code; server tự tạo/cập nhật bảng khi khởi động | Bản vẽ kho, ai cũng có một bản |
| **BCrypt (gói BCrypt.Net-Next)** | Băm mật khẩu trước khi lưu, **không bao giờ lưu mật khẩu thật** | Khóa két |
| **SSMS** hoặc **SQL Server Object Explorer** (trong Visual Studio) | Công cụ để **người** mở ra xem, kiểm tra dữ liệu | Cửa sổ nhìn vào kho |

Gói NuGet đã cài cho `ViChat.Server`:

- `Microsoft.EntityFrameworkCore.SqlServer`
- `Microsoft.EntityFrameworkCore.Design`
- `BCrypt.Net-Next`

Công cụ dòng lệnh: `dotnet-ef` (cài một lần mỗi máy bằng `dotnet tool install --global dotnet-ef`).

**Chuỗi kết nối** (giống nhau trên mọi máy, không có mật khẩu):

```
Server=(localdb)\MSSQLLocalDB;Database=ViChat;Trusted_Connection=True;TrustServerCertificate=True
```

`Trusted_Connection=True` nghĩa là dùng tài khoản Windows đang đăng nhập, nên không có mật khẩu nào bị đưa lên GitHub.

**Cả nhóm dùng chung thế nào?** Mỗi máy có một LocalDB riêng. Cấu trúc bảng nằm trong thư mục `Migrations/` được commit lên Git. Ai `git pull` rồi chạy server thì server tự tạo CSDL và bảng giống hệt nhau. Hôm demo, máy chạy server có CSDL, mọi client dùng chung qua server.

---

## 3. Có những bảng gì?

CSDL tên **`ViChat`**, gồm 4 bảng chính và 1 bảng của EF Core.

### 3.1. `Users` – tài khoản

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | int, tự tăng | Khóa chính |
| `Username` | chuỗi, **không trùng** | Tên đăng nhập (chữ thường, không dấu) |
| `PasswordHash` | chuỗi | Mật khẩu **đã băm bằng BCrypt** |
| `DisplayName` | chuỗi | Tên hiển thị |
| `CreatedAt` | ngày giờ | Thời điểm tạo tài khoản |

### 3.2. `Rooms` – phòng chat

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | int, tự tăng | Khóa chính |
| `Name` | chuỗi, **không trùng** | Tên phòng (không phân biệt hoa thường) |
| `CreatedById` | int, có thể rỗng | Người tạo phòng; **rỗng** với phòng `General` do hệ thống tạo |
| `CreatedAt` | ngày giờ | Thời điểm tạo |

### 3.3. `RoomMembers` – ai ở phòng nào

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `RoomId` | int | Phòng nào |
| `UserId` | int | Người nào |
| `IsAdmin` | bit | 1 = quản trị phòng (người tạo) |
| `JoinedAt` | ngày giờ | Thời điểm vào phòng |

Khóa chính là cặp `(RoomId, UserId)`: một người chỉ có một dòng trong mỗi phòng.

### 3.4. `Messages` – tin nhắn (cả phòng, 1-1, file, ảnh)

| Cột | Kiểu | Ý nghĩa |
|---|---|---|
| `Id` | bigint, tự tăng | Khóa chính, đồng thời là **`msgId`** gửi cho client |
| `RoomId` | int, có thể rỗng | Có giá trị nếu là **tin trong phòng** |
| `ReceiverId` | int, có thể rỗng | Có giá trị nếu là **tin riêng 1-1** |
| `SenderId` | int | Người gửi |
| `Content` | chuỗi dài | Nội dung tin (với file: dòng tóm tắt như "📎 baocao.docx") |
| `MessageType` | chuỗi | `text` / `file` / `image` / `system` |
| `FileName`, `FileSize` | chuỗi, số | Tên và dung lượng file (chỉ với file/ảnh) |
| `FileSha256` | chuỗi | Mã kiểm tra file |
| `StoredPath` | chuỗi | Đường dẫn file thật trong thư mục `Uploads` của server |
| `SentAt` | ngày giờ | Thời điểm gửi |

Ràng buộc: mỗi tin **hoặc** có `RoomId` **hoặc** có `ReceiverId`, không bao giờ có cả hai. Nhờ vậy một bảng chứa được cả tin phòng lẫn tin riêng.

**CSDL chỉ lưu thông tin file**, còn nội dung file thật nằm trong thư mục `Uploads/` của server.

### 3.5. `__EFMigrationsHistory`

Bảng do EF Core tự tạo, ghi lại các migration đã áp dụng (ví dụ `..._InitialCreate`). Không cần đụng vào.

### 3.6. Quan hệ giữa các bảng

```
Users 1 ──── n RoomMembers n ──── 1 Rooms
  │ 1                                 │ 1
  │                                   │
  n (SenderId, ReceiverId)            n (RoomId)
Messages ─────────────────────────────┘
```

- Một người ở nhiều phòng, một phòng có nhiều người (qua `RoomMembers`).
- Một người gửi nhiều tin; một phòng có nhiều tin.

---

## 4. Các file trong code dùng cho gì?

### 4.1. Phía server (`ViChat.Server`)

| File | Dùng để làm gì |
|---|---|
| `Data/Entities.cs` | Các class `UserEntity`, `RoomEntity`, `RoomMemberEntity`, `MessageEntity`. **Mỗi class là một bảng, mỗi property là một cột.** Đuôi `Entity` để khỏi trùng với class `Room` có sẵn của server |
| `Data/ViChatDbContext.cs` | "Cửa ngõ" vào CSDL: khai báo các bảng (`DbSet`), chuỗi kết nối LocalDB, các ràng buộc (tên không trùng, tin phải có phòng hoặc người nhận) |
| `Migrations/` (thư mục) | Do lệnh `dotnet ef migrations add ...` sinh ra. Ghi lại cấu trúc bảng. **Phải commit lên Git** để máy khác có cùng cấu trúc |
| `Data/AuthService.cs` | Mọi việc về tài khoản: `RegisterAsync` (kiểm tra tên hợp lệ, mật khẩu ≥ 6 ký tự, băm BCrypt, lưu) và `CheckLoginAsync` (tìm user, so mật khẩu với chuỗi băm) |
| `Data/ChatRepository.cs` | Mọi việc về phòng và tin nhắn: lưu phòng, thêm/xóa thành viên, lưu tin phòng, lưu tin 1-1, lưu thông tin file, **lấy lịch sử** phòng và lịch sử 1-1 |
| `Program.cs` | Lúc khởi động: gọi `Database.Migrate()` để tự tạo/cập nhật CSDL, nạp lại các phòng đã lưu vào `RoomManager`, in dòng "CSDL sẵn sàng...". Khi chạy: xử lý `REGISTER`, `LOGIN`, chặn gói khác nếu chưa đăng nhập, gọi `ChatRepository` để lưu tin và trả lịch sử |
| `ClientSession.cs` | Thêm `UserId` (Id trong bảng `Users`) và `IsLoggedIn`. Server dùng `UserId` khi lưu tin, không tin vào tên do client tự khai |
| `PrivateChat.cs` | Trước khi gửi tin 1-1, lưu vào CSDL để lấy `Id` thật làm `msgId` |
| `FileRelay.cs` | Khi file gửi xong và đúng SHA-256, báo thông tin để `ChatRepository` lưu thành một tin loại `file`/`image` |

**Quy tắc an toàn khi nhiều client:** mỗi hàm trong `AuthService` và `ChatRepository` tự tạo một `ViChatDbContext` mới rồi bỏ đi (`await using var db = new ViChatDbContext();`). Lý do: một `DbContext` **không dùng chung được giữa nhiều luồng**, mà server xử lý mỗi client trên một luồng riêng.

### 4.2. Phía client (`ViChat.Client`)

Client không có code CSDL, chỉ gửi gói tin và hiển thị kết quả:

| File | Liên quan tới CSDL thế nào |
|---|---|
| `Views/LoginWindow.xaml` + `ViewModels/LoginViewModel.cs` | Gửi `LOGIN` (tên + mật khẩu thật), nhận `LOGIN_RESULT`; hiện lỗi nếu sai mật khẩu hoặc đang đăng nhập ở nơi khác |
| Màn **Đăng ký** (View + ViewModel tương ứng) | Gửi `REGISTER`, nhận `REGISTER_RESULT` |
| `ViewModels/ChatViewModel.cs` | Tự gửi `GET_HISTORY` khi vào phòng và `GET_PRIVATE_HISTORY` khi mở chat riêng lần đầu; nhận `HISTORY` / `PRIVATE_HISTORY` rồi chèn tin cũ lên đầu khung chat |
| `Models/ChatMessage.cs` | Có thêm `MsgId` (Id của tin trong CSDL) để **không hiện trùng** một tin vừa có trong lịch sử vừa mới nhận |

---

## 5. Các gói tin liên quan tới CSDL

| Gói gửi | Dữ liệu | Server trả về |
|---|---|---|
| `REGISTER` | `username`, `password`, `displayName` | `REGISTER_RESULT { success, message }` |
| `LOGIN` | `username`, `password` | `LOGIN_RESULT { success, username, displayName }` hoặc `{ success=false, message }` |
| `GET_HISTORY` | `room` | `HISTORY { room, messages }` |
| `GET_PRIVATE_HISTORY` | `with` (username người kia) | `PRIVATE_HISTORY { with, messages }` |

`messages` là một chuỗi JSON, mỗi tin gồm: `msgId`, `from`, `content`, `time`, `kind` (`text`/`file`/`image`), `fileName`, `fileSize`.

Chưa đăng nhập mà gửi gói khác `REGISTER`/`LOGIN` → server trả `ERROR NOT_LOGGED_IN`.

---

## 6. Luồng hoạt động chính

### 6.1. Đăng ký

1. Người dùng nhập tên, mật khẩu, tên hiển thị → client gửi `REGISTER`.
2. Server (`AuthService.RegisterAsync`): kiểm tra tên 3–30 ký tự (chữ thường không dấu, số, `.`, `_`), mật khẩu ≥ 6 ký tự, tên chưa tồn tại.
3. Băm mật khẩu bằng BCrypt rồi lưu vào `Users`.
4. Trả `REGISTER_RESULT`.

### 6.2. Đăng nhập

1. Client gửi `LOGIN`.
2. Server (`AuthService.CheckLoginAsync`) tìm user, dùng `BCrypt.Verify` so mật khẩu.
3. Sai → luôn báo "Sai tên đăng nhập **hoặc** mật khẩu" (không nói rõ sai cái nào, để người lạ không dò được tên tài khoản có thật).
4. Tài khoản đang online ở máy khác → từ chối (chặn đăng nhập trùng).
5. Đúng → ghi `UserId`, `Username` vào phiên kết nối, cho vào phòng `General`, trả `LOGIN_RESULT`.

### 6.3. Gửi tin

1. Client gửi `SEND_MSG` (phòng) hoặc `PRIVATE_MSG` (1-1).
2. Server **lưu vào bảng `Messages` trước**, lấy `Id` vừa tạo làm `msgId`.
3. Server gửi tin cho người nhận kèm `msgId`.

### 6.4. Xem lại lịch sử

1. Vào phòng (hoặc mở chat riêng lần đầu) → client gửi `GET_HISTORY` (hoặc `GET_PRIVATE_HISTORY`).
2. Server (`ChatRepository`) lấy **50 tin gần nhất**, sắp xếp từ cũ đến mới.
3. Client chèn các tin đó lên đầu khung chat, bỏ qua tin trùng `msgId`.

---

## 7. Hiển thị lên giao diện thế nào?

- Tin cũ hiện **giống hệt tin mới**: bong bóng tím bên phải cho tin của mình, bong bóng xám bên trái cho người khác.
- **Giờ hiển thị:** tin trong hôm nay chỉ hiện giờ (`20:46`); tin ngày khác hiện cả ngày tháng (`04/11 20:46`).
- **Tin file/ảnh cũ** hiện dạng thẻ có tên và dung lượng file. Nội dung file thật nằm trên server (thư mục `Uploads`), nên hiện chưa bấm mở lại được tin file cũ (phần mở rộng: thêm gói `FILE_DOWNLOAD`).
- Đăng xuất rồi đăng nhập lại, hoặc **tắt server bật lại**, vẫn thấy đủ tài khoản, phòng và tin cũ.

---

## 8. Tra cứu, xem dữ liệu trong CSDL

### 8.1. Mở CSDL

**Cách 1 – SSMS:**

1. Server name: `(localdb)\MSSQLLocalDB`
2. Authentication: **Windows Authentication**
3. Tích **Trust Server Certificate**, bấm **Connect**
4. Mở **Databases → ViChat → Tables**

**Cách 2 – Ngay trong Visual Studio:** **View → SQL Server Object Explorer → (localdb)\MSSQLLocalDB → Databases → ViChat → Tables**, chuột phải bảng → **View Data**.

CSDL `ViChat` chỉ xuất hiện sau khi **server đã chạy ít nhất một lần**.

### 8.2. Chạy câu truy vấn

Chuột phải **ViChat → New Query**, dán câu lệnh, bấm **Execute** (F5).

**Xem tài khoản** (cột mật khẩu chỉ là chuỗi băm, không đọc được mật khẩu thật):

```sql
SELECT Id, Username, DisplayName, PasswordHash, CreatedAt FROM Users;
```

**Xem phòng và thành viên:**

```sql
SELECT r.Name AS Phong, u.Username AS ThanhVien, m.IsAdmin AS QuanTri
FROM RoomMembers m
JOIN Rooms r ON r.Id = m.RoomId
JOIN Users u ON u.Id = m.UserId
ORDER BY r.Name;
```

**Xem toàn bộ tin nhắn** (tin phòng, tin riêng, file, ảnh trong cùng một bảng):

```sql
SELECT m.Id, u.Username AS NguoiGui,
       ISNULL(r.Name, '(riêng) -> ' + v.Username) AS GuiToi,
       m.MessageType, m.Content, m.FileName, m.FileSize, m.SentAt
FROM Messages m
JOIN Users u      ON u.Id = m.SenderId
LEFT JOIN Rooms r ON r.Id = m.RoomId
LEFT JOIN Users v ON v.Id = m.ReceiverId
ORDER BY m.Id;
```

**Xem tin của một phòng:**

```sql
SELECT u.Username, m.Content, m.SentAt
FROM Messages m JOIN Users u ON u.Id = m.SenderId
WHERE m.RoomId = (SELECT Id FROM Rooms WHERE Name = 'General')
ORDER BY m.Id;
```

**Đếm tin theo từng người** (hợp để đưa vào báo cáo):

```sql
SELECT u.Username, COUNT(*) AS SoTin
FROM Messages m JOIN Users u ON u.Id = m.SenderId
GROUP BY u.Username
ORDER BY SoTin DESC;
```

**Sơ đồ quan hệ cho báo cáo:** trong SSMS, chuột phải **Database Diagrams → New Database Diagram**, chọn 4 bảng, rồi chụp lại.

---

## 9. Thành viên trong nhóm cần làm gì để kết nối vào CSDL?

Có **2 trường hợp**, cần làm khác nhau:

| Trường hợp | Ai | Cần CSDL trên máy mình không? |
|---|---|---|
| **A. Tự chạy server trên máy mình để code, thử** | Người sửa code server (Đạt, Khánh, Thùy, Kiên...) | **Có**: máy nào chạy server thì máy đó cần LocalDB |
| **B. Chỉ chạy client, kết nối vào server của bạn khác** (hôm demo, thử nhiều máy) | Mọi người còn lại | **Không**: client không đụng CSDL, chỉ cần IP của máy chạy server |

### 9.1. Trường hợp A: tự chạy server trên máy mình (làm một lần đầu)

**Bước 1. Kiểm tra máy đã có LocalDB chưa.** Mở Command Prompt hoặc PowerShell, gõ:

```bash
sqllocaldb info
```

- Thấy dòng `MSSQLLocalDB` là đã có, sang bước 2.
- Báo lỗi không có lệnh: mở **Visual Studio Installer → Modify → Individual components**, tìm **SQL Server Express LocalDB**, tích chọn rồi bấm **Modify** để cài. Cài xong mở lại PowerShell, gõ lại lệnh trên.

**Bước 2. Lấy code mới nhất** (luôn dùng `git clone` / `git pull`, **không tải ZIP**):

```bash
cd D:\ViChat_Group12          # thư mục repo trên máy bạn
git status                    # phải sạch; nếu còn file sửa dở thì commit trước
git checkout feature/ui-chat-main
git pull
```

Thay `feature/ui-chat-main` bằng nhánh chung mà trưởng nhóm thông báo, nếu khác.

**Bước 3. Build lại toàn bộ.** Trong Visual Studio: **Build → Rebuild Solution**. Các gói NuGet (EF Core, BCrypt) tự tải về, không cần cài tay.

**Bước 4. Chạy server.** Đặt **ViChat.Server** làm startup project (chuột phải → **Set as Startup Project**) rồi bấm **Start**. Lần chạy đầu, server tự:

1. Tạo CSDL `ViChat` trong LocalDB.
2. Tạo đủ các bảng theo thư mục `Migrations/`.
3. Thêm phòng `General`.

Cửa sổ server phải in dòng kiểu:

```
CSDL sẵn sàng: 0 tài khoản, 1 phòng, 0 tin nhắn.
Server đang lắng nghe cổng 5000...
```

Không cần mở SSMS, không cần tạo bảng bằng tay, **không cần sửa chuỗi kết nối**.

**Bước 5. Thử.** Chạy thêm client (chuột phải **ViChat.Client → Debug → Start New Instance**), bấm **Đăng ký** tạo tài khoản, đăng nhập, nhắn vài tin. Tắt server, bật lại, đăng nhập lại: tài khoản và tin cũ vẫn còn là thành công.

**Bước 6 (không bắt buộc). Xem dữ liệu** bằng SSMS hoặc Visual Studio theo mục 8.

Lưu ý: dữ liệu trên máy bạn là **dữ liệu riêng của bạn**. Tài khoản bạn tạo trên máy mình sẽ **không có** trên máy bạn khác, vì mỗi máy một LocalDB. Đây là điều bình thường khi code.

### 9.2. Mỗi lần có người đổi cấu trúc bảng

Khi trưởng nhóm báo "có migration mới":

```bash
git pull
```

Rồi **Rebuild Solution** và chạy lại server. Server tự áp migration mới (thêm cột, thêm bảng) vào CSDL trên máy bạn. Không phải làm gì thêm.

Chỉ người **tự tạo migration** mới cần công cụ `dotnet-ef`:

```bash
dotnet tool install --global dotnet-ef
```

### 9.3. Trường hợp B: chỉ chạy client, kết nối vào server của bạn khác

Dùng hôm demo hoặc khi thử nhiều máy trong cùng mạng (cùng Wi-Fi hoặc cùng mạng LAN).

**Máy chạy server** (chỉ một máy, máy này đã làm xong 9.1):

1. Chạy ViChat.Server.
2. Xem IP của máy: mở PowerShell, gõ `ipconfig`, lấy dòng **IPv4 Address** của card mạng đang dùng (ví dụ `192.168.1.25`).
3. Lần đầu chạy, Windows có thể hỏi cho phép mạng: tích **Private networks** rồi bấm **Allow**. Nếu lỡ bấm Cancel, mở **Windows Defender Firewall → Allow an app**, cho phép ViChat.Server, hoặc mở cổng TCP **5000**.
4. Báo IP cho các bạn trong nhóm.

**Các máy chạy client:**

1. Lấy code mới nhất và build như 9.1 bước 2–3 (để client cùng phiên bản với server).
2. Chạy **ViChat.Client** (không cần chạy server, không cần LocalDB).
3. Ở màn đăng nhập, nhập **IP của máy chạy server** và cổng **5000**.
4. **Đăng ký tài khoản mới** (vì tài khoản nằm trong CSDL của máy server, không phải máy bạn), rồi đăng nhập.

Hôm demo, tất cả tài khoản, phòng, tin nhắn của mọi người đều nằm chung trong CSDL của **máy chạy server**. Nên chọn trước một máy làm server, tạo sẵn vài tài khoản và phòng để demo cho nhanh.

### 9.4. Kiểm tra nhanh khi không kết nối được

| Hiện tượng | Kiểm tra |
|---|---|
| Server báo lỗi CSDL khi khởi động | Đã làm bước 1 (LocalDB) chưa? Chạy `sqllocaldb start MSSQLLocalDB` rồi chạy lại server |
| Client báo không kết nối được server | Server đã chạy chưa? IP đúng chưa? Hai máy có cùng mạng không? Tường lửa máy server đã cho phép cổng 5000 chưa? |
| Đăng nhập báo sai mật khẩu dù nhập đúng | Tài khoản đó tạo trên **máy khác**. Đăng ký lại trên server đang kết nối |
| Build báo file bị khóa (MSB3026/MSB3027) | Còn ViChat cũ đang chạy. Đóng hết rồi build lại |

---

## 10. Quy tắc làm việc cho cả nhóm

- **Muốn đổi cấu trúc bảng** (thêm cột, thêm bảng): sửa class trong `Data/Entities.cs`, rồi chạy

  ```bash
  dotnet ef migrations add TenThayDoi --project ViChat.Server
  ```

  và commit thư mục `Migrations/`. **Không sửa cấu trúc bảng bằng SSMS**, vì máy khác sẽ không có thay đổi đó.
- **Xem, xóa dữ liệu thử, chạy `SELECT`:** dùng SSMS thoải mái.
- **Muốn làm sạch dữ liệu thử:** tắt server, trong SSMS chuột phải **ViChat → Delete** (tích *Close existing connections*), chạy lại server để tạo mới.

---

## 11. Lỗi hay gặp

| Lỗi | Cách xử lý |
|---|---|
| Không kết nối được, báo *network-related or instance-specific error* | Kiểm tra tên `(localdb)\MSSQLLocalDB`; chạy `sqllocaldb start MSSQLLocalDB` rồi thử lại |
| Báo lỗi chứng chỉ (*certificate chain*) | Chưa tích **Trust Server Certificate** |
| Không thấy database `ViChat` | Server chưa chạy lần nào; chuột phải **Databases → Refresh** |
| SSMS kết nối vào `DESKTOP-...` với tài khoản `sa` | Đó là SQL Server khác trên máy. Đổi Server name thành `(localdb)\MSSQLLocalDB`, dùng Windows Authentication |
| *A second operation was started on this context...* | Đang dùng chung một `DbContext` cho nhiều luồng. Mỗi thao tác phải tạo `ViChatDbContext` mới |
| Máy chưa có LocalDB (`sqllocaldb info` báo lỗi) | **Visual Studio Installer → Modify → Individual components → SQL Server Express LocalDB** |
