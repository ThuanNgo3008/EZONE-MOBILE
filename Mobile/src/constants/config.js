// Android Emulator dùng 10.0.2.2 để trỏ về localhost của máy tính.
// Backend của bạn có HTTP port 5044 trong launchSettings.json.
// Nếu chạy trên điện thoại thật: đổi thành http://IP_MAY_TINH:5044
export const BASE_URL = 'http://10.0.2.2:5044';
export const API_BASE_URL = `${BASE_URL}/api`;
export const HUB_URL = `${BASE_URL}/chatHub`;
export const FIREBASE_API_KEY = 'AIzaSyBvnQ92IG9KZugc2KerMRCzPiAZ2A1-nKU';