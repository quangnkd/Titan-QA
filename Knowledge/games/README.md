# Kiến thức riêng từng game

Mỗi game 1 thư mục, tên = **bundle id Android** của game (vd `com.no1ornothing.bloom.tile.match.puzzle`; không có thì bundle id iOS, không có nữa thì tên game viết liền). App / package tự nhận game đang mở và dùng `common/` + đúng thư mục của game đó. Game chưa có thư mục vẫn dùng được ngay với `common/`.

Trong thư mục của 1 game có thể có:

| File | Nội dung |
|---|---|
| `exceptions.json` | Mục QA đã đánh dấu **"Đúng thiết kế"** cho game này (có lý do, người, ngày) — không tính là lỗi nữa. Chỉ áp dụng cho game này. |
| `docs/<tên file doc>.json` | Chỉnh sửa doc QA đã xác nhận (bỏ qua event/param, sửa kiểu, sửa giá trị hợp lệ) — theo tên file Excel, máy nào cũng dùng được. |
| `tracking-rules.json`, `code-patterns.json`, `sdk-apis.json`, `actions.json` | Ghi đè / bổ sung kiến thức chung **chỉ cho game này** (vd game dùng hàm bọc tracking riêng, tên nút đặc biệt). Chỉ cần ghi phần khác — phần còn lại lấy từ `common/`. |
| `tracking-review/SKILL.md` | Ghi chú riêng cho Claude khi review game này (nối sau kiến thức chung). |
| `cases/G-xxx.json` | **Case riêng của game** (cùng cấu trúc case chung `common/cases/TC-xxx.json`, xem `docs/kho-case.md` của package). Lỗi khớp case này ghi mã `G-xxx` trước mã `TC-xxx`. Cùng mã với case chung thì thay case chung cho riêng game này. |

Nguyên tắc:
- Điều gì đúng cho **mọi** game → `common/` (phải chạy thử trên tất cả game chuẩn + có người duyệt).
- Điều chỉ đúng cho **1** game → thư mục của game đó (người duyệt xem nhanh, không ảnh hưởng game khác).
- Thấy cùng 1 ngoại lệ ở nhiều game → dấu hiệu nên đưa lên `common/` (hoặc là lỗi chung của package Titan).

Chỉnh sửa trong app được lưu **trên máy** trước (`%APPDATA%\TrackingChecker\knowledge-local`), bấm **Gửi lên kho chung** để chép vào thư mục kho kiến thức (bản git này) rồi commit / tạo PR.
