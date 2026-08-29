using System.Windows;
using System.Windows.Markup;

namespace Printonator.UI.Localization;

/// <summary>
/// Markup extension cho XAML: <c>{l10n:Loc Main.FooterHint}</c> → chuỗi theo ngôn ngữ hiện tại.
/// Lưu ý: MarkupExtension resolve lúc XAML Loaded (sau App.OnStartup đã set culture) nên hoạt động
/// đúng với ngôn ngữ user chọn. KHÔNG dùng cho chuỗi cần đổi runtime (không có — ngôn ngữ cố định).
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => L10n.S(Key);
}