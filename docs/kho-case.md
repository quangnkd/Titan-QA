# Kho case tracking (TC-xxx) — bản đề xuất để duyệt

> Trạng thái: **đã có trong code** (mục 7, bước 1–3): 44 case ở `Knowledge/common/cases/`, mỗi lỗi ghi mã case, tab **Theo case** trong cửa sổ Tracking QA, case riêng game `G-xxx` chạy được. Các câu ở mục 8 vẫn chờ chốt; phần suy luận của TC-030, TC-031, TC-037 đã chốt bật.

## 1. Kho case là gì

Mỗi **case** là 1 điều **phải đúng** về tracking, viết chung cho mọi game, có mã số (TC-001, TC-002…).
Ví dụ: *"Mỗi lượt chơi chỉ có đúng 1 `level_end`"*, *"Bấm chơi lại → `level_end.win` phải là giá trị 'replay' theo doc"*.

- **Check all** (đọc code) và **Record** (chơi trong Editor) cùng đọc một kho case → lỗi nào cũng ghi theo mã: *"Vi phạm TC-010: level_end bắn 2 lần"*.
- Case chỉ nói **cái gì phải đúng**. Còn ở game này hành động đó nằm ở code nào thì package tự tìm (nút trong prefab, hàm, callback…), vì mỗi dev code một kiểu.
- Thêm case **cùng loại kiểm tra** với case đã có = chỉ thêm 1 file dữ liệu, **không sửa code, không build lại**.

## 2. Một case gồm những gì

| Trường | Ý nghĩa | Ví dụ |
|---|---|---|
| `id` | Mã case. Kho chung: `TC-001`…; riêng 1 game: `G-001`… (trong thư mục của game) | `TC-007` |
| `title` | 1 câu: điều phải đúng | Hành động người chơi → đúng giá trị doc định nghĩa cho hành động đó |
| `why` | Vì sao quan trọng: sai thì số liệu nào sai | Dashboard tỉ lệ replay / thoát sẽ đếm nhầm |
| `category` | Nhóm khi vi phạm: **Lỗi** / **Thiếu** / **Nghi ngờ** / **Lỗi doc** / **Ngoài plan** | Lỗi |
| `appliesWhen` | Case chỉ có hiệu lực khi điều kiện này đúng ở game đang check. Không đúng → **Không áp dụng** (không phải lỗi) | doc có `level_end.win` và định nghĩa giá trị cho "replay" |
| `check` | **Loại kiểm tra** (chọn trong danh sách cố định, mục 3) + tham số | `{ "kind": "value_for_action", "event": "level_end", "param": "win", "action": "replay" }` |
| `staticCheck` | Check all kiểm được không: `yes` / `partial` / `no` (chỉ kiểm khi Record) | `yes` |
| `runtimeCheck` | Record kiểm được không: `yes` / `no` | `yes` |
| `preconditions` | Điều kiện trước khi kiểm: dữ liệu sạch, không dùng trạng thái của case khác, bản doc khớp build | Level chưa chơi lần nào |
| `repro` | Các bước tái hiện chung, như người chơi | Vào level → thua → bấm Chơi lại |
| `example` | Ví dụ kỳ vọng cụ thể (dùng làm game mẫu để test và kịch bản cho Record) | Đứng yên 5s, chơi 3s → `duration = 3` |
| `docRef` | Ô nguồn trong doc (điền khi chạy trên 1 game) | `Final Tracking!G107` |
| `basis` | Căn cứ: `doc` (doc / Master ghi rõ) hoặc `inferred` (suy luận, cần chốt trước khi bật) | doc |
| `source` | Nguồn: luật gốc / Record / QA báo; game, người, ngày | Record · Bloom Tile · QA · 08/10/2026 |
| `status` | `draft` (nháp) / `active` (đang dùng) / `retired` (ngừng) | active |
| `test` | Game mẫu chứa đúng lỗi này + kết quả mong đợi (bắt buộc với case chung) | `tests/fixtures/MiniGame` → vi phạm 1 lần |

Mỗi case 1 file JSON, để khi thêm hoặc sửa thì PR dễ xem:

```
Knowledge/common/cases/TC-007.json        ← kho chung (mọi game)
Knowledge/games/<bundle id>/cases/G-001.json  ← riêng 1 game
```

### Ví dụ 1 — case dùng loại kiểm tra có sẵn

```json
{
  "id": "TC-007",
  "title": "Hành động người chơi → đúng giá trị doc định nghĩa cho hành động đó",
  "why": "Ví dụ doc định nghĩa win=3 là 'ấn replay' mà code gửi 2 → mọi số liệu replay/thoát đều sai.",
  "category": "Lỗi",
  "appliesWhen": { "docDefinesValueForAction": true },
  "check": { "kind": "value_for_action" },
  "staticCheck": "yes",
  "runtimeCheck": "yes",
  "repro": ["Làm hành động X (thắng / thua / chơi lại / về Home / thoát app…)", "Xem giá trị param của event"],
  "source": "Luật gốc (value_action_mismatch)",
  "status": "active",
  "test": { "fixture": "MiniGame", "expect": "violation" }
}
```

### Ví dụ 2 — case riêng 1 game, lưu từ Record

```json
{
  "id": "G-001",
  "title": "Bloom Tile: bấm btnReplay ở OutOfSpacePanel → level_end.win = giá trị 'replay' của doc",
  "category": "Lỗi",
  "appliesWhen": { "doc": "level_end.win" },
  "check": { "kind": "value_for_action", "event": "level_end", "param": "win", "action": "replay", "trigger": "OutOfSpacePanel/btnReplay" },
  "staticCheck": "yes",
  "runtimeCheck": "yes",
  "repro": ["Vào level 5", "Xếp hết chỗ trống → OutOfSpacePanel hiện", "Bấm btnReplay"],
  "source": "Record · com.no1ornothing.bloom.tile.match.puzzle · QA · 08/10/2026",
  "status": "active"
}
```

## 3. Loại kiểm tra (danh sách cố định)

Mỗi loại là 1 đoạn code kiểm tra, viết 1 lần, dùng cho mọi case cùng loại. Thêm loại mới mới cần sửa code (và duyệt như sửa code).

| Loại (`kind`) | Kiểm gì | Check all | Record |
|---|---|---|---|
| `doc_event` | Event trong doc có trong code, bắn từ luồng chạy thật | ✅ có | ✅ |
| `doc_param` | Param doc yêu cầu được gửi, đúng tên, đúng kiểu | ✅ có | ✅ |
| `doc_value` | Giá trị gửi thuộc tập giá trị doc; mỗi giá trị doc có nhánh bắn | ✅ có | ✅ |
| `doc_property` | User property trong doc được set | ✅ có | ✅ |
| `value_for_action` | Hành động (thắng, thua, replay, home, thoát…) → đúng giá trị doc định nghĩa cho hành động đó | ✅ có | ✅ |
| `property_after_event` | Property set ngay **sau** event tương ứng | ✅ có | ✅ |
| `once_per_play` | Event chỉ bắn đúng 1 lần mỗi lượt chơi | ✅ có (`level_end`) | ✅ |
| `firebase_limits` | Độ dài tên/giá trị, số param, tiền tố cấm | ✅ có | ✅ |
| `doc_naming` | Doc dùng tên cũ / nhiều booster hơn game | ✅ có | — |
| `on_action_must_fire` | Hành động X **phải** bắn event E với giá trị V (vd kill app giữa level → `level_end win=2`) | 🆕 cần làm (đi xuôi từ hành động) | ✅ |
| `param_required_when` | Param bắt buộc khi có điều kiện (vd banner → phải có `ad_placement`) | 🆕 cần làm | ✅ |
| `counter` | Biến đếm tăng đúng 1 khi làm hành động X | 🆕 cần log giả lập | ✅ |
| `runtime_value` | Giá trị chỉ biết khi chạy (vd `remaining_value` = số dư thật) | — | ✅ |
| `sequence` | Thứ tự / đi cặp giữa các event (phễu quảng cáo, phễu IAP, start–end, open–close) | ⏳ một phần | ✅ |
| `resource_change_tracked` | Mọi chỗ đổi tài nguyên trong code đều có `resource_update` trên cùng luồng | ⏳ có (nhận biến theo tên — `tracking-rules.json` → `resourceTracking`) | ✅ |
| `no_duplicate_with_package` | Game không bắn lại event package Titan đã bắn | 🆕 cần làm | ✅ |
| `custom` | Case kiểu mới chưa có loại kiểm tra → chỉ lưu mô tả + cách tái hiện, gắn nhãn "chưa tự kiểm được" | ⏳ | ⏳ |

**Chỉ báo lỗi người chơi thật gặp** (bản release trên điện thoại, dữ liệu lên Firebase): đọc code như bản build thật (không có `#if UNITY_EDITOR`, thư mục Editor, asmdef chỉ Editor); lỗi mà mọi đường gây ra đều chỉ có trong Unity Editor (`Application.isEditor`, `OnValidate`, `[ContextMenu]`…), bản debug (`Debug.isDebugBuild`) hoặc nút cheat thì **ẩn, không tính vào số lỗi và kết quả case** (vẫn xem lại được). Event chỉ bắn từ những đường đó = người chơi thật không bao giờ bắn → báo Lỗi ở TC-002. Chưa chắc (không lần ra thao tác) thì vẫn hiện — không giấu lỗi khi chưa chắc. Danh sách điều kiện Editor / debug ở `Knowledge/common/code-patterns.json` → `deviceFilter`.

Nguyên tắc: **kỳ vọng luôn lấy từ doc / Master, không lấy code hiện tại làm đáp án**. Quy tắc doc không ghi rõ (vd thứ tự giữa quảng cáo, thưởng, tài nguyên) để `basis: inferred` và chưa bật cho tới khi được chốt.

## 4. Kết quả của 1 case trên 1 game

| Kết quả | Nghĩa |
|---|---|
| **Đạt** | Đã kiểm, đúng |
| **Vi phạm** | Có mục **Lỗi**: đã làm nhưng bắn sai / không bắn ở nhánh đã làm / đếm sai → số liệu sai. Chỉ trường hợp này mới là vi phạm |
| **Nghi ngờ** | Chỉ có mục Nghi ngờ (cần xem / chạy thật để xác nhận) |
| **Thiếu** | Chỉ có mục Thiếu: không tìm thấy trong code — **chưa làm**, không phải bắn sai. Là cảnh báo, xếp dưới Vi phạm |
| **Lỗi doc** / **Ngoài plan** | Chỉ có mục Lỗi doc (doc lệch chuẩn) / Ngoài plan (có trong code, không có trong doc) |

Một case có nhiều loại mục thì lấy loại nặng nhất (Lỗi → Nghi ngờ → Thiếu → Lỗi doc → Ngoài plan). Danh sách "Theo case" xếp theo đúng thứ tự đó, rồi mới đến Đạt / chưa kiểm / không áp dụng.

| Kết quả khác | Nghĩa |
|---|---|
| **Không áp dụng** | `appliesWhen` không đúng (vd doc không có event đó, game không có tính năng đó) |
| **Đúng thiết kế** | Vi phạm nhưng QA đã đánh dấu là ngoại lệ của game này (nút "Đúng thiết kế") |
| **Chỉ kiểm khi Record** | Check all không kiểm được; nhắc QA chơi thử để kiểm |
| **Doc chưa rõ** | Doc chưa đủ để kết luận (chưa rõ param bắt buộc hay tuỳ chọn, giá trị chưa định nghĩa, doc có nhiều booster hơn game…) → hỏi team data, không tính là lỗi game |
| **Không quan sát được** | Có kiểm nhưng không thấy được kết quả (vd quảng cáo / IAP không chạy thật trong Editor) |

Cửa sổ Tracking QA sẽ có thêm tab **Theo case**: mỗi case 1 dòng với kết quả trên, bấm vào để xem các lỗi thuộc case đó.

## 5. Chung hay riêng, và ai duyệt

| Mức | Nằm ở | Dùng khi | Có hiệu lực |
|---|---|---|---|
| **Riêng game** (`G-xxx`) | `games/<bundle id>/cases/` | Lỗi đặc thù của game (tính năng riêng, nút riêng) | Ngay trên máy; gửi lên kho chung qua PR khi muốn cả team dùng |
| **Chung** (`TC-xxx`) | `common/cases/` | Quy tắc đúng với mọi game Titan | Sau khi: chạy trên **bộ game chuẩn** không báo nhầm + có game mẫu bắt được lỗi + **người duyệt merge PR** (QA phụ trách package; Claude chuẩn bị và review trước) |

- Lưu case riêng game ngay trong Unity: nút **Lưu thành case G-xxx…** ở chi tiết 1 mục (cửa sổ CheckAll) hoặc 1 lỗi (cửa sổ Record → Lỗi tổng hợp). Case tạo ra khớp đúng luật + event + param của mục đó, ghi nguồn (Check all F… / Record R…, người lưu, ngày) và các bước tái hiện.
- Case riêng game có thể nâng lên case chung, khi đó case chung ghi lại nguồn (`từ G-001 của Bloom Tile`). Tool **tự đề xuất** (không tự nâng): G-xxx giống nhau (cùng luật + event + param) ở ≥ 2 game → hiện ở cửa sổ Kho chung, trong PR và `tc learn`; G-xxx đã có TC chung bao → gợi ý bỏ.
- Mục Check all báo nhầm được lưu kèm lý do (`exceptions.json`: `kind: "false_positive"`, `cause`: code_unused / remote_config / wrong_flow / editor_only / other) — gom lại để sửa luật / thêm case chung.
- Mã case không đổi, không dùng lại. Case không dùng nữa thì để `retired`, không xoá, để báo cáo cũ vẫn tra được.
- Claude (tuỳ chọn) giúp: khái quát case từ Record thành case chung, tìm case trùng, tạo game mẫu, chạy thử. Người duyệt luôn quyết định cuối.

## 6. Danh sách case ban đầu

Chuyển từ ~30 luật đang chạy trong lõi và 12 lỗi hay gặp (SKILL.md). Các mục độ phủ (`coverage_*`, tên event tạo lúc chạy) **không phải case**: chúng là cảnh báo "package chưa nhìn thấy hết", luôn chạy.

| Mã | Điều phải đúng | Nhóm khi vi phạm | Check all | Record | Từ luật cũ |
|---|---|---|---|---|---|
| **A. So với doc** | | | | | |
| TC-001 | Event trong doc phải có trong code, đúng tên | Thiếu (gần giống tên → Lỗi) | ✅ | ✅ | event_missing, event_name_typo |
| TC-002 | Event phải bắn từ luồng chạy thật — không chỉ trong code không ai gọi, sau cờ không bao giờ set, hay chỉ từ nút cheat/debug. (Code trong `#if` không bật cho nền tảng coi như không có → báo ở TC-001) | Lỗi (chỉ một phần chỗ bắn chết → Nghi ngờ) | ✅ | — | event_only_in_dead_code, event_only_in_debug, some_origins_dead, dead_flag |
| TC-003 | Param doc yêu cầu phải được gửi ở mọi chỗ bắn, đúng tên | Thiếu (không chỗ nào gửi) / Lỗi (sót ở vài chỗ, sai tên) | ✅ | ✅ | param_missing, param_name_mismatch |
| TC-004 | Kiểu param đúng doc (int / string / float) | Lỗi | ✅ | ✅ | param_type |
| TC-005 | Giá trị gửi phải thuộc tập giá trị doc. Ví dụ hay gặp: `ads_show.status` gửi "not_ready" / "no_ad_unit" thay vì "success" / "fail by …" (SKILL #7) | Lỗi | ✅ | ✅ | value_not_in_doc |
| TC-006 | Mỗi giá trị doc định nghĩa phải có ít nhất 1 nhánh bắn | Thiếu (chưa làm nhánh đó); nếu hành động đó đang bắn giá trị khác → Lỗi (qua TC-007) | ✅ | — | value_never_sent |
| TC-007 | Hành động người chơi → đúng giá trị doc định nghĩa cho hành động đó | Lỗi | ✅ | ✅ | value_action_mismatch |
| TC-008 | User property trong doc phải được set | Thiếu | ✅ | ✅ | prop_missing |
| TC-009 | ⛔ **Đang tắt** (09/10/2026, QA quyết định bỏ qua lỗi thứ tự property / event — `tracking-rules.json → disabledRules`). User property set ngay **sau** event tương ứng (coin / lives / lives_infinity / booster sau `resource_update`; `iap_count` sau `Purchase_Success`; `reward_count`, `inter_count` sau `ad_impression_*`) | Nghi ngờ (Check all) / Lỗi (Record thấy thật) | ✅ | ✅ | flow_property_before_event |
| **B. Luồng chơi (theo Master)** | | | | | |
| TC-010 | Mỗi lượt chơi có đúng 1 `level_start` và 1 `level_end` — không thừa, không thiếu ở nhánh nào (thắng, thua, replay, home, thoát) | Lỗi | ⏳ một phần (có: `level_end` bắn 2 lần) | ✅ | flow_double_level_end |
| TC-011 | Kill app / thoát giữa level → lần mở sau phải có `level_end win=2` | Lỗi | ⏳ một phần (qua TC-006, TC-002) | ✅ | mới |
| TC-012 | `level_end.level_id` là level đang chơi — không tăng level trước khi bắn | Lỗi | 🆕 (log giả lập kịch bản "Thắng" đã chỉ ra; chưa thành mục lỗi) | ✅ | mới (SKILL #3) |
| TC-013 | Không bắn `level_exit` khi quảng cáo toàn màn hình làm app pause (chỉ áp dụng khi doc có `level_exit`; Master: chỉ cần `level_exit` nếu không log được `level_end win=2`) | Lỗi | 🆕 | ✅ | mới (SKILL #4) |
| TC-014 | `level_play` bắn đúng mốc theo doc của game (Master: 1 = move đầu, 2/5/8 ≈ 20/50/80%, 0 = stuck), mỗi `type` chỉ 1 lần mỗi lượt. Ví dụ: chơi hết level → đúng `[1, 2, 5, 8]` | Lỗi | 🆕 | ✅ | mới (Master) |
| TC-015 | `play_index` = lần thứ mấy chơi level đó: tăng 1 mỗi `level_start`, giống nhau ở mọi event cùng lượt. Ví dụ: chơi lần 1 → 1, chơi lại → 2 | Lỗi | 🆕 cần log giả lập | ✅ | mới (Master) |
| TC-016 | `resource_update.value` luôn dương (kể cả khi spend); `remaining_value` = số dư sau thay đổi | Lỗi | ⏳ một phần (giá trị âm / dấu trừ trong code) | ✅ | mới (SKILL, Master) |
| **C. Giới hạn Firebase** | | | | | |
| TC-017 | Tên event ≤ 40, tên param ≤ 40, giá trị ≤ 100, tên property ≤ 24, giá trị property ≤ 36 ký tự; không dùng tiền tố `firebase_`, `google_`, `ga_` | Lỗi | ✅ | ✅ | fb_* |
| TC-018 | Tối đa 25 param mỗi event (tính cả `fps` Titan tự thêm), kể cả param dựng trong vòng lặp | Lỗi / Nghi ngờ | ✅ | ✅ | fb_param_count, fb_param_count_loop |
| **D. Package Titan / SDK** | | | | | |
| TC-019 | Event quảng cáo mà doc yêu cầu `ad_placement` thì phải có giá trị có nghĩa (không rỗng / "null"). Banner: theo doc từng game — Master đang ghi `ad_placement` của banner là chưa implement → Doc chưa rõ | Lỗi | 🆕 | ✅ | mới (SKILL #5) |
| TC-020 | Tên `booster_N` đúng tab Booster list của game và nhất quán: cùng 1 booster dùng cùng số ở `level_*` (số đã dùng), `resource_update.item_name` và property `booster_N` | Lỗi | ⏳ một phần (giá trị hằng trong code) | ✅ | mới (Master: Booster list "đồng bộ tên") |
| **E. Doc** | | | | | |
| TC-021 | Doc không dùng tên cũ (`buy_resource` / `earn_resource` / `spend_resource`, `max_level`, `remaining_coin`, `iaa_count`…). Lưu ý: `product` thay vì `product_id` trong `Purchase_Success` là lỗi của package `com.titan.iap` (TC-003), không phải lỗi doc | Lỗi doc | ✅ | — | doc_old_name, prop_renamed |
| TC-022 | Doc không liệt kê nhiều booster hơn game có (doc 3 booster, game 2 → báo team data sửa doc, không đánh lỗi game) | Lỗi doc | ✅ | — | doc_more_boosters |
| TC-023 | Event / param / property có trong code thì phải có trong doc | Ngoài plan | ✅ | ✅ | event_out_of_plan, param_out_of_plan, prop_out_of_plan, event_removed_in_doc |
| **F. Lượt chơi — bổ sung theo Master** | | | | | |
| TC-024 | `level_start` bắn ở **mọi** đường bắt đầu (chơi mới, next sau khi thắng, restart từ setting, restart khi thua, vào lại từ Home); `type` đúng ngữ cảnh: 1 chơi mới, 2 restart từ setting, 3 restart khi thua. (TC-007 kiểm giá trị; case này kiểm có bắn ở đủ đường) | Lỗi | ⏳ một phần (qua TC-007) | ✅ | mới (Master) |
| TC-025 | Số đếm trong lượt (`revive`, `booster_N` đã dùng, `exit_index`) chỉ đếm trong lượt đó, reset khi bắt đầu lượt mới | Lỗi | 🆕 cần log giả lập | ✅ | mới (Master) |
| TC-026 | `duration` tính từ lúc thực sự chơi (move đầu), không tính lúc chờ, xem quảng cáo, app ở nền; `percent_complete` trong 0–100. Ví dụ: đứng yên 5s, chơi 3s → `duration = 3` | Lỗi | — | ✅ | mới (Master) |
| TC-027 | `level_exit` chỉ bắn khi rời game giữa level, không bắn sau khi đã có `level_end` | Lỗi | 🆕 | ✅ | mới (Master) |
| TC-028 | `level_unlock` cập nhật khi user **bắt đầu** level mới, không phải lúc thắng level trước | Lỗi | ⏳ một phần (package Titan tự set `level_unlock` từ hàm game cung cấp → kiểm hàm đó trả level nào) | ✅ | mới (Master) |
| TC-029 | Chế độ chơi phụ có cặp start / end (`level_daily_challenge_*`, `level_adventure_*`, `challenge_*`…) theo cùng quy tắc lượt chơi: đúng 1 end mỗi start, có end khi thoát giữa chừng | Lỗi | ⏳ một phần | ✅ | mới (doc các game) |
| **G. Quảng cáo — bổ sung** | | | | | |
| TC-030 | `ads_show` chỉ bắn cho inter / rewarded; `ads_complete.end_type` (quit / done) chỉ cho rewarded (Master ghi rõ). Thứ tự `ads_show` → `ad_impression_*` → `ads_complete` (suy luận, **đã chốt 08/10/2026**) | Lỗi | ⏳ một phần | ✅ | Master + suy luận |
| TC-031 | Thưởng từ quảng cáo phải có `resource_update` type earn, source là quảng cáo (Master: earn tính cả tài nguyên từ quảng cáo). chỉ thưởng khi `end_type = done` (suy luận, **đã chốt 08/10/2026**) | Lỗi | ⏳ một phần | ✅ | Master + suy luận |
| TC-032 | `ad_impression_banner` cộng gộp, bắn khi kết thúc level / session, `count` = tổng impression | Lỗi | ⏳ một phần (bắn từ chỗ kết thúc level / session) | ✅ | mới (Master) |
| **H. IAP** | | | | | |
| TC-033 | Phễu IAP: `iap_show` → `iap_click` → `Purchase_Success` hoặc `iap_failed`; `iap_close` có `iap_duration`; cùng `product_id`, `placement` trong 1 lần mua | Lỗi | ⏳ một phần | ✅ | mới (Master) |
| TC-034 | `Purchase_Success.iap_index` tăng 1 mỗi lần mua; gói có tài nguyên thì mua xong phải có `resource_update` type buy, source = tên gói (gói không có tài nguyên như remove ads → không áp dụng) | Lỗi | 🆕 | ✅ | mới (Master) |
| **I. Tài nguyên** | | | | | |
| TC-035 | **Mọi chỗ code làm đổi coin / booster / lives đều bắn `resource_update`** với type đúng (buy = tiền thật, earn = chơi / quảng cáo, spend = tiêu) | Nghi ngờ (nhận biết biến tài nguyên theo tên) | ⏳ một phần (có: tìm chỗ đổi tài nguyên tương đối không có resource_update trên luồng) | ✅ | resource_change_untracked |
| TC-036 | `lives_infinity` cập nhật khi đổi level (phần sau `resource_update` đã nằm trong TC-009) | Lỗi | ⏳ một phần | ✅ | mới (Master) |
| **J. Tutorial, live ops, tính năng** | | | | | |
| TC-037 | `tutorial.step_name` theo mẫu trong doc của game (Master ví dụ `step_N_start` / `step_N_complete`), đúng thứ tự. mỗi bước chỉ 1 lần, không lặp khi chơi lại (suy luận, **đã chốt 08/10/2026**) | Lỗi | ⏳ một phần | ✅ | Master + suy luận |
| TC-038 | `event_live_ops.type` là start / win / lose; `event_play_index` tăng theo từng event | Lỗi | ⏳ một phần | ✅ | mới (Master) |
| TC-039 | `feature_open` / `feature_close` đi cặp, cùng `open_index`; `open_index` tăng theo từng tính năng; `duration_feature` tính bằng mili giây | Lỗi | ⏳ một phần | ✅ | mới (Master) |
| **K. UA và mở app lần đầu** | | | | | |
| TC-040 | Property UA (network / campaign / adgroup / creative) luôn có giá trị theo quy tắc Unattributed / Unavailable / Organic; ≤ 36 ký tự; `creative` bỏ campaign_id ở đầu | Lỗi | ⏳ một phần (code package Titan) | ✅ | mới (SKILL, Master) |
| TC-041 | `adjust_id` / `user_id` set ngay lần mở đầu; `adjust_data` / `appsflyer_data` bắn lần mở đầu và cập nhật tới khi có giá trị dùng được | Lỗi | ⏳ một phần | ✅ | mới (Master) |
| TC-042 | Không bắn quá 100 lệnh tracking trước khi Firebase sẵn sàng (lúc mở app) — quá thì bị bỏ | Nghi ngờ | ⏳ một phần | ✅ | mới (SKILL) |
| **L. Chung** | | | | | |
| TC-043 | Giá trị thoả cột "Value Requirement" của doc (`>= 0`, `> 0`…) | Lỗi | 🆕 (bộ đọc doc cần đọc thêm cột này) | ✅ | mới (Master) |
| TC-044 | Game không tự bắn lại event mà package Titan đã tự bắn (vd `Purchase_Success` của `com.titan.iap`, `level_unlock`) → tránh đếm 2 lần | Lỗi | 🆕 | ✅ | mới |

Tổng: 44 case.

| | Số case |
|---|---|
| Check all kiểm được ngay (chỉ cần gắn mã) | 14 |
| Check all kiểm được một phần, phần còn lại cần Record | 19 |
| Cần làm loại kiểm tra mới cho Check all | 10 |
| Chỉ kiểm được khi Record (giá trị lúc chạy) | 1 |

Phần suy luận của TC-030, TC-031, TC-037 **đã chốt bật** (08/10/2026).

**Đã bao quát:** mọi event trong doc của bất kỳ game nào (kể cả event riêng như `level_daily_challenge_start`, `event_race`, `button_click`) đều được kiểm ở mức **có bắn, đủ param, đúng tên, đúng kiểu, giá trị hợp lệ** qua TC-001…TC-008 — không cần case riêng cho từng event. Các case còn lại là quy tắc về **nghĩa và hành vi** của Master (lượt chơi, quảng cáo, IAP, tài nguyên, tutorial, live ops, tính năng, UA).

**Chưa bao quát (cố ý):** nghĩa của event riêng từng game (vd `event_race` khi nào bắn) — doc game thường chỉ mô tả bằng lời, nên ghi thành case riêng game `G-xxx` khi QA gặp (qua Record).

**Sau này:** xuất **checklist QA theo game** từ kho case + doc (mỗi case áp dụng cho game đó thành 1 mục: điều kiện, bước, kỳ vọng, ô Thực tế / Kết quả / Mã lỗi) để QA test tay khi cần.

## 7. Duyệt xong sẽ làm

1. Tạo 44 file case; mỗi lỗi trong báo cáo (Unity, HTML, Excel) ghi **mã case** và ô nguồn trong doc.
2. Tab **Theo case** trong cửa sổ Tracking QA (Đạt / Vi phạm / Không áp dụng / Đúng thiết kế / Chỉ kiểm khi Record / Doc chưa rõ).
3. Case riêng game (`G-xxx`) đọc được và chạy được trong Check all — nền cho Record lưu case.
4. Làm các loại kiểm tra mới theo thứ tự giá trị: `resource_change_tracked` (TC-035), `on_action_must_fire` (TC-011), `param_required_when` (TC-019), `no_duplicate_with_package` (TC-044). Phần còn lại làm cùng log giả lập và Record.
5. Test: mỗi case chung có game mẫu bắt được lỗi; bộ 4 game chuẩn không đổi kết quả.

## 8. Cần chốt

1. Cấu trúc 1 case (mục 2) và danh sách loại kiểm tra (mục 3): **đang theo dõi thêm** (08/10/2026) — dùng thử trên các game, chỉnh dần theo tiêu chí phủ nhiều nhất, đúng nhất, hợp lý nhất; chưa đóng băng.
2. Danh sách 44 case ban đầu có cần thêm / bỏ / đổi nhóm case nào không? (Phần suy luận của TC-030, TC-031, TC-037: đã chốt bật.)
3. ~~Ai duyệt case chung (merge PR vào package)?~~ **Đã chốt 08/10/2026**: QA phụ trách package (Dang Quang) duyệt và merge; Claude soạn case, chạy thử trên bộ game chuẩn và review PR trước khi gửi duyệt.
4. ~~Mã: `TC-xxx` cho case chung, `G-xxx` cho case riêng game — ổn không?~~ **Đã chốt 08/10/2026**: `TC-xxx` cho case chung, `G-xxx` cho case riêng game.
