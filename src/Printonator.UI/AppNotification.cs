using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Printonator.UI;

/// <summary>Loại thông báo — quyết định màu pill + biểu tượng trong danh sách bell.</summary>
public enum NotificationKind
{
    Done,     // in xong — xanh
    Update,   // bản cập nhật mới — cam (actionable)
    Warning,  // cảnh báo/lỗi — đỏ
}

/// <summary>Một thông báo trong danh sách bell (thay thế card đơn cũ — hỗ trợ nhiều item).</summary>
public sealed class AppNotification
{
    public Guid Id { get; } = Guid.NewGuid();
    public NotificationKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public DateTime Time { get; }
    private bool _read;
    public bool Read
    {
        get => _read;
        set { if (_read != value) { _read = value; OnPropertyChanged(); } }
    }
    /// <summary>Hành động khi bấm vào thông báo (vd mở installer bản cập nhật); null = chỉ đọc.</summary>
    public Action? Act { get; }
    public string TimeText => Time.ToString("HH:mm");

    public AppNotification(NotificationKind kind, string title, string detail, Action? act = null)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        Time = DateTime.Now;
        Act = act;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}