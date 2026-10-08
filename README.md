# Titan Tracking QA (`com.titan.tracking-qa`)

Package Unity kiểm tra tracking Firebase của game **từ source code**, so với file tracking plan (Excel) — ngay trong Unity Editor. Chỉ chạy trong Editor: không vào bản build, không cần sửa code game, không cần cắm điện thoại.

> Trạng thái: **0.1.0 — thử nghiệm**. Lõi phân tích đã chạy được trong Unity 6.3 (BlossomMatch: ~23–34s, ~220–280 MB bộ nhớ managed, không xung đột thư viện) và ra cùng kết quả với app Tracking Checker. Chưa dùng cho QA.

## Cài vào game (đang phát triển)

Thêm vào `Packages/manifest.json` của game (đường dẫn tương đối tới thư mục package trên máy):

```json
"com.titan.tracking-qa": "file:../../TitanTrackingQA",
```

Menu **Titan → QAUTO**:
- **Tracking QA CheckAll** — đọc code, so với doc (không cần chạy game).
- **Tracking QA Record** — ghi và kiểm event khi chơi trong Editor.

### Check all

Chọn file tracking (Excel), bấm **Check all** (chạy ngầm ~30s, Editor vẫn dùng bình thường).

- Danh sách mục bên trái (lọc theo nhóm Lỗi / Nghi ngờ / Thiếu / Lỗi doc / Ngoài plan, tìm theo event / file); bên phải là chi tiết với 2 tab **Lý do trong code** và **Tái hiện**.
- Bấm vị trí `File.cs:123` để mở đúng dòng trong IDE; prefab / scene thì được chọn trong Project.
- **Đúng thiết kế…**: đánh dấu ngoại lệ riêng của game (ghi lý do) — lần sau không tính là lỗi; xem lại / bỏ ở bộ lọc "Đã chấp nhận".
- **Chạy tiếp điểm mù**: phân tích tiếp từ chỗ bị dừng do giới hạn (không chạy lại từ đầu).
- **Mở báo cáo HTML** để gửi người khác.

### Record

Bấm **Play** là bắt đầu ghi (tắt được bằng "Tự Record khi Play"): package gắn vào `TrackingManager` của Titan như 1 dịch vụ tracking (giống Firebase) trước khi scene đầu chạy — nghe mọi event / user property / items, **không sửa code game hay Titan**, chỉ có trong Editor (assembly `Titan.TrackingQA.Recorder` chỉ biên dịch khi có `UNITY_EDITOR`).
- Ghi đúng như Firebase nhận (items của `resource_update` → `view_item`).
- Kiểm ngay theo doc: có trong doc, đủ param, đúng kiểu, giá trị hợp lệ, giới hạn Firebase, param rỗng (Firebase bỏ), property set trước event (TC-009).
- Lỗi Check all đã báo (cùng lỗi, đường gọi đi qua đúng chỗ code) hiện xám "đã biết", không báo lại.
- Mỗi lần Play là 1 phiên, tự lưu dần vào `UserSettings/TrackingQA/record/` (giữ 50 phiên), xem lại được.
- Trong Editor không có: quảng cáo thật, `screen_view` tự động, param Firebase tự thêm (`firebase_*`, `ga_session_*`, `fps`).

Báo cáo Check all gần nhất lưu ở `Library/TrackingQA/last-report.json` (mở lại Unity vẫn xem được); chỉnh sửa trên máy (ngoại lệ, chỉnh doc) ở `UserSettings/TrackingQA/` — không vào git của game.

## Cấu trúc

```
Recorder/            nghe event qua Titan khi Play (chỉ trong Editor)
Editor/              cửa sổ Tracking QA (UI Toolkit, chỉ Editor): TrackingQAWindow, QaRunner (chạy ngầm, lưu báo cáo), CodeNav (mở code)
Editor/Plugins/      lõi phân tích (TrackingChecker.Core, build netstandard2.1) + thư viện đi kèm (Roslyn, đọc Excel)
Knowledge/common/    kiến thức chung cho mọi game (cases/ = kho case TC-xxx)
Knowledge/games/     kiến thức riêng từng game (theo bundle id)
```

Lõi phân tích và kho kiến thức được phát triển + kiểm thử ở repo `TrackingChecker` (test + bộ game chuẩn). Cập nhật package = build lõi `netstandard2.1` ở đó rồi chép DLL + `knowledge/` sang đây.

## Kế hoạch

1. ✅ Lõi build được cho Unity (netstandard2.1)
2. ✅ Thử nạp + chạy Check all trong Editor (BlossomMatch, nhánh PackageQA) — cùng kết quả với app
3. ✅ Cửa sổ Check all đầy đủ (2 tab Lý do / Tái hiện, mở code, Đúng thiết kế, chạy tiếp điểm mù, báo cáo HTML)
4. ✅ Kho case TC-xxx (`Knowledge/common/cases/`, xem `docs/kho-case.md`): mỗi lỗi ghi mã case, chế độ xem **Theo case**, case riêng game `G-xxx`
5. ✅ Log giả lập theo kịch bản (chế độ **Kịch bản**): thắng / thua / chơi lại / về Home / thoát app / hồi sinh + kịch bản nhiều bước (`Knowledge/common/scenarios.json`)
6. ✅ TC-035: đổi tài nguyên phải có `resource_update` · lọc lỗi chỉ gặp trong Editor / bản debug / nút cheat
7. ⏳ Record khi chơi trong Editor — ✅ R1 (v0.2.0): nghe event, kiểm theo doc, bỏ qua lỗi đã biết, lưu phiên · R2: ghi bước bấm + kiểm theo kho case · R3: bảng lỗi gộp + lưu case G-xxx · R4: so với log giả lập
8. Gửi case lên kho chung (PR)
