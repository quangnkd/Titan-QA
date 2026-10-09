# Titan Tracking QA (`com.titan.tracking-qa`)

Package Unity kiểm tra tracking Firebase của game **từ source code**, so với file tracking plan (Excel) — ngay trong Unity Editor. Chỉ chạy trong Editor: không vào bản build, không cần sửa code game, không cần cắm điện thoại.

> Trạng thái: **thử nghiệm** (đã chạy trên BlossomMatch và TileFruits, Unity 6.3). Lõi phân tích ra cùng kết quả với app Tracking Checker.

## Cài vào game

Thêm vào `Packages/manifest.json` của game (repo private — máy cần quyền đọc repo, giống các package Titan khác):

```json
"com.titan.tracking-qa": "https://github.com/quangnkd/Titan-QA.git#v0.6.5",
```

Đang sửa package thì trỏ tạm về thư mục trên máy: `"file:../../TitanTrackingQA"`.

Menu **Titan → QAUTO**:
- **Tracking QA CheckAll** — đọc code, so với doc (không cần chạy game).
- **Tracking QA Record** — ghi và kiểm event khi chơi trong Editor (bấm Play là tự ghi, không cần mở cửa sổ).

Trong cửa sổ CheckAll còn 2 nút: **Doc** (Check all hiểu file tracking thế nào; sửa / xác nhận chỗ đọc chưa chắc) và **Kho chung** (gửi chỉnh sửa trên máy cho cả team bằng PR; học từ chỗ Check all sai).

### Check all

Chọn file tracking (Excel), bấm **Check all** (chạy ngầm ~30s, Editor vẫn dùng bình thường).

- Danh sách mục bên trái (lọc theo nhóm Lỗi / Nghi ngờ / Thiếu / Lỗi doc / Ngoài plan, tìm theo event / file); bên phải là chi tiết với 2 tab **Lý do trong code** và **Tái hiện**.
- Bấm vị trí `File.cs:123` để mở đúng dòng trong IDE; prefab / scene thì được chọn trong Project.
- **Đúng thiết kế…**: đánh dấu ngoại lệ riêng của game (ghi lý do) — lần sau không tính là lỗi; xem lại / bỏ ở bộ lọc "Đã chấp nhận".
- **Check all báo nhầm…**: Check all đoán sai (code rác / không dùng, phụ thuộc Remote Config trên Firebase, lần sai luồng, chỉ có trong Editor…) — chọn lý do + ghi chú, lưu làm ngoại lệ của game; lý do được giữ lại để học, sửa luật.
- **Lưu thành case G-xxx…**: lưu mục thành case riêng của game (`games/<bundle id>/cases/G-xxx.json`, trên máy) — các lần Check all / Record sau gắn mã case này; sau có thể nâng thành case chung TC.
- **So với lần check trước** (cùng doc): mục mới có nhãn "mới"; chip **Đã sửa so với lần trước** liệt kê mục lần trước còn mà lần này không thấy.
- **Code đã đổi**: file code của mục đổi từ lúc check (băm nội dung file) → nhãn "code đã đổi — có thể đã sửa", bấm Check all để kiểm lại.
- Lỗi không tự mất: chỉ rời danh sách khi Check all chạy lại không còn thấy (vào "Đã sửa so với lần trước"), khi lưu ngoại lệ, khi Record bác bỏ (bộ lọc "Record bác bỏ"), hoặc khi chỉ gặp trong Editor / cheat (ẩn, xem lại được).
- **Chạy tiếp điểm mù**: phân tích tiếp từ chỗ bị dừng do giới hạn (không chạy lại từ đầu).
- **Mở báo cáo HTML** để gửi người khác.
- **Claude** (tắt mặc định, hỏi trước khi bật): Check all gửi kết quả + đoạn code liên quan cho Claude qua **Claude Code đang đăng nhập trên máy** (gói Claude của bạn, không cần API key) để xác nhận / bác bỏ mục nghi ngờ và tìm thêm lỗi; nút **Claude review** gửi báo cáo đang xem mà không phân tích lại. Code được gửi tới Anthropic.
- **Doc**: danh sách event / param / user property Check all đọc được, chỗ đọc chưa chắc (không rõ kiểu, mô tả có vẻ liệt kê giá trị nhưng không tách được…), giá trị hợp lệ Check all hiểu. Sửa tại chỗ: bỏ event / param game không dùng, sửa kiểu, viết lại giá trị (`0-thua; 1-thắng`) → **Lưu & xác nhận** (theo game + tên file doc, không sửa Excel), áp dụng ở lần Check all / Record sau.

### Kho chung (gửi cho cả team)

Ngoại lệ (Đúng thiết kế / Check all báo nhầm), case `G-xxx`, chỉnh doc lưu **trên máy** (`UserSettings/TrackingQA/knowledge-local`) và có hiệu lực ngay trên máy đó. Nút **Kho chung · N chờ gửi** (cửa sổ CheckAll) → xem trước → **Gửi lên kho chung (tạo PR)**:
- clone repo package vào thư mục riêng (`%LOCALAPPDATA%/TitanTrackingQA/kb-repo`, không đụng project game), tạo nhánh `kb/<game>-<thời điểm>`, ghi vào `Knowledge/games/<bundle id>/` (ngoại lệ ghép theo mã, case trùng mã tự đổi sang mã trống, tạo `.meta`), push, tạo PR bằng GitHub CLI (`gh`) — không có `gh` thì mở trang tạo PR trên trình duyệt. Dùng quyền git trên máy; token trong URL không ghi ra log;
- nội dung PR gồm từng chỉnh sửa + **tổng hợp báo nhầm** theo luật (để sửa luật chung);
- người duyệt merge PR → phát hành bản package mới → các máy cập nhật package thì bản trên máy đã có trong kho chung được tự dọn.

Tab **Học từ chỗ Check all sai**: báo nhầm theo luật + lý do (code rác, Remote Config, lần sai luồng, chỉ Editor) qua mọi game có dữ liệu, gợi ý sửa luật; các thao tác Record lệch log giả lập. (Ở repo TrackingChecker: `tc learn --dir <Knowledge>`.)

### Chạy không mở giao diện (CI / máy build)

```
Unity.exe -batchmode -projectPath <game> -executeMethod Titan.TrackingQA.Batch.CheckAll -trackingDoc <plan.xlsx> [-trackingPlatform iOS] [-trackingOut <thư mục>] -logFile -
```
Ra `report.json`, `report.html`, `summary.txt` (mặc định `Library/TrackingQA/batch`), kèm kiểm tra Record gắn được vào Titan của game. Mã thoát: 0 = không có Lỗi, 2 = có Lỗi, 1 = không chạy được.

### Record

Bấm **Play** là bắt đầu ghi (tắt được bằng "Tự Record khi Play"): package gắn vào `TrackingManager` của Titan như 1 dịch vụ tracking (giống Firebase) trước khi scene đầu chạy — nghe mọi event / user property / items, **không sửa code game hay Titan**, chỉ có trong Editor (assembly `Titan.TrackingQA.Recorder` chỉ biên dịch khi có `UNITY_EDITOR`).
- Ghi đúng như Firebase nhận (items của `resource_update` → `view_item`).
- Kiểm ngay theo doc: có trong doc, đủ param, đúng kiểu, giá trị hợp lệ, giới hạn Firebase, param rỗng (Firebase bỏ), property set trước event (TC-009).
- Ghi bước chơi: tự gắn người nghe vào nút / toggle UI khi Play (chỉ trong bộ nhớ) → "▶ Bấm Button Home (SettingPanel)" đứng trước các event nó gây ra; ghi chuyển scene. Bấm liên tiếp cùng kiểu được gộp ("Tile_38 … ×12").
- Kiểm theo kho case khi chơi: TC-007 (nút vừa bấm → giá trị đúng hành động), TC-010 (1 level_start ↔ 1 level_end), TC-012, TC-014, TC-015, TC-016, TC-027, event bắn 2 lần liền.
- Lỗi Check all đã báo (cùng lỗi, đường gọi đi qua đúng chỗ code) hiện xám "đã biết", không báo lại.
- Đối chiếu với Check all (cập nhật vào cửa sổ CheckAll, giữ qua các lần Check all — `UserSettings/TrackingQA/record-feedback.json`): **xác nhận** (Nghi ngờ → Lỗi), **bác bỏ** (Check all đoán sai, không tính; nút "Xác nhận báo nhầm" lưu thành ngoại lệ), **không tái hiện** (ghi chú).
- Mỗi lần Play là 1 phiên, tự lưu dần vào `UserSettings/TrackingQA/record/` (giữ 50 phiên), xem lại được.
- Kiểm thêm: về Home (bấm nút Home / `level_end` mang giá trị "về Home") mà chưa bấm vào chơi đã bắn `level_start` → lượt ma (TC-010).
- Luật đang tắt cho mọi game (`Knowledge/common/tracking-rules.json → disabledRules`) không báo ở cả Check all lẫn Record; hiện tắt: thứ tự user property / event (TC-009).
- **Lỗi do cheat được ẩn** (người chơi thật không gặp): đường gọi đi qua code cheat / debug (vd `Cheat.ActiveCheat` nhảy level) → ẩn mọi lỗi của lời gọi đó; ngay sau khi bấm nút cheat (vd `Setting/Cheat/ButtonWin`) → ẩn lỗi về trình tự lượt chơi, lỗi theo doc (param rỗng…) vẫn giữ. Không tính vào số lỗi, không vào bảng Lỗi Record; vẫn xem được (xám, ghi lý do).
- **So với log giả lập** (cần Check all có tab Kịch bản): mỗi cú bấm nút có trong log giả lập (vd “btnReplay” trong OutOfSpacePanel — tên GameObject "Button Replay" cũng khớp) được so với những gì Check all dự đoán: event luôn bắn mà lúc chơi không thấy, event bắn ngay khi bấm mà giả lập không có, giá trị số khác (vd win). Kết quả: **Khớp / Lệch / Chưa rõ** ở dòng bấm (dòng thời gian, "▶ Bấm ≠") và cộng dồn ở tab **Kịch bản** của CheckAll ("Record ×N · lệch ×K"). Lệch không phải lỗi tracking — là chỗ Check all đoán khác thực tế (code rác, Remote Config, nhánh khác) để QA xem và sửa nhận định.
- **Lỗi tổng hợp** (bảng Lỗi Record, `UserSettings/TrackingQA/record-issues.json`): lỗi mới (Check all chưa báo) cộng dồn qua mọi phiên — mã `R001…`, số lần gặp, số phiên, các bước bấm trước lần gặp đầu, mở đúng chỗ trong dòng thời gian. Trạng thái:
  - **Mới** → đi lại đúng chỗ đó (cùng đường gọi, cùng nút) 2 lần không còn lỗi → **Đã sửa?** (chưa đóng) → QA bấm **Xác nhận đã sửa**;
  - đã đóng / Đã sửa? mà gặp lại → **Gặp lại**; **Bỏ qua** thì gặp lại chỉ đếm số lần;
  - file code chỗ bắn đổi → nhãn "code đã đổi — có thể đã sửa".
  Lỗi Record còn mở cũng hiện trong cửa sổ CheckAll (chip **Chỉ lỗi Record**) và tính vào kết quả **Theo case**; Đúng thiết kế / lưu case G-xxx làm được ở cả 2 cửa sổ.
- Ghi bước **⏸ Rời cửa sổ game / ▶ Quay lại** (OnApplicationFocus / OnApplicationPause): bấm ra ngoài cửa sổ Game trong Editor = app xuống nền trên máy thật. Event game bắn lúc đó (vd `level_exit` ngay sau `level_start`) được ghi chú "(rời focus)" — đúng là phải bắn, không phải lỗi.
- Trong Editor không có: quảng cáo thật, `screen_view` tự động, param Firebase tự thêm (`firebase_*`, `ga_session_*`, `fps`).

Báo cáo Check all gần nhất lưu ở `Library/TrackingQA/last-report.json` (mở lại Unity vẫn xem được); chỉnh sửa trên máy (ngoại lệ, chỉnh doc) ở `UserSettings/TrackingQA/` — không vào git của game.

## Cấu trúc

```
Recorder/            nghe event qua Titan khi Play (chỉ trong Editor)
Editor/              cửa sổ Tracking QA (UI Toolkit, chỉ Editor): TrackingQAWindow, RecordWindow, KnowledgeWindow (kho chung), DocWindow,
                     QaRunner (chạy ngầm, lưu báo cáo), RecordController, Batch (chạy không giao diện), CodeNav (mở code)
Editor/Plugins/      lõi phân tích (TrackingChecker.Core, build netstandard2.1) + thư viện đi kèm (Roslyn, đọc Excel)
Knowledge/common/    kiến thức chung cho mọi game (cases/ = kho case TC-xxx)
Knowledge/games/     kiến thức riêng từng game (theo bundle id)
```

Lõi phân tích và kiến thức chung được phát triển + kiểm thử ở repo `TrackingChecker` (test + bộ game chuẩn). Cập nhật package = build lõi `netstandard2.1` ở đó rồi chép DLL + `knowledge/common/` sang đây. **`Knowledge/games/` là của kho chung ở repo này** (QA gửi qua PR) — không chép đè từ TrackingChecker.

## Kế hoạch

1. ✅ Lõi build được cho Unity (netstandard2.1)
2. ✅ Thử nạp + chạy Check all trong Editor (BlossomMatch, nhánh PackageQA) — cùng kết quả với app
3. ✅ Cửa sổ Check all đầy đủ (2 tab Lý do / Tái hiện, mở code, Đúng thiết kế, chạy tiếp điểm mù, báo cáo HTML)
4. ✅ Kho case TC-xxx (`Knowledge/common/cases/`, xem `docs/kho-case.md`): mỗi lỗi ghi mã case, chế độ xem **Theo case**, case riêng game `G-xxx`
5. ✅ Log giả lập theo kịch bản (chế độ **Kịch bản**): thắng / thua / chơi lại / về Home / thoát app / hồi sinh + kịch bản nhiều bước (`Knowledge/common/scenarios.json`)
6. ✅ TC-035: đổi tài nguyên phải có `resource_update` · lọc lỗi chỉ gặp trong Editor / bản debug / nút cheat
7. ✅ Record khi chơi trong Editor — ✅ R1 (v0.2.0): nghe event, kiểm theo doc, bỏ qua lỗi đã biết, lưu phiên · ✅ R2 (v0.3.0): ghi bước bấm + kiểm theo kho case + đối chiếu Check all · ✅ R3 (v0.4.0): bảng Lỗi Record gộp qua phiên, so với lần check trước, code đã đổi, lưu case G-xxx, Check all báo nhầm có lý do · ✅ R4 (v0.5.0): so với log giả lập, ẩn lỗi do cheat
8. ✅ (v0.6.0) Gửi lên kho chung bằng PR · học từ chỗ Check all sai · cửa sổ Doc · Claude review · chạy batch (CI) · thử trên TileFruits
