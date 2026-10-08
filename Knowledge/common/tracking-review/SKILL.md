---
name: tracking-review
description: Kiến thức chung của studio để review tracking Firebase trong game Unity (package com.titan.tracking, Master Event Tracking Plan, các lỗi hay gặp). Dùng khi kiểm tra một game có bắn tracking đúng và đủ theo tracking plan không.
version: 1.0.0
updated: 2026-10-07
---

# Tracking review — kiến thức chung của studio

Bộ kiến thức này dùng chung cho MỌI game. Khi review, đọc theo đúng chuẩn dưới đây để kết luận giữa các game nhất quán. Doc của từng game luôn là chuẩn chính; Master dùng để hiểu ý nghĩa khi doc game ghi thiếu.

## 1. Đường đi của tracking trong game Titan

- Mọi game dùng package `com.titan.tracking`. Code game KHÔNG gọi Firebase trực tiếp mà gọi `Titan.Tracking.TrackingManager.Instance.*`:
  - `TrackEvent(name, params ITrackingParam[])` — event tuỳ ý.
  - `TrackLevelStart / TrackLevelPlay / TrackLevelEnd / TrackLevelExit` — mỗi tham số int của hàm thành 1 param cùng tên (`level` → `level_id`), `parameters` là param bổ sung (booster_1..n, param riêng game).
  - `TrackTutorial(step_id, step_name)`, `TrackEventLiveOps(name, type, event_level)`.
  - `TrackResourceUpdate(type, source, source_id, items[])` → bắn **mỗi item 1 event `resource_update`** VÀ **1 event `view_item`** gom items.
  - `TrackProperty(name, value)` — user property (luôn là string).
  - `InitGetLevelUnlock*` — package tự set `level_unlock` mỗi lần có event.
- Lớp chuyển tiếp `Titan.Firebase_Analytic.FirebaseAnalytic`:
  - Chỉ chạy trên thiết bị (`Application.isMobilePlatform`) — Editor không bắn gì lên Firebase.
  - Event trùng tên trong cùng 1 frame bị dời sang frame sau (thứ tự giữ nguyên); `ad_impression` đi thẳng không qua hàng đợi.
  - Hàng đợi trước khi Firebase init tối đa 100 lệnh — quá thì bị bỏ (rủi ro ở lúc mở app).
  - Tự set user property `Group` (0-9, A/B test) và `SetUserId(GAID)`.
  - Event có chữ `level` / `lv_` trên thiết bị được tự thêm param `fps` → tính vào giới hạn 25 param.
- Param: `ParamInt` → int (long), `ParamFloat` → double, `ParamString` → string. Lớp con dùng `const KEY` (vd `ParamLevel` → `level_id`). `ParamEndLevelType` gửi `win` dạng **string** (khác `TrackLevelEnd` gửi int).
- Package khác cũng bắn tracking: `com.titan.ads` (ad_impression*, ads_show, ads_click, ads_complete, ad_request, user property reward_count/inter_count), `com.titan.iap` (Purchase_Success, iap_count), Falcon (log revenue động theo remote config).

## 2. Ý nghĩa chuẩn theo Master Event Tracking Plan

- `level_end.win`: 0 = thua; 1 = thắng; 2 = thoát game đột ngột (kill app, hoặc thoát rồi vào lại mà không resume level); 3 = ấn replay; 4 = ấn nút home.
  - Một số doc game cũ đánh số khác (vd 2 = replay, 3 = thoát đột ngột). Luôn so với doc của game đang check và chỉ ra chỗ lệch nghĩa giữa code và doc.
- `level_start.type`: 1 = start; 2 = restart từ nút restart trong setting; 3 = restart khi thua.
- `level_play.type`: 1 = move đầu tiên; 2/5/8 = đạt ~20/50/80% hoàn thành; 0 = stuck/hết nước đi. Mỗi type chỉ bắn 1 lần trong 1 lượt chơi.
- `level_exit`: user rời game giữa level (không bắn khi rời sau khi đã thắng/thua); `exit_index` reset mỗi lần start level.
- Mỗi lượt chơi có đúng 1 `level_start` và 1 `level_end`; `play_index` của 2 event phải khớp; `level_id` là level ĐANG chơi (không phải level kế tiếp).
- `resource_update.type` = buy / earn / spend; `value` luôn dương; `remaining_value` = số dư sau thay đổi.
- User property cập nhật NGAY SAU event tương ứng: coin/lives/booster_N sau `resource_update`; `iap_count` sau `Purchase_Success`; `reward_count` sau `ad_impression_rewarded`; `inter_count` sau `ad_impression_inter`; `level_unlock` khi user thực sự bắt đầu chơi level mới.
- Booster: tên tracking cố định `booster_1..booster_n` theo tab Booster list. Game có ít booster hơn doc → lỗi doc, không phải lỗi code.
- Tên cũ ↔ tên hiện tại: buy/earn/spend_resource → resource_update + view_item; max_level → level_unlock; iaa_count / rewarded_ad_count → reward_count; inter_ad_count → inter_count; remaining_coin → coin; adjust_data ↔ appsflyer_data (tuỳ MMP).

## 3. Hướng dẫn đặc biệt của studio

**level_end khi user thoát giữa level** — phải log đủ, cách chuẩn:
1. `level_start` → lưu "pending level_end" cục bộ.
2. App xuống nền (`OnApplicationPause(true)`) → lưu timestamp.
3. Quay lại trong ~30 phút và được chơi tiếp → huỷ pending; không được chơi tiếp hoặc quá 30 phút → bắn `level_end win=2`.
4. Kill app → lần mở app sau kiểm tra pending → bắn `level_end win=2`.
5. Uninstall → không bắn được từ client (chấp nhận).
Dấu hiệu lỗi hay gặp: cờ "đang chơi" (PlayerPrefs) được đọc khi mở app nhưng không bao giờ được set; chỉ bắn `level_exit` mà không có `level_end win=2`.

**User property UA (Adjust / AppsFlyer)** — mọi property network/campaign/adgroup/creative luôn có giá trị:
- Chưa có callback → "Unattributed"; callback đến thì ghi đè.
- Paid (network/media_source không chứa "Organic") → field thiếu = "Unavailable".
- Organic → field thiếu = giá trị network/media_source.
- Firebase: cắt giá trị cho vừa 36 ký tự; không log `ua_tracker_name`.

## 4. Giới hạn Firebase (vi phạm = dữ liệu bị mất âm thầm)

- Tên event ≤ 40 ký tự, tên param ≤ 40, tối đa 25 param/event (tính cả `fps` tự thêm), giá trị string ≤ 100 ký tự.
- Tên user property ≤ 24 ký tự, giá trị ≤ 36 ký tự.
- Tiền tố bị cấm: `firebase_`, `google_`, `ga_`.
- Gửi sai kiểu (string cho int) → cột int_value trong BigQuery rỗng, query sai.

## 5. Lỗi hay gặp — kiểm tra từng mục

1. `level_end` bắn 2 lần cho 1 lượt (vd thua → bấm Home từ popup mất streak: LoseGame + RestartGame/LogEnd(3)).
2. Thiếu `level_end win=2` (thoát đột ngột) hoặc win=2 bị dùng sai nghĩa.
3. `level_id` bị tăng (level++) TRƯỚC khi bắn `level_end` thắng → lệch 1 level.
4. `level_exit` bắn sai khi quảng cáo toàn màn hình làm app pause giữa level.
5. Banner `ad_impression` thiếu / null `ad_placement` (MAX banner không set placement).
6. `Purchase_Success` của package iap dùng param `product` thay vì `product_id`, thiếu `placement`.
7. `ads_show.status` gửi "not_ready"/"no_ad_unit" thay vì "success" / "fail by …".
8. Param dựng trong vòng lặp (action_seq_1..N) làm vượt 25 param ở level dài.
9. Hàm tracking không bao giờ được gọi (tutorial, buy_resource…) — code chết.
10. Property set TRƯỚC event cập nhật nó.
11. Code tracking nằm trong `#if` không bật cho Android/iOS, hoặc chỉ chạy trong cheat/debug.
12. Event bắn từ code cheat (Cheat.cs, menu debug) — không phải luồng thật của user.

## 6. Cách review và kết luận

- Đọc code thật trước khi kết luận; dẫn tên hàm và file:dòng.
- Với mục "Nghi ngờ" của bộ luật tĩnh: `confirmed` = đúng là lỗi; `rejected` = báo nhầm (nói rõ vì sao); `uncertain` = cần chạy thật (nói cần test case nào).
- Với mục "Độ phủ / điểm mù": mở từng vị trí, xác định đó có phải luồng gửi tracking app chưa hiểu không. Nếu có → báo phát hiện mới (CodeError/Suspect) mô tả luồng đó; nếu không → ghi rõ là chuỗi dùng cho việc khác.
- Không lặp lại điều bộ luật tĩnh đã báo. Phân biệt rõ lỗi code / lỗi doc / lỗi trong package dùng chung (sửa 1 lần cho mọi game).
- Viết tiếng Việt, ngắn gọn, dùng tên event/param nguyên văn.
