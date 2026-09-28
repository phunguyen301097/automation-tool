# weplan-export (.NET): tự động Download Table trên Weplan Analytics Dashboard

Tool dùng **C# / .NET 8 + Playwright for .NET** để điều khiển trình duyệt thật, chạy theo **kịch bản YAML** (giống automation test):

```
Vào menu  →  chọn quốc gia  →  chọn khoảng thời gian  →  chọn mạng / bộ lọc
          →  chọn "Macro data"  →  chờ data load lên table
          →  bấm "Download table" + chọn loại file  →  lưu file
```

Không cần source code của web: tool thao tác trực tiếp trên giao diện, dựa vào các `id` có sẵn trong HTML
(`#dropdownCountryChooser`, `#datepicker`, `#carrier_filter`, `#byCountry`, `#results`, `#tableProvinces`...).

## 1. Cài đặt

Cần [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build
dotnet run --project src/WeplanExport -- install-browser   # tải Chromium cho Playwright (1 lần)
```

Nếu không muốn tải Chromium, đặt `browser.channel: chrome` (hoặc `msedge`) trong `config.yaml` để dùng trình
duyệt có sẵn trên máy.

Để tạo file `.exe` chạy được trên máy không cài .NET:

```bash
dotnet publish src/WeplanExport -c Release -r win-x64 --self-contained -o publish
# chép config.yaml và thư mục scenarios/ vào cạnh publish/weplan-export.exe
```

Các lệnh bên dưới viết ở dạng `weplan-export ...`. Khi chạy từ source, thay bằng
`dotnet run --project src/WeplanExport -- ...`. Tool đọc `config.yaml` và `scenarios/` trong thư mục hiện tại
(đổi file config bằng `-c đường_dẫn`).

## 2. Đăng nhập (một lần)

```bash
weplan-export login
```

Một cửa sổ trình duyệt sẽ mở ra. Bạn đăng nhập bình thường (có captcha hay 2FA cũng được). Khi thấy dashboard,
quay lại terminal và nhấn **Enter**. Session được lưu vào `.auth/state.json` và các lần chạy sau sẽ dùng lại.
Khi session hết hạn thì chạy lại lệnh này.

> Nếu trang login chỉ có user/password thì có thể đặt biến môi trường `WEPLAN_USERNAME` / `WEPLAN_PASSWORD`,
> tool sẽ tự đăng nhập khi session hết hạn (selector ô nhập nằm ở `auth:`, xem `src/WeplanExport/AppConfig.cs`).

## 3. Chạy

```bash
weplan-export list                          # xem danh sách kịch bản
weplan-export run                           # chạy tất cả scenarios/*.yaml (không gồm scenarios/examples/)
weplan-export run scenarios/coverage_macro.yaml
weplan-export run -k "coverage_*"           # lọc theo tên (glob)
weplan-export run -e "VTB_Sample_*"               # chạy tất cả, TRỪ các kịch bản khớp mẫu (lặp lại -e được)
weplan-export run -t daily                  # lọc theo tag
weplan-export run --headed --slow-mo 300    # xem trình duyệt chạy, chậm lại để quan sát
weplan-export run --trace                   # ghi Playwright trace để debug
weplan-export run --dry-run                 # chỉ in các bước sau khi thay biến
weplan-export steps                         # danh sách các loại bước
```

**Cách chạy:**
- Mặc định trình duyệt chạy **ẩn** (`browser.headless: true`), không hiện cửa sổ nào. Thêm `--headed` nếu muốn xem.
- Tất cả scenario chạy nối tiếp trong **một trang duy nhất** (một cửa sổ khi `--headed`, kiểm tra đăng nhập một
  lần). Scenario nào lỗi giữa chừng thì scenario sau sẽ mở lại trang đầu cho sạch. Muốn mỗi scenario một phiên
  trình duyệt riêng như trước: `--isolated` (hoặc `browser.isolated: true`).
- **Đóng cửa sổ trình duyệt** (khi `--headed`) hoặc bấm **Ctrl+C** trong terminal sẽ **dừng cả lượt chạy**: scenario
  đang chạy ghi `STOPPED`, các scenario còn lại ghi `NOT RUN`, báo cáo vẫn được in và lưu.

**Popup thông báo** (ví dụ "What's New: *New Delta Analysis in Map View*") được **tự đóng**: trước mỗi bước và
bất cứ khi nào popup che thao tác click, tool bấm nút đóng (`×` / Close / OK / Got it...), không được thì bấm Esc,
cuối cùng xoá popup khỏi trang. Log ghi `closed popup '...'`. Session được lưu lại sau khi chạy nên popup đã đóng
thường không hiện lại. Tắt bằng `popups.auto_dismiss: false`; selector nằm ở `Popups` trong
`src/WeplanExport/AppConfig.cs`. Có thể gọi thủ công bằng bước `- dismiss_popups: {}`.

File được lưu vào thư mục `downloads/` (đổi ở `output_dir`). Cuối mỗi lần chạy sẽ in bảng PASS/FAIL, còn
`downloads/_runs/<thời gian>/report.json` chứa chi tiết. Nếu một kịch bản lỗi, tool chụp màn hình và lưu HTML
của trang tại thời điểm lỗi vào cùng thư mục đó, rồi chạy tiếp các kịch bản còn lại (dùng `-x` để dừng ngay).
Exit code là 1 nếu có kịch bản lỗi, nên có thể gắn vào Windows Task Scheduler hoặc cron.

## 4. Viết kịch bản

```yaml
vars:                     # biến dùng chung, gọi bằng ${ten}
  from: max-30d
  to: max

scenarios:
  - name: coverage_burundi_macro
    tags: [daily]
    steps:
      - open_menu: ["Coverage time"]
      - select_country: Burundi
      - set_date: {from: "${from}", to: "${to}"}
      - select_filter: {id: carrier_filter, options: [ECONET, LUMITEL]}
      - choose_view: macro
      - wait_for_table: {}
      - download_table:
          format: xlsx
          filename: "coverage/${country}_${date_from}_${date_to}"

  # matrix: sinh ra 1 lần chạy cho mỗi tổ hợp (ở đây là 2 x 2 = 4 file)
  - name: coverage_${country}_${network}
    matrix:
      country: [Burundi, Cambodia]
      network: [ECONET, LUMITEL]
    steps: [...]
```

**Mỗi scenario là một lần chạy độc lập** (mở menu, chọn quốc gia, ngày, filter... rồi tải file), còn mỗi tổ hợp
trong `matrix` là một scenario riêng. Trước khi chạy, tool in danh sách scenario sẽ chạy. Muốn chạy ít hơn thì chỉ
định file, lọc bằng `-k` / `-t`, hoặc thêm `skip: true` vào scenario không cần.

Mức file còn có `before:` và `after:`: các bước chèn vào đầu và cuối mọi scenario. Thêm `skip: true` để tạm
bỏ qua một scenario.

### Các bước chính

| Bước | Ý nghĩa | Ví dụ |
|---|---|---|
| `open_menu` | Bấm menu sidebar (nhiều cấp) hoặc đi thẳng tới URL | `["Latency", "Latency Mobile (Cellular)"]`, `/app/bi/signal` |
| `goto` | Mở một đường dẫn | `/app/bi/coverage` |
| `select_country` | Chọn quốc gia theo tên hoặc mã | `Burundi`, `kh` |
| `set_date` | Chọn khoảng thời gian | `{from: 2026-08-01, to: 2026-08-31}` |
| `select_filter` | Chọn giá trị cho một ô select (theo `id`), nhận text hoặc value | `{id: carrier_filter, options: [ECONET]}` |
| `filters` | Chọn nhiều bộ lọc cùng lúc | `{carrier_filter: [ECONET], coverage_filter: ["4G"]}` |
| `choose_view` | Bấm thẻ visualization | `macro`, `admin_1`, `population_range`, `{text: "By provinces"}` |
| `apply` | Bấm nút "Parameters changed. Click here to execute new query" nếu nó hiện | `{}` |
| `wait_for_table` | Chờ `#results` hiện và table có dòng; báo lỗi nếu dashboard hiện lỗi | `{min_rows: 1, timeout: 300000}` |
| `download_table` | Bấm "Download table", chọn loại file, lưu và kiểm tra file | `{format: xlsx}` / `{format: csv}` / `{format_text: "Excel"}` |
| `click`, `fill`, `press`, `wait`, `wait_for`, `screenshot`, `js`, `pause` | Thao tác tự do | `click: {text: "Macro data"}` |

**Chọn tất cả** một filter (nút *Select All*): `select_filter: {id: carrier_filter, all: true}`.

**Matrix với giá trị dạng nhóm**: mỗi giá trị có thể là một mapping, dùng lại bằng `${ten.khoa}`:

```yaml
matrix:
  tech:
    - {name: 5G, coverage: [5G_SA, 5G_NSA_CONNECTED, 5G_NSA_NOT_RESTRICTED, 5G_NSA_RESTRICTED]}
    - {name: 4G, coverage: [4G]}
steps:
  - select_filter: {id: coverage_filter, options: "${tech.coverage}"}
  - download_table: {format: xlsx, filename: "VTB_${year}_T${month}_Coverage time_Net_${tech.name}"}
```

**Thị trường**: mọi KPI chạy cho từng thị trường khai báo trong `config.yaml` (`vars.markets`):

| Mã | Quốc gia | `country` (mã trong ô chọn quốc gia) |
|---|---|---|
| VTC | Cambodia | kh |
| STL | Laos | la |
| VTL | Timor-Leste | tl |
| MYN | Myanmar | mm |
| VTB | Burundi | bi |
| VTZ | Tanzania | tz |
| MVT | Mozambique | mz |
| NCM | Haiti | ht |

Tool chạy **từng trang (KPI) cho cả 8 thị trường** rồi mới sang trang khác (Coverage time của VTC, STL,
... NCM, rồi Signal strength của VTC...). Muốn chạy hết mọi trang của một nước rồi mới sang nước khác:
`--order market` (hoặc `run_order: market` trong `config.yaml`). File lưu vào `downloads/<mã>/`.
Mã quốc gia nằm trong địa chỉ trang (`/app/kh/coverage` là Cambodia, `/app/bi/coverage` là Burundi), nên
mỗi kịch bản mở trang bằng `/app/<country>/<trang>` rồi kiểm tra ô quốc gia. Trước khi tải file, tool kiểm tra
lại quốc gia đang hiển thị; nếu khác thị trường của kịch bản thì dừng kịch bản đó, không lưu file sai. Chạy một thị trường: `-k "VTC_*"`; một KPI của một thị trường:
`run scenarios/latency.yaml -k "VTZ_*"`. Bỏ / thêm thị trường: sửa danh sách trong `config.yaml`.
Nếu bảng không có dữ liệu (ví dụ thị trường chưa có 5G), sau 20 giây tool ghi cảnh báo
`table is empty` và vẫn tải file.

Các kịch bản hàng tháng theo `Weplan_export.docx` (mỗi KPI: mức toàn mạng + mức tỉnh, xuất As XLSX,
tên `<mã>_<năm>_T<tháng>_<KPI>_<Net|Province>_<công nghệ>`, số file tính cho mỗi thị trường):

| File | Trang | Tách theo | Số file |
|---|---|---|---|
| `coverage_time.yaml` | Coverage time | Coverage type All/5G/4G | 6 |
| `signal_strength.yaml` | Signal & Quality → Average signal level | Coverage type All/5G/4G | 6 |
| `data_traffic.yaml` | Data traffic | Coverage type All/5G/4G | 6 |
| `latency.yaml` | Latency → Latency Mobile (Cellular) | Coverage type All/5G/4G | 6 |
| `packet_loss.yaml` | Latency → Packet Loss Mobile (Cellular) | Coverage type All/5G/4G | 6 |
| `throughput.yaml` | Throughput → Mobile (Cellular) | Coverage type All/5G/4G | 6 |
| `mobile_quality_score.yaml` | Mobile Quality Score | Excellent / Sufficient / Insufficient | 6 |
| `sample.yaml` | Sample | – (All) | 2 |
| `network_availability.yaml` | Network availability → Mobile (Cellular) | Coverage type All/5G/4G | 6 |
| `topology_stock.yaml` | Topology Stock (không chọn Date) | Technology NR/LTE/UMTS/GSM | 8 |
| `speed_test.yaml` | Speed test → Mobile (Cellular) → Throughput | Coverage type All/5G/4G | 6 |
| `web_performance.yaml` | Web performance → Mobile (Cellular) → Times (Time to first byte) | Coverage type All/5G/4G | 6 |
| `video_streaming.yaml` | Video Streaming → Mobile (Cellular) → Times (Video start time) | Coverage type All/5G/4G | 6 |

Chạy riêng một KPI: `run scenarios/latency.yaml` hoặc theo tag (`-t latency`); chạy tất cả: `run` hoặc `-t monthly`.

Filter không có `id` cố định có thể chọn theo **nhãn hiển thị**: `select_filter: {label: "Technology", options: [NR]}`
(bỏ qua phần đếm như "(1 active)"). Với trang không chọn Date, `${year}`/`${month}` mặc định là tháng trước.

**Ngày** nhận các dạng `2026-08-01`, `01/08/2026`, `today`, `today-7d`, `max` (ngày mới nhất có dữ liệu,
lấy từ `window.dateLimits` của trang), `max-30d`, `max-1m`, `min`.

**Tên file** có thể dùng các biến `${scenario}`, `${country}`, `${country_code}`, `${date_from}`, `${date_to}`,
`${year}`, `${month}` (8), `${month2}` (08), `${timestamp}`, `${rows}` và mọi biến trong `vars`/`matrix`. Năm/tháng lấy
từ ngày bắt đầu của khoảng đã chọn (kể cả khi chọn preset như `Last month`). Khoảng trắng được giữ nguyên, chỉ
ký tự Windows không cho phép (`<>:"/\\|?*`) bị thay bằng `_`. Dấu `/` tạo thư mục con. Phần đuôi file được lấy
theo file server trả về.

**Id các bộ lọc** (lấy từ HTML trang Coverage): `carrier_filter` (Cellular network), `coverage_filter`,
`origin_filter`, `geography_filter`, `connection_filter`, `signal_filter`, `popRange_filter`, `location_status`,
`clients_filter`, `screen_on_filter`, các bộ lọc Topology (`zone_filter`, `frequency_band_filter`, `vendor_filter`...),
Device (`manufacturer_filter`, `brand_filter`, `model_filter`...), Wi‑Fi (`wifi_isp_filter`...), thời gian
(`dayOfWeek_filter`, `hour_filter`), Grouping `group_selector`, X Axis `xaxis_selector`, Measurement type `macroMode`.

## 5. Khi giao diện khác với dự kiến

Tool được xây dựng từ HTML gốc của trang (trước khi JavaScript chạy). Hai phần do JavaScript vẽ ra sau nên
**cần xác nhận trên web thật ở lần chạy đầu**:

1. **Ô chọn ngày** (`#datepicker`). Trên dashboard, bấm vào ô sẽ mở popup 2 lịch tháng (`Aug 2026 | Sep 2026`,
   nút `<` `>`); bấm ngày bắt đầu rồi ngày kết thúc thì popup tự đóng. Với `date.mode: auto` (mặc định) tool làm
   đúng như vậy: mở popup, đọc tiêu đề tháng/năm **theo chữ trên màn hình** (không phụ thuộc tên class của thư
   viện lịch), bấm `<`/`>` tới đúng tháng, bấm ngày bắt đầu và kết thúc, rồi kiểm tra ô ngày hiển thị đúng
   `dd-mm-yyyy - dd-mm-yyyy` (sai thì báo lỗi, tránh tải nhầm khoảng thời gian). Ngày bị gạch (sau ngày có dữ
   liệu mới nhất) sẽ báo lỗi rõ ràng. Có thể dùng mốc có sẵn trong popup: `set_date: {preset: "Last 30 days"}`
   (`Last 7 days`, `Current month`, `Last month`, `Last 3 months`...). Nếu không nhận ra được, HTML của ô ngày
   được lưu vào `downloads/_debug/`; tạm thời có thể dùng `set_date: {mode: skip}`.
2. **Nút "Download table" và menu chọn loại file** (`As XLSX / As JSON / As CSV / As PDF / As TXT / As PNG`).
   `format: xlsx|json|csv|pdf|txt|png` chọn đúng mục tương ứng. Nếu chữ trên web khác thì chỉnh
   `download_button_text`, hoặc `format_text` trong bước `download_table`.

**Lệnh `inspect`**: mở dashboard bằng session đã lưu, rồi ghi HTML (đã render) và ảnh chụp của: toàn trang,
ô chọn ngày, trang sau khi mở ô ngày, vùng kết quả sau khi bấm Macro data, và menu "Download table":

```bash
weplan-export inspect            # thêm --headed để xem trình duyệt
```

Kết quả nằm ở `downloads/_inspect/<thời gian>/`. Nén thư mục đó gửi lại là đủ để chỉnh selector.

**File tải tay trong cửa sổ do tool mở**: Playwright lưu file tải về dưới tên dạng GUID trong thư mục tạm và xóa
khi đóng trình duyệt. Trong lệnh `login` và `inspect`, file bạn tự bấm tải được lưu vào `downloads/manual/` với
tên gốc. Khi chạy `run`, file được lưu theo `filename` của bước `download_table`.

Cách tìm selector đúng trên web thật:

```bash
weplan-export run -k tên_kịch_bản --headed   # thêm bước `- pause: {}` vào chỗ cần xem
pwsh src/WeplanExport/bin/Debug/net8.0/playwright.ps1 codegen --load-storage .auth/state.json https://dashboard.weplananalytics.com/app/bi/coverage
```

`codegen` ghi lại các thao tác bạn click và in ra selector tương ứng. Selector đó dùng được ngay trong các bước
`click: {selector: "..."}`, hoặc bạn có thể sửa `selectors:` trong `config.yaml`.

Các ô chọn (bootstrap-select) mặc định được đặt giá trị qua JavaScript rồi phát sự kiện `change`, vì cách này ổn
định hơn. Nếu trang không nhận thay đổi, thêm `mode: ui` để tool bấm chọn như người dùng:
`select_filter: {id: carrier_filter, options: [ECONET], mode: ui}`.

## 6. Kiểm thử

`tests/mock_site/index.html` mô phỏng cấu trúc DOM của dashboard (menu nhiều cấp, đổi quốc gia, date, filter
tải bất đồng bộ, thẻ Macro data, table load chậm, nút Download table có menu Excel/CSV). Các test xUnit chạy toàn
bộ luồng trên trang mô phỏng này:

```bash
dotnet test
```

## Cấu trúc

```
WeplanExport.sln
config.yaml                       cấu hình (URL, timeout, selector, định dạng ngày)
scenarios/*.yaml                  kịch bản (cùng định dạng với bản Python)
src/WeplanExport/
  Program.cs                      lệnh login / install-browser / run / list / steps
  AppConfig.cs                    cấu hình và giá trị mặc định
  Scenarios.cs                    đọc kịch bản, vars, matrix, before/after
  Steps.cs                        các bước (menu, country, date, filter, view, wait, download...)
  DateParser.cs                   ngày tuyệt đối / tương đối (max-30d...)
  Runner.cs                       mở trình duyệt, chạy kịch bản, báo cáo
tests/WeplanExport.Tests/         test xUnit (mock server + trang mô phỏng)
```
