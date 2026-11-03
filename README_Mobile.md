> Historical frontend planning document. Its endpoint examples describe the previous client; use [current backend documentation](Backend/README.md) for EZone 2.0.

# EZone Mobile — Tài liệu kỹ thuật (as-built)

> File này trước đây là bản **kế hoạch triển khai** (task list trước khi code).
> Toàn bộ đã triển khai xong — nội dung dưới đây mô tả **đúng những gì đã làm
> thật trong code**, dùng để viết report/slide phần Mobile Frontend.

Ứng dụng **EZone** — chat ẩn danh dành cho sinh viên EIU: match ngẫu nhiên với
1 sinh viên khác, trò chuyện ẩn danh, có thể đồng ý lộ danh tính sau khi chat
đủ lâu, báo cáo vi phạm, xem lại lịch sử match.

---

## 1. Tech stack

| Thành phần | Công nghệ | Ghi chú |
|---|---|---|
| Framework | **React Native 0.86.0** (React 19.2.3) | Dùng **React Native CLI thuần**, không dùng Expo |
| Điều hướng | `@react-navigation/native` + `@react-navigation/native-stack` | Native Stack Navigator, 21 screen trong 1 Stack duy nhất |
| Real-time | `@microsoft/signalr` | Client kết nối `ChatHub` bên backend (match, chat, typing) |
| Lưu session | `@react-native-async-storage/async-storage` | Lưu JWT token, userId, fullname, roles |
| Upload ảnh | `react-native-image-picker` | Chọn ảnh từ thư viện để đổi avatar |
| Giải mã JWT | `base-64` | Tự decode payload JWT (không cần lib jwt-decode ngoài) |
| Ngôn ngữ | JavaScript (`.jsx`), có cấu hình TypeScript sẵn nhưng không dùng triệt để | |
| Lint | ESLint (`@react-native/eslint-config`) | |
| Test | Jest + `react-test-renderer` | Hiện chỉ có test mặc định của RN CLI, xem mục 9 |

## 2. Cấu trúc thư mục thực tế

```
Mobile/
├── android/ , ios/              # Native project (RN CLI tự sinh)
├── App.jsx                      # Root component — enableScreens(false) + <AppNavigator/>
├── index.js                     # Entry point RN
├── src/
│   ├── screens/                 # 21 màn hình — mỗi file 1 screen (xem mục 4)
│   ├── components/              # UI dùng lại nhiều nơi (xem mục 5)
│   ├── navigation/
│   │   └── AppNavigator.jsx     # Khai báo toàn bộ 21 route trong 1 Native Stack
│   ├── services/                # Toàn bộ code gọi API/SignalR/storage (xem mục 6)
│   │   ├── api.js                # apiRequest() dùng chung + mọi hàm gọi REST API
│   │   ├── chatService.js        # Tạo SignalR HubConnection
│   │   ├── revealApi.js          # 3 hàm gọi RevealController
│   │   └── storage.js            # Quản lý session trong AsyncStorage
│   ├── utils/
│   │   └── jwt.js                # Tự decode JWT, check còn hạn không (không gọi API)
│   └── constants/
│       ├── theme.js               # colors, spacing, cartoonShadow, fonts (toàn bộ monospace)
│       └── config.js              # BASE_URL / API_BASE_URL / HUB_URL
└── __tests__/App.test.tsx        # Test mặc định RN CLI
```

> `src/api/`, `src/context/`, `src/assets/` còn tồn tại (file `.gitkeep`) từ
> bản kế hoạch ban đầu nhưng **không dùng tới** trong bản triển khai thật —
> logic gọi API dồn hết vào `src/services/`, session quản lý trực tiếp qua
> AsyncStorage (`storage.js`) thay vì dùng React Context riêng.

## 3. Chạy project

```bash
cd Mobile
npm install
npx react-native start                              # Metro bundler
npx react-native run-android --device=<emulator-id>  # Cài & chạy (terminal khác)
```

Cấu hình base URL trong `src/constants/config.js`:
```js
export const BASE_URL = 'http://10.0.2.2:5044';  // 10.0.2.2 = alias localhost cho Android Emulator
export const API_BASE_URL = `${BASE_URL}/api`;
export const HUB_URL = `${BASE_URL}/chatHub`;
```
Chạy trên điện thoại thật: đổi `BASE_URL` thành IP LAN thật của máy chạy backend.

---

## 4. Danh sách 21 screens — vai trò từng màn

| # | Route (trong `AppNavigator`) | File | Vai trò | API/Service gọi |
|---|---|---|---|---|
| 1 | `Splash` | `SplashScreen.jsx` | Màn khởi động — tự kiểm tra JWT còn hạn (`isTokenValid`, giải mã cục bộ, **không gọi API**) rồi tự chuyển `Home` hoặc `Login` | `hasValidSession()` |
| 2 | `Login` | `LoginScreen.jsx` | Form đăng nhập Email/Password, lưu session sau khi login | `login()` |
| 3 | `Register` | `RegisterScreen.jsx` | Form đăng ký tài khoản mới (Fullname/Email/Major/Password) | `registerUser()` |
| 4 | `ActivateAccount` | `ActivateAccountScreen.jsx` | Nhập email + mã kích hoạt 6 số (gửi qua email) để kích hoạt tài khoản | `activateAccount()` |
| 5 | `Home` | `HomeScreen.jsx` | Màn trung tâm sau khi login — hiển thị nickname/userId, các lối vào: Find Match, My Profile, Chat History, My Reports, Rules/About, Settings, **Admin Reports (chỉ hiện nếu role có "Admin")**, Log Out | `getSession()` |
| 6 | `RulesAbout` | `RulesAboutScreen.jsx` | Nội dung tĩnh: quy tắc ứng xử trong app | — |
| 7 | `About` | `AboutScreen.jsx` | Nội dung tĩnh: giới thiệu app + version + công nghệ dùng | — |
| 8 | `Waiting` | `WaitingScreen.jsx` | Kết nối SignalR, gọi `FindMatch`, hiện hiệu ứng chờ (đếm giờ chờ + số người online giả lập) cho tới khi nhận event `Matched` | SignalR `FindMatch` |
| 9 | `MatchSuccess` | `MatchSuccessScreen.jsx` | Hiệu ứng chúc mừng match thành công (animation), tự động chuyển sang `ChatRoom` sau 1.2s | — |
| 10 | `ChatRoom` | `ChatRoomScreen.jsx` | **Màn core** — nạp lại lịch sử tin nhắn (`getMessages`), kết nối SignalR (gửi/nhận tin real-time, typing indicator), yêu cầu Reveal danh tính, điều hướng sang Report/Logout, rời phòng (Leave) | `getMessages()`, SignalR (`JoinRoom`/`SendMessage`/`Typing`/`LeaveRoom`), `revealApi.js` |
| 11 | `ReportUser` | `ReportUserScreen.jsx` | Form báo cáo vi phạm (chọn lý do + mô tả tối thiểu 10 ký tự nếu có nhập) | `createReport()` |
| 12 | `Profile` | `ProfileScreen.jsx` | Xem hồ sơ cá nhân (chỉ xem — tự refetch mỗi lần quay lại màn qua `useFocusEffect`), đổi avatar tại chỗ | `getMyProfile()`, `updateProfile()` (avatar) |
| 13 | `EditProfile` | `EditProfileScreen.jsx` | Form sửa Fullname/Major/Gender/SocialLink (tách riêng khỏi màn xem Profile) | `updateProfile()` |
| 14 | `LogoutConfirm` | `LogoutConfirmScreen.jsx` | Màn xác nhận đăng xuất (không phải modal) | `logoutRequest()`, `clearSession()` |
| 15 | `ChatHistory` | `ChatHistoryScreen.jsx` | Danh sách các phòng chat **đã kết thúc** (`Status=Closed`) — List screen | `getChatHistory()` |
| 16 | `ChatRoomDetail` | `ChatRoomDetailScreen.jsx` | Chi tiết 1 phòng đã kết thúc: trạng thái, thời gian, Affinity Score, đã reveal chưa — Detail screen | `getChatRoomDetail()` |
| 17 | `MyReports` | `MyReportsScreen.jsx` | Danh sách report **do chính user gửi**, xem trạng thái xử lý (Pending/Resolved) | `getMyReports()` |
| 18 | `Settings` | `SettingsScreen.jsx` | Toggle Notifications/Sound (lưu AsyncStorage cục bộ), lối tắt Edit Profile, lối vào Delete Account | AsyncStorage local |
| 19 | `DeleteAccount` | `DeleteAccountScreen.jsx` | Bắt nhập lại mật khẩu + xác nhận 2 lớp trước khi xoá tài khoản (soft delete) | `deleteAccount()` |
| 20 | `AdminReportList` | `AdminReportListScreen.jsx` | *(chỉ Admin)* Danh sách toàn bộ report — filter theo status, sort, phân trang, action Ban/Delete nhanh | `getAdminReports()`, `banReportedUser()`, `deleteReport()` |
| 21 | `AdminReportDetail` | `AdminReportDetailScreen.jsx` | *(chỉ Admin)* Chi tiết 1 report, đổi trạng thái xử lý (Update data API), Ban nhanh | `updateReportStatus()`, `banReportedUser()` |

### Luồng điều hướng chính

```
Splash ─┬─(có session)──> Home
        └─(chưa login)──> Login ──> Register ──> ActivateAccount ──> Login

Home ──> Waiting ──(matched)──> MatchSuccess ──(auto)──> ChatRoom
                                                              ├──> ReportUser
                                                              ├──> (Reveal modal — IdentityRevealedScreen)
                                                              └──(Leave)──> Waiting

Home ──> Profile ──> EditProfile
Home ──> ChatHistory ──> ChatRoomDetail
Home ──> MyReports
Home ──> Settings ──> DeleteAccount
Home ──> AdminReportList ──> AdminReportDetail   (chỉ role Admin)
Home / Profile ──> LogoutConfirm ──> Login
```

---

## 5. Components dùng chung (`src/components/`)

| Component | Vai trò |
|---|---|
| `Screen.jsx` | Wrapper chuẩn cho mọi screen — `SafeAreaView` + `ScrollView` tuỳ chọn (`scroll={false}` cho screen có `FlatList` riêng, tránh lỗi nested VirtualizedList) |
| `CartoonButton.jsx` | Nút bấm dùng chung toàn app — 3 variant `primary`/`secondary`/`danger`, có `loading` state (ActivityIndicator) |
| `InfoCard.jsx` | Card bo góc viền đậm dùng chung (style "cartoon") |
| `TextInputField.jsx` | Input có label + hiện lỗi validate |
| `MessageBubble.jsx` | Bong bóng tin nhắn chat — style khác nhau cho tin của mình (`isOwn`) và của đối phương |
| `TypingIndicator.jsx` | Hiện "Typing..." khi đối phương đang gõ |
| `IdentityRevealedScreen.jsx` | **Thực chất là Modal** (đặt tên gây nhầm là "Screen" nhưng không phải route) — hiện thông tin thật của đối phương sau khi cả 2 bên đồng ý Reveal, dùng trong `ChatRoomScreen` |
| `LogoutConfirmationDialog.jsx` | ⚠️ **Dead code** — modal xác nhận logout kiểu cũ, đã bị thay bằng screen `LogoutConfirmScreen` (route riêng), không còn được import ở đâu cả. Giữ lại file nhưng không dùng. |

---

## 6. Services layer (`src/services/`)

### `api.js` — trung tâm gọi REST API
- `apiRequest(endpoint, options)`: hàm gốc mọi API khác đều gọi qua đây —
  tự lấy JWT từ `storage.js` và đính `Authorization: Bearer <token>`, tự
  phân biệt body là `FormData` (upload avatar) hay JSON thường để không đè
  sai `Content-Type`, tự parse lỗi từ response backend thành `Error` ném ra
  cho screen bắt bằng `try/catch`.
- Toàn bộ hàm nghiệp vụ export từ đây: `login`, `registerUser`,
  `activateAccount`, `getMyProfile`, `updateProfile`, `deleteAccount`,
  `logoutRequest`, `getChatHistory`, `getChatRoomDetail`, `getMessages`,
  `createReport`, `getMyReports`, `getAdminReports`, `updateReportStatus`,
  `banReportedUser`, `deleteReport`.
- `ROOM_STATUS_LABELS`: map số → tên trạng thái phòng (backend trả
  `ChatRooms.Status` dạng số vì không cấu hình `JsonStringEnumConverter`).

### `chatService.js`
- `createChatConnection(userId)`: tạo `HubConnection` SignalR trỏ tới
  `HUB_URL?userId=<id>`, bật `withAutomaticReconnect()`.

### `revealApi.js`
- `requestReveal`, `getRevealStatus`, `getRevealedIdentity` — gọi 3
  endpoint của `RevealController`.

### `storage.js`
- Quản lý session trong `AsyncStorage`: `saveSession`, `getSession`,
  `clearSession`, `hasValidSession` (kết hợp với `utils/jwt.js` để check
  token còn hạn mà không cần gọi API).

### `utils/jwt.js`
- Tự decode payload JWT (base64url, không cần thư viện `jwt-decode`), đọc
  `exp` để biết token còn hạn hay không — dùng ở `SplashScreen` quyết định
  vào thẳng `Home` hay bắt về `Login`.

---

## 7. Kỹ thuật / pattern đã áp dụng

1. **Session persistence không cần gọi API mỗi lần mở app** — decode JWT cục
   bộ (`utils/jwt.js`) để check hạn, chỉ gọi API khi thực sự thao tác.
2. **`useFocusEffect`** (`@react-navigation/native`) — tự refetch dữ liệu mỗi
   khi quay lại 1 màn hình (Profile, ChatHistory, MyReports,
   AdminReportList) thay vì chỉ fetch 1 lần lúc mount, đảm bảo dữ liệu luôn
   mới sau khi chỉnh sửa ở màn khác.
3. **Real-time 2 chiều bằng SignalR** — không polling, dùng Group theo
   `roomId` để broadcast tin nhắn/typing đúng người trong phòng.
4. **Upload ảnh multipart/form-data** — `react-native-image-picker` lấy
   file từ thư viện ảnh, đóng gói vào `FormData`, `apiRequest` tự nhận diện
   để không set sai `Content-Type`.
5. **Role-based UI** — nút "Admin Reports" trên Home chỉ hiện nếu
   `session.roles.includes('Admin')` (đọc từ claim JWT lúc login).
6. **`enableScreens(false)` trong `App.jsx`** — workaround 1 bug timing giữa
   Fabric (New Architecture của RN 0.86) và `react-native-screens`
   (`FabricUIManager` NullPointerException), tắt native-screens để fallback
   về `View` thường, đổi lại animation chuyển màn không mượt bằng nhưng ổn
   định.
7. **Toàn bộ font family set `monospace`** (`constants/theme.js`) — quyết
   định thiết kế đồng bộ toàn app.
8. **ESLint** theo cấu hình chính thức `@react-native/eslint-config` — chạy
   sạch (0 lỗi) trên toàn bộ code hiện tại.

---

## 8. Đối chiếu với yêu cầu môn học

| Yêu cầu | Đạt |
|---|---|
| Tối thiểu 20 screens | **21 screens** (mục 4) |
| Đăng nhập/Đăng ký | ✅ có kèm activate qua email |
| Kết nối API thật, không mock data | ✅ toàn bộ 21 screens đều gọi API/service thật |
| CRUD (List/Detail/Create/Update/Delete) | ✅ trải trên ChatHistory-Detail (List/Detail), ReportUser (Create), AdminReportDetail (Update), AdminReportList (Delete) |
| Vai trò người dùng khác nhau (User/Admin) | ✅ role-based UI + `[Authorize(Roles="Admin")]` phía backend |

---

## 9. Testing hiện tại (cần lưu ý khi viết report)

`__tests__/App.test.tsx` hiện chỉ là **test mặc định của React Native CLI**
(render `<App/>` kiểm tra không crash) — **chưa có test riêng cho logic
nghiệp vụ mobile** (chưa test service `api.js`, chưa test flow đăng
nhập/chat). Đề bài không bắt buộc phải có automated test cho mobile (chỉ
yêu cầu hoạt động "Test application functions" — có thể làm test thủ công,
xem `DEMO_SCRIPT.md`), nhưng nên biết rõ để không nhỡ nói "có test coverage"
trong report nếu không đúng thực tế.

## 10. Hạn chế đã biết / hướng phát triển tiếp

- Không tự động "vào lại phòng chat" khi mở lại app giữa phiên chat dở —
  quyết định thiết kế có chủ đích (xem giải thích trong `Backend/README.md`
  mục 9.5), không phải thiếu sót.
- Nội dung tin nhắn không lưu trữ vĩnh viễn (bị xoá khi phòng đóng) — đúng
  tinh thần "chat ẩn danh", không phải bug.
- Chưa có cơ chế refresh token — JWT hết hạn sau 3 giờ phải đăng nhập lại
  thủ công.
- `Frontend/Chat` (web) chỉ là công cụ test nội bộ do 1 thành viên làm thêm,
  không thuộc phạm vi bài nộp Mobile, không có luồng auth đầy đủ.
