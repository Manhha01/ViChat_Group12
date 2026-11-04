using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ViChat.Shared;

/// <summary>Tiến trình nhận một file (dùng để cập nhật thanh tiến trình).</summary>
public record FileProgressInfo(string FileId, string Name, long Received, long Size, int Percent);

/// <summary>Kết quả cuối cùng khi nhận xong (hoặc nhận lỗi) một file.</summary>
public record FileResultInfo(
    string FileId, string Name, bool Ok, string Code, string Message, string? SavedPath)
{
    /// <summary>Đóng gói thành gói FILE_RESULT để gửi cho bên gửi.</summary>
    public Packet ToPacket() => new()
    {
        Type = PacketTypes.FileResult,
        Data = new Dictionary<string, string>
        {
            ["fileId"] = FileId,
            ["name"] = Name,
            ["ok"] = Ok ? "true" : "false",
            ["code"] = Code,
            ["message"] = Message
        }
    };
}

/// <summary>
/// Nhận file từ chuỗi gói FILE_START → FILE_CHUNK... → FILE_END.
/// Dùng chung cho Server (lưu bản gốc, kiểm tra) và Client (nhận file về máy).
/// Mỗi kết nối nên dùng một FileReceiver riêng; các gói của cùng một kết nối
/// đến tuần tự nên không cần khóa cho từng file.
/// </summary>
public class FileReceiver
{
    private sealed class State
    {
        public string Name = "";
        public string SafeName = "";
        public string From = "";
        public string Kind = FileKinds.File;
        public long Size;
        public string ExpectedHash = "";
        public long Received;
        public int NextIndex;
        public string Path = "";
        public FileStream Out = null!;
        public IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public int LastPercent = -1;
    }

    private readonly string _saveDir;
    private readonly ConcurrentDictionary<string, State> _files = new();

    /// <summary>Báo khi phần trăm thay đổi. Trên WPF nhớ chuyển qua Dispatcher trước khi đụng UI.</summary>
    public event Action<FileProgressInfo>? Progress;

    /// <summary>Báo khi nhận xong (thành công hoặc thất bại).</summary>
    public event Action<FileResultInfo>? Completed;

    public FileReceiver(string saveDir)
    {
        _saveDir = saveDir;
        Directory.CreateDirectory(_saveDir);
    }

    /// <summary>Xử lý FILE_START. Trả về null nếu ổn; khác null là lỗi (file đã bị hủy).</summary>
    public FileResultInfo? HandleStart(Packet p)
    {
        string fileId = Get(p, "fileId");
        if (fileId == "")
            return Fail("", "", ErrorCodes.BadRequest, "Thiếu fileId.");

        string name = Get(p, "name");
        string hash = Get(p, "sha256").ToUpperInvariant();
        if (!long.TryParse(Get(p, "size"), out long size) || size < 0 || name == "" || hash.Length != 64)
            return Fail(fileId, name, ErrorCodes.BadRequest, "FILE_START thiếu hoặc sai trường.");

        if (size > ProtocolLimits.MaxFileBytes)
            return Fail(fileId, name, ErrorCodes.FileTooLarge,
                $"File vượt quá {ProtocolLimits.MaxFileBytes / (1024 * 1024)} MB.");

        var st = new State
        {
            Name = name,
            SafeName = MakeSafeName(name),
            From = Get(p, "from"),
            Kind = Get(p, "kind") == FileKinds.Image ? FileKinds.Image : FileKinds.File,
            Size = size,
            ExpectedHash = hash
        };
        // Tiền tố fileId để hai file trùng tên không đè nhau.
        st.Path = Path.Combine(_saveDir, fileId + "_" + st.SafeName);

        try
        {
            st.Out = new FileStream(st.Path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: false);
        }
        catch (Exception ex)
        {
            return Fail(fileId, name, ErrorCodes.ServerError, "Không tạo được file: " + ex.Message);
        }

        if (!_files.TryAdd(fileId, st))
        {
            st.Out.Dispose();
            TryDelete(st.Path);
            return Fail(fileId, name, ErrorCodes.BadRequest, "fileId đã tồn tại.");
        }
        return null;
    }

    /// <summary>Xử lý FILE_CHUNK. Trả về null nếu ổn; khác null là lỗi (file đã bị hủy).</summary>
    public FileResultInfo? HandleChunk(Packet p)
    {
        string fileId = Get(p, "fileId");
        if (!_files.TryGetValue(fileId, out var st))
            return Fail(fileId, "", ErrorCodes.FileUnknown, "Chunk của file chưa FILE_START.");

        if (!int.TryParse(Get(p, "index"), out int index) || index != st.NextIndex)
            return Abort(fileId, st, ErrorCodes.FileCorrupt, "Chunk sai thứ tự.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(Get(p, "data")); }
        catch (FormatException) { return Abort(fileId, st, ErrorCodes.FileCorrupt, "Base64 không hợp lệ."); }

        if (st.Received + bytes.Length > st.Size)
            return Abort(fileId, st, ErrorCodes.FileCorrupt, "Nhận nhiều dữ liệu hơn kích thước đã khai báo.");

        try
        {
            st.Out.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            return Abort(fileId, st, ErrorCodes.ServerError, "Lỗi ghi file: " + ex.Message);
        }

        st.Hash.AppendData(bytes);
        st.Received += bytes.Length;
        st.NextIndex++;

        int percent = st.Size == 0 ? 100 : (int)(st.Received * 100 / st.Size);
        if (percent != st.LastPercent) // chỉ báo khi % đổi, tránh làm UI quá tải
        {
            st.LastPercent = percent;
            Progress?.Invoke(new FileProgressInfo(fileId, st.Name, st.Received, st.Size, percent));
        }
        return null;
    }

    /// <summary>Xử lý FILE_END: đóng file, so SHA-256, trả kết quả (đồng thời bắn sự kiện Completed).</summary>
    public FileResultInfo HandleEnd(Packet p)
    {
        string fileId = Get(p, "fileId");
        if (!_files.TryRemove(fileId, out var st))
            return Fail(fileId, "", ErrorCodes.FileUnknown, "FILE_END của file chưa FILE_START.");

        st.Out.Dispose();
        string actual = Convert.ToHexString(st.Hash.GetHashAndReset());
        st.Hash.Dispose();

        FileResultInfo result;
        if (!int.TryParse(Get(p, "totalChunks"), out int total) || total != st.NextIndex)
            result = new FileResultInfo(fileId, st.Name, false, ErrorCodes.FileCorrupt,
                "Số chunk không khớp.", null);
        else if (st.Received != st.Size)
            result = new FileResultInfo(fileId, st.Name, false, ErrorCodes.FileCorrupt,
                "Thiếu dữ liệu so với kích thước khai báo.", null);
        else if (!string.Equals(actual, st.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            result = new FileResultInfo(fileId, st.Name, false, ErrorCodes.FileCorrupt,
                "SHA-256 không khớp, file bị lỗi khi truyền.", null);
        else
            result = new FileResultInfo(fileId, st.Name, true, "", "OK", st.Path);

        if (!result.Ok) TryDelete(st.Path);
        Completed?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Hủy một file đang nhận dở (xóa file tạm). Client gọi khi nhận FILE_RESULT ok=false
    /// cho một file mình đang nhận dở (do bên gửi hoặc server báo lỗi).
    /// </summary>
    public void Abort(string fileId)
    {
        if (_files.TryRemove(fileId, out var st))
        {
            st.Out.Dispose();
            st.Hash.Dispose();
            TryDelete(st.Path);
        }
    }

    /// <summary>Hủy mọi file đang nhận dở (gọi khi kết nối bị ngắt).</summary>
    public void AbortAll()
    {
        foreach (var id in _files.Keys.ToList())
            if (_files.TryRemove(id, out var st))
            {
                st.Out.Dispose();
                st.Hash.Dispose();
                TryDelete(st.Path);
            }
    }

    // ---------------- hàm phụ ----------------

    private FileResultInfo Abort(string fileId, State st, string code, string message)
    {
        _files.TryRemove(fileId, out _);
        st.Out.Dispose();
        st.Hash.Dispose();
        TryDelete(st.Path);
        return Fail(fileId, st.Name, code, message);
    }

    private FileResultInfo Fail(string fileId, string name, string code, string message)
    {
        var r = new FileResultInfo(fileId, name, false, code, message, null);
        Completed?.Invoke(r);
        return r;
    }

    private static string Get(Packet p, string key) =>
        p.Data != null && p.Data.TryGetValue(key, out var v) ? v : "";

    /// <summary>Chống tên file nguy hiểm kiểu "..\..\x.exe".</summary>
    private static string MakeSafeName(string name)
    {
        string n = Path.GetFileName(name.Replace('\\', '/'));
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "file" : n;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* bỏ qua */ }
    }
}