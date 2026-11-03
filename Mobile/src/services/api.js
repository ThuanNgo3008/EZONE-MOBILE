import { API_BASE_URL, FIREBASE_API_KEY } from '../constants/config';
import { getSession } from './storage';

export async function apiRequest(endpoint, options = {}) {
  const { token } = await getSession();

  // FormData tự set Content-Type (kèm boundary) khi fetch gửi đi — nếu ép
  // "application/json" đè lên thì backend không parse được multipart nữa.
  const isFormData = options.body instanceof FormData;

  const headers = {
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...(options.body && !isFormData ? { 'Content-Type': 'application/json' } : {}),
    ...(options.headers || {}),
  };

  const response = await fetch(`${API_BASE_URL}${endpoint}`, {
    ...options,
    headers,
  });

  const text = await response.text();
  let data;

  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }

  if (!response.ok) {
    const message = typeof data === 'string' ? data : data?.message;
    throw new Error(message || 'Request failed');
  }

  return data;
}

export function login(email, password) {
  return apiRequest('/Auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  });
}


export async function firebaseLogin(email, password) {
  // 1. Đăng nhập bằng Firebase Authentication
  const authResponse = await fetch(
    `https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=${FIREBASE_API_KEY}`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        email: email.trim(),
        password,
        returnSecureToken: true,
      }),
    },
  );

  const authData = await authResponse.json();

  if (!authResponse.ok) {
    throw new Error('Email hoặc mật khẩu không chính xác.');
  }

  // 2. Backend kiểm tra trạng thái tài khoản
  const sessionResponse = await fetch(
    `${API_BASE_URL}/firebase-session/me`,
    {
      method: 'GET',
      headers: {
        Authorization: `Bearer ${authData.idToken}`,
      },
    },
  );

  const sessionData = await sessionResponse.json();

  if (!sessionResponse.ok) {
    throw new Error(
      sessionData?.message ||
      'Tài khoản chưa được phép truy cập EZone.',
    );
  }

  // 3. Trả về phiên đăng nhập Firebase
  return {
    token: authData.idToken,
    refreshToken: authData.refreshToken,
    expiresIn: Number(authData.expiresIn),
    userId: sessionData.userId,
    fullname: sessionData.fullname,
    roles: [],
  };
}


export async function refreshFirebaseToken(refreshToken) {
  if (!refreshToken) {
    throw new Error('Phiên đăng nhập không hợp lệ.');
  }

  const response = await fetch(
    `https://securetoken.googleapis.com/v1/token?key=${FIREBASE_API_KEY}`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
      },
      body:
        'grant_type=refresh_token&refresh_token=' +
        encodeURIComponent(refreshToken),
    },
  );

  const data = await response.json();

  if (!response.ok) {
    throw new Error(
      'Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.',
    );
  }

  return {
    token: data.id_token,
    refreshToken: data.refresh_token,
    expiresIn: Number(data.expires_in),
    userId: data.user_id,
  };
}



export function registerUser({ fullname, email, password, majorCode }) {
  return apiRequest('/Users/register', {
    method: 'POST',
    body: JSON.stringify({
      Fullname: fullname,
      Email: email,
      Password: password,
      MajorCode: majorCode,
    }),
  });
}

export function activateAccount({ email, code }) {
  return apiRequest('/Users/activate', {
    method: 'POST',
    body: JSON.stringify({ Email: email, Code: code }),
  });
}

export function createReport({ roomId, reporterId, violatingMessage, reason }) {
  return apiRequest('/ChatReports', {
    method: 'POST',
    body: JSON.stringify({
      RoomId: Number(roomId),
      ReporterId: Number(reporterId),
      ReportedUserId: 0,
      ViolatingMessage: violatingMessage || 'Reported from mobile app',
      Reason: reason,
    }),
  });
}

export function getMyReports() {
  return apiRequest('/ChatReports/my');
}

export function getAdminReports({ page = 1, pageSize = 10, status = '', sortOrder = 'desc' } = {}) {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    sortBy: 'createdAt',
    sortOrder,
  });

  if (status) params.set('status', status);

  return apiRequest(`/ChatReports?${params.toString()}`);
}

export function banReportedUser(reportId) {
  return apiRequest(`/ChatReports/${reportId}/ban`, { method: 'POST' });
}

export function updateReportStatus(reportId, status) {
  return apiRequest(`/ChatReports/${reportId}`, {
    method: 'PUT',
    body: JSON.stringify({ Status: status }),
  });
}

export function deleteReport(reportId) {
  return apiRequest(`/ChatReports/${reportId}`, { method: 'DELETE' });
}

export function logoutRequest() {
  return apiRequest('/Auth/logout', { method: 'POST' });
}

// Soft delete tài khoản — backend bắt xác thực lại mật khẩu trước khi xóa,
// chỉ đánh dấu IsDeleted chứ không xóa data liên quan.
export function deleteAccount(userId, password) {
  return apiRequest(`/Users/${userId}`, {
    method: 'DELETE',
    body: JSON.stringify({ Password: password }),
  });
}

export function getMyProfile() {
  return apiRequest('/Users/me');
}

// ChatRooms.Status là enum bên backend (Waiting/Active/Disconnected/Closed/Expired),
// không có JsonStringEnumConverter nên JSON trả về là số — map lại để hiển thị.
export const ROOM_STATUS_LABELS = ['Waiting', 'Active', 'Disconnected', 'Closed', 'Expired'];

export function getChatHistory(userId) {
  return apiRequest(`/ChatRooms/history/${userId}`);
}

export function getChatRoomDetail(roomId) {
  return apiRequest(`/ChatRooms/${roomId}`);
}

export function getMessages(roomId) {
  return apiRequest(`/Messages/${roomId}`);
}


export function updateProfile(userId, { fullname, gender, majorCode, socialLink, avatarFile }) {
  const form = new FormData();

  form.append('Fullname', fullname ?? '');
  form.append('Gender', gender ?? '');
  form.append('MajorCode', majorCode ?? '');
  form.append('SocialLink', socialLink ?? '');

  if (avatarFile) {
    form.append('AvatarFile', {
      uri: avatarFile.uri,
      type: avatarFile.type || 'image/jpeg',
      name: avatarFile.fileName || 'avatar.jpg',
    });
  }

  return apiRequest(`/Users/${userId}`, {
    method: 'PUT',
    body: form,
  });
}
