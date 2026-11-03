
import { firebaseLogin } from './api';
import {
    saveFirebaseSession,
    clearFirebaseSession,
} from './firebaseStorage';
import { refreshFirebaseToken } from './api';
import { getFirebaseRefreshToken } from './firebaseStorage';
import { API_BASE_URL } from '../constants/config';


export async function signInWithFirebase(email, password) {
    // Đăng nhập và kiểm tra trạng thái tài khoản qua backend
    const session = await firebaseLogin(email, password);

    if (!session.refreshToken) {
        throw new Error('Firebase refresh token is missing.');
    }

    // Chỉ lưu Refresh Token sau khi backend cho phép đăng nhập
    try {
        await saveFirebaseSession(session.refreshToken);
    } catch (error) {
        await clearFirebaseSession();
        throw error;
    }

    return {
        userId: session.userId,
        fullname: session.fullname,
        token: session.token,
    };
}

export async function restoreFirebaseSession() {
    // 1. Đọc Refresh Token từ Keychain
    const storedToken = await getFirebaseRefreshToken();

    if (!storedToken) {
        return null;
    }

    // 2. Yêu cầu Firebase cấp ID Token mới
    const refreshed = await refreshFirebaseToken(storedToken);

    // 3. Backend kiểm tra trạng thái tài khoản
    const response = await fetch(
        `${API_BASE_URL}/firebase-session/me`,
        {
            method: 'GET',
            headers: {
                Authorization: `Bearer ${refreshed.token}`,
            },
        },
    );

    if (!response.ok) {
        if ([401, 403, 404].includes(response.status)) {
            await clearFirebaseSession();
        }

        throw new Error('Không thể khôi phục phiên đăng nhập.');
    }

    const user = await response.json();

    // 4. Cập nhật Refresh Token nếu Firebase cấp token mới
    if (
        refreshed.refreshToken &&
        refreshed.refreshToken !== storedToken
    ) {
        await saveFirebaseSession(refreshed.refreshToken);
    }

    // 5. Trả về phiên đăng nhập
    return {
        token: refreshed.token,
        userId: user.userId,
        fullname: user.fullname,
    };
}


export async function signOutFirebase() {
    await clearFirebaseSession();
}

