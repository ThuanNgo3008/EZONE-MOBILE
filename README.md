# EZone — Anonymous Chat (Mobile)

Ứng dụng chat ẩn danh dành riêng cho sinh viên EIU (`@eiu.edu.vn`), sử dụng Firebase Authentication, Firestore và SignalR. Backend hiện hỗ trợ ghép cặp, xác nhận Ready, chat tạm thời, quyết định tiếp tục và báo cáo vi phạm.

## Tính năng chính

- Firebase registration, email verification, and session validation.
- Random Match with heartbeat, reservations, and two-sided ready confirmation.
- Five-minute temporary chat and two-minute continuation decisions.
- SignalR chat, Firestore persistence, moderation, reporting, and evidence preservation.
- Background expiration and cleanup processing.

The existing web/mobile clients require rebuilding against the current backend.
See [the backend API documentation](Backend/README.md) for supported operations.

## Tech stack

**Backend** — `Backend/`
- ASP.NET Core Web API (.NET)
- Firebase Authentication + Google Cloud Firestore
- SignalR (real-time chat)
- Firebase ID Token validation (JWT bearer)
- Swagger (API docs)

**Frontend (Web)** — `Frontend/Chat/`
- React (Vite) + Tailwind/Radix UI
- `@microsoft/signalr` (client kết nối ChatHub)

**Mobile** — `Mobile/`
- React Native (khởi tạo bằng **React Native CLI thuần**, không dùng Expo Go)
- React Navigation (điều hướng)
- `@microsoft/signalr` (client kết nối ChatHub)
- AsyncStorage (lưu token/session)

## Cấu trúc thư mục

```
EZone/
├── Backend/
│   ├── WebChatEIU/           # ASP.NET Core Web API + SignalR Hub
│   └── WebChatEIU.Tests/     # unit test backend
├── Frontend/
│   └── Chat/                 # source code web (React)
├── Mobile/                   # source code React Native
├── README_Mobile.md          # kế hoạch task chi tiết (backend bổ sung + danh sách screen mobile)
└── README.md                 # file này
```

## Hướng dẫn chạy dự án

### Backend
```bash
cd Backend/WebChatEIU
dotnet restore
dotnet run
```
See [Backend setup and current API](Backend/README.md) for Firebase credentials, email configuration, and runtime safety. Starting the API also starts its Firestore worker.

### Frontend (Web)
```bash
cd Frontend/Chat
npm install
npm run dev
```

### Mobile
```bash
cd Mobile
npm install
npx react-native run-android
```
Cần cài Android SDK + máy ảo (hoặc thiết bị thật) chạy sẵn. Base URL của backend cần trỏ tới IP LAN của máy chạy backend (không dùng `localhost` vì mobile không chạy chung máy với backend).

