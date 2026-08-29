namespace Printonator.Core.Models;

/// <summary>
/// Exception bọc PrintError đã phân loại — để lỗi cụ thể (PRINTER_OFFLINE, FILE_LOCKED...)
/// đi xuyên qua boundary async mà KHÔNG bị PrintQueue.WrapError nuốt thành SPOOLER_FAILED chung.
/// Engine/engine-wrapper ném PrintErrorException thay vì Exception trần khi đã có mã lỗi rõ.
/// </summary>
public sealed class PrintErrorException : Exception
{
    public PrintErrorException(PrintError error) : base(error.ToString()) => Error = error;

    /// <summary>Lỗi in đã phân loại (code/message/hint tiếng Việt) — giữ nguyên khi lên queue.</summary>
    public PrintError Error { get; }
}