# Printonator — Glossary thuật ngữ in (i18n)

> Nguồn: `src/Printonator.UI/Localization/Strings.json` (vi = source of truth).
> File này là ANCHOR cho mọi bản dịch mới — key mới phải thêm đủ 5 ngôn ngữ trong Strings.json
> và khớp nghĩa glossary. Không dịch rời từng chuỗi; dịch theo block có context.
> Thuật ngữ đầy đủ 300 key: sinh lại bằng `python i18n/gen_glossary.py` (nếu script tồn tại) hoặc
> đọc trực tiếp Strings.json. Dưới đây là các thuật ngữ CỐT LÕI (in/hàng đợi/máy in) dùng làm chuẩn.

| Key | VI (nguồn) | EN | zh-CN | ru | ja |
|---|---|---|---|---|---|
| Common.ApplyButton | Áp dụng | Apply | 应用 | Применить | 適用 |
| Common.CancelButton | Hủy | Cancel | 取消 | Отмена | キャンセル |
| Common.OkButton | OK | OK | 确定 | ОК | OK |
| Common.CheckUpdateButton | Kiểm tra bản mới | Check for updates | 检查更新 | Проверить обновления | 更新を確認 |
| Common.SheetAll | Tất cả | All | 全部 | Все | すべて |
| Common.PagesCountSuffix | trang | pages | 页 | стр. | ページ |
| Main.Title | Printonator | Printonator | Printonator | Printonator | Printonator |
| Main.AddFilesButton | Thêm file | Add files | 添加文件 | Добавить файлы | ファイルを追加 |
| Main.PrintButton | In | Print | 打印 | Печать | 印刷 |
| Main.PrintAllButton | In tất cả | Print all | 全部打印 | Печать всех | すべて印刷 |
| Main.PauseButton | Tạm dừng | Pause | 暂停 | Пауза | 一時停止 |
| Main.ResumeButton | Tiếp tục | Resume | 继续 | Продолжить | 再開 |
| Main.CancelButton | Hủy lô | Cancel batch | 取消批次 | Отменить пакет | バッチをキャンセル |
| Main.SearchPlaceholder | Tìm kiếm... | Search... | 搜索... | Поиск... | 検索... |
| Main.PrinterLabel | Máy in | Printer | 打印机 | Принтер | プリンター |
| Main.QueueColumn | File | File | 文件 | Файл | ファイル |
| Main.SettingsColumn | Cài đặt | Settings | 设置 | Настройки | 設定 |
| Main.StatusColumn | Trạng thái | Status | 状态 | Статус | ステータス |
| Main.JobsCountFormat | {0} file | {0} files | {0} 个文件 | Файлов: {0} | {0} ファイル |
| Main.SelectedCountFormat | Đã chọn {0} file | {0} files selected | 已选择 {0} 个文件 | Выбрано файлов: {0} | {0} ファイルを選択 |
| Settings.Title | Cài đặt in | Print settings | 打印设置 | Настройки печати | 印刷設定 |
| Settings.CopiesLabel | Số bản | Copies | 份数 | Копии | 部数 |
| Settings.PageRangeLabel | Khoảng trang | Page range | 页码范围 | Диапазон страниц | ページ範囲 |
| Settings.AllPages | Tất cả các trang | All pages | 所有页面 | Все страницы | すべてのページ |
| Settings.RangeExample | Ví dụ: 1-5, 8, 11-13 | e.g. 1-5, 8, 11-13 | 例如：1-5, 8, 11-13 | Например: 1-5, 8, 11-13 | 例: 1-5, 8, 11-13 |
| Settings.ColorModeLabel | Màu sắc | Color mode | 颜色 | Цвет | カラー |
| Settings.ColorAsPrinter | Theo máy in | As printer | 按打印机 | Как у принтера | プリンターに従う |
| Settings.ColorColor | Màu | Color | 彩色 | Цветной | カラー |
| Settings.ColorGrayscale | Đen trắng | Grayscale | 灰度 | Ч/б | グレースケール |
| Settings.DuplexLabel | In 2 mặt | Duplex | 双面 | Двусторонняя | 両面 |
| Settings.DuplexOff | 1 mặt | Single-sided | 单面 | Односторонняя | 片面 |
| Settings.DuplexLongEdge | 2 mặt — lật cạnh dài | Duplex — long edge | 双面 — 长边 | Двусторонняя — длинный край | 両面 — 長辺とじ |
| Settings.DuplexShortEdge | 2 mặt — lật cạnh ngắn | Duplex — short edge | 双面 — 短边 | Двусторонняя — короткий край | 両面 — 短辺とじ |
| Settings.PaperLabel | Khổ giấy | Paper size | 纸张大小 | Формат бумаги | 用紙サイズ |
| Settings.PaperAsDocument | Theo tài liệu (khổ gốc) | As document | 按文档 | Как в документе | ドキュメントに従う |
| Settings.PaperA4 | A4 | A4 | A4 | A4 | A4 |
| Settings.PaperA3 | A3 | A3 | A3 | A3 | A3 |
| Settings.TrayLabel | Khay giấy | Paper source | 纸源 | Лоток | 給紙トレイ |
| Settings.OrientationLabel | Chiều giấy | Orientation | 方向 | Ориентация | 向き |
| Settings.OrientationPortrait | Dọc | Portrait | 纵向 | Книжная | 縦 |
| Settings.OrientationLandscape | Ngang | Landscape | 横向 | Альбомная | 横 |
| Settings.ScaleLabel | Tỷ lệ | Scale | 缩放 | Масштаб | 拡大縮小 |
| Settings.ScaleAsDocument | Theo tài liệu | As document | 按文档 | Как в документе | ドキュメントに従う |
| Settings.ScaleFit | Vừa trang | Fit to page | 适应页面 | По размеру страницы | ページに合わせる |
| Settings.ScalePercent | Tỷ lệ % | Zoom % | 缩放 % | Масштаб % | ズーム % |
| Settings.PerSheetLabel | Số trang/tờ | Pages per sheet | 每页版数 | Страниц на листе | 1枚あたりのページ数 |
| Settings.BookletLabel | Tập nhỏ (booklet) | Booklet | 小册子 | Брошюра | 小冊子 |
| Settings.CollationLabel | Gom bản | Collate | 逐份 | Подбор | 部単位 |
| Settings.CollateOn | Gom bản | Collated | 逐份打印 | С подбором | 部単位で印刷 |
| Settings.CollateOff | Rời bản | Uncollated | 不逐份 | Без подбора | 部単位なし |
| Settings.ParityLabel | Chẵn/lẻ | Odd/even | 奇偶 | Четные/нечетные | 奇数/偶数 |
| Settings.ParityAll | Tất cả | All | 全部 | Все | すべて |
| Settings.ParityOdd | Trang lẻ | Odd pages | 奇数页 | Нечетные | 奇数ページ |
| Settings.ParityEven | Trang chẵn | Even pages | 偶数页 | Четные | 偶数ページ |
| Settings.QualityLabel | Chất lượng | Quality | 质量 | Качество | 品質 |
| Settings.QualityHigh | Cao | High | 高 | Высокое | 高 |
| Settings.QualityMedium | Trung bình | Medium | 中 | Среднее | 中 |
| Settings.QualityLow | Thấp | Low | 低 | Низкое | 低 |
| Settings.QualityDraft | Nháp | Draft | 草稿 | Черновое | 下書き |
| Settings.ProfileLabel | Cấu hình | Profile | 配置 | Профиль | プロファイル |
| Settings.SaveProfileButton | Lưu cấu hình | Save profile | 保存配置 | Сохранить профиль | プロファイルを保存 |
| Settings.DeleteProfileButton | Xóa cấu hình | Delete profile | 删除配置 | Удалить профиль | プロファイルを削除 |
| Settings.ExportProfileButton | Xuất cấu hình | Export profile | 导出配置 | Экспорт профиля | プロファイルをエクスポート |
| Settings.ImportProfileButton | Nhập cấu hình | Import profile | 导入配置 | Импорт профиля | プロファイルをインポート |
| Confirm.Title | Xác nhận in | Confirm print | 确认打印 | Подтверждение печати | 印刷の確認 |
| Confirm.EstimatedSheets | ~{0} tờ | ~{0} sheets | 约 {0} 张 | ~{0} листов | 約 {0} 枚 |
| Confirm.ApplyPrinterToAll | Áp dụng máy in này cho tất cả {0} file | Apply this printer to all {0} files | 将此打印机应用于所有 {0} 个文件 | Применить принтер ко всем {0} файлам | このプリンターをすべての {0} ファイルに適用 |
| Done.Title | Đã in xong | Print complete | 打印完成 | Печать завершена | 印刷完了 |
| Done.RemovePrintedFiles | Xóa file đã in khỏi hàng đợi | Remove printed files from queue | 从队列中删除已打印文件 | Удалить напечатанные файлы из очереди | 印刷済みファイルをキューから削除 |
| About.Title | Thông tin | About | 关于 | О программе | 情報 |
| About.Version | Phiên bản {0} | Version {0} | 版本 {0} | Версия {0} | バージョン {0} |
| About.LanguageLabel | Ngôn ngữ | Language | 语言 | Язык | 言語 |
| About.LanguageRestartPrompt | Đổi ngôn ngữ cần khởi động lại app | Changing language requires app restart | 更改语言需要重启应用 | Смена языка требует перезапуска | 言語の変更には再起動が必要です |
| Printer.Offline | Ngoại tuyến | Offline | 离线 | Не в сети | オフライン |
| Printer.Unresponsive | Không phản hồi | Unresponsive | 无响应 | Не отвечает | 応答なし |
| Printer.Online | Sẵn sàng | Ready | 就绪 | Готов | 準備完了 |
| Printer.Error | Lỗi | Error | 错误 | Ошибка | エラー |
| Printer.Properties | Thuộc tính máy in | Printer properties | 打印机属性 | Свойства принтера | プリンターのプロパティ |
| Printer.Preferences | Tùy chọn in | Printing preferences | 打印首选项 | Настройки печати | 印刷設定 |
| Job.Queued | Chờ in | Queued | 排队 | В очереди | 待機中 |
| Job.Converting | Đang xử lý | Converting | 处理中 | Преобразование | 変換中 |
| Job.Spooling | Đang gửi máy in | Spooling | 发送到打印机 | Отправка на принтер | スプール中 |
| Job.Done | Xong | Done | 完成 | Готово | 完了 |
| Job.Error | Lỗi | Error | 错误 | Ошибка | エラー |
| Job.Cancelled | Đã hủy | Cancelled | 已取消 | Отменено | キャンセル済み |
| Job.AwaitingApproval | Chờ duyệt | Awaiting approval | 等待批准 | Ожидает утверждения | 承認待ち |
| Approve.ApproveButton | Duyệt | Approve | 批准 | Утвердить | 承認 |
| Approve.RejectButton | Từ chối | Reject | 拒绝 | Отклонить | 拒否 |
| Approve.ApproveAll | Duyệt tất cả | Approve all | 全部批准 | Утвердить все | すべて承認 |
| Approve.RejectAll | Từ chối tất cả | Reject all | 全部拒绝 | Отклонить все | すべて拒否 |
| WatchFolder.Title | Thư mục theo dõi | Watch folder | 监视文件夹 | Отслеживание папки | 監視フォルダー |
| WatchFolder.AddButton | Thêm thư mục | Add folder | 添加文件夹 | Добавить папку | フォルダーを追加 |
| WatchFolder.AutoPrint | Tự in | Auto-print | 自动打印 | Автопечать | 自動印刷 |
| History.Title | Lịch sử in | Print history | 打印历史 | История печати | 印刷履歴 |
| History.ClearButton | Xóa lịch sử | Clear history | 清除历史 | Очистить историю | 履歴をクリア |
| Ctx.Print | In file này | Print this file | 打印此文件 | Печать этого файла | このファイルを印刷 |
| Ctx.Remove | Xóa khỏi danh sách | Remove from list | 从列表移除 | Удалить из списка | リストから削除 |
| Ctx.Settings | Cài đặt in (Item settings) | Print settings (item) | 打印设置（项目） | Настройки печати (элемент) | 印刷設定（項目） |
| Banner.Printing | Đang in {0}/{1}... | Printing {0}/{1}... | 正在打印 {0}/{1}... | Печать {0}/{1}... | 印刷中 {0}/{1}... |
| Banner.BatchStopped | Lô in tạm dừng do lỗi | Batch paused due to error | 批次因错误暂停 | Пакет приостановлен из-за ошибки | エラーによりバッチ一時停止 |
| Banner.OfflinePrinter | Máy in đang ngoại tuyến | Printer is offline | 打印机离线 | Принтер не в сети | プリンターがオフライン |
| Error.PrinterOffline | Máy in đang offline. Bật máy in lên rồi thử lại. | Printer is offline. Turn it on and try again. | 打印机离线。请开启后重试。 | Принтер не в сети. Включите его и повторите. | プリンターがオフラインです。電源を入れて再試行してください。 |
| Error.SpoolerFailed | Lỗi dịch vụ in. Thử lại sau. | Print spooler error. Try again later. | 打印服务错误。请稍后重试。 | Ошибка службы печати. Повторите позже. | 印刷スプーラーエラー。後でもう一度お試しください。 |
| Error.AppError | App gốc lỗi khi in | Source app failed to print | 源应用打印失败 | Ошибка приложения при печати | 元アプリでの印刷に失敗しました |
| Error.Unsupported | Định dạng không hỗ trợ | Unsupported format | 不支持的格式 | Неподдерживаемый формат | サポートされていない形式 |
| Error.Cancelled | Đã hủy | Cancelled | 已取消 | Отменено | キャンセル済み |
| Error.ApprovalRequired | Cần duyệt trước khi in | Approval required before printing | 打印前需要批准 | Требуется утверждение перед печатью | 印刷前に承認が必要です |
| Update.NewVersion | Có bản mới {0} | New version {0} available | 有新版本 {0} | Доступна новая версия {0} | 新しいバージョン {0} があります |
| Update.Downloading | Đang tải... | Downloading... | 下载中... | Загрузка... | ダウンロード中... |
| Update.InstallPrompt | Cài đặt bản mới? | Install new version? | 安装新版本？ | Установить новую версию? | 新しいバージョンをインストールしますか？ |

## Ghi chú chuẩn dịch
- **vi = nguồn sự thật** — không sửa vi khi thêm key mới trừ khi đổi nghĩa sản phẩm.
- **Placeholder {0}/{1}...** — giữ NGUYÊN văn trong mọi ngôn ngữ (check_i18n.ps1 verify parity).
- **Token kỹ thuật giữ nguyên**: All, S2:1-3, AsPrinter, PDF, Office, N-up, A4/A3... không dịch.
- **Sentinel "mặc định"** (stored value) — không dịch; UI chỉ display-map.
- **RU plural**: dùng template trung lập "Файлов: {0}" (không ICU). Nếu cần 3-form sau → key `_ru0/_ru1/_ru2`.
- **Length budget**: RU ~30% dài hơn EN, JA ~+10%, ZH ~-20% — chừa MinWidth + TextTrimming khi thêm UI.
- **Access key `&`**: giữ parity giữa các ngôn ngữ (nếu EN dùng &Print thì các ngôn ngữ khác giữ & ở cùng vị trí hợp lý).
